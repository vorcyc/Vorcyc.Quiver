using System.Collections.Concurrent;
using Vorcyc.Quiver.Similarity;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// Flat index (brute-force search): iterates over all vectors to compute similarity and returns the Top-K results.
/// <para>
/// <b>Exact search</b>: results have no approximation error; 100% recall. Suitable for small datasets or scenarios requiring exact results.
/// </para>
/// <para>
/// <b>Automatic parallelization</b>: when the number of vectors exceeds <see cref="ParallelThreshold"/> (10,000),
/// automatically switches to <see cref="Parallel.ForEach"/> multi-threaded search to fully utilize multi-core CPUs.
/// </para>
/// <para>
/// Time complexity: O(n × d), where n is the number of vectors and d is the vector dimension.
/// </para>
/// <para>
/// The type parameter <typeparamref name="TSim"/> is specialized at JIT compile time;
/// <c>TSim.Compute()</c> is inlined as a direct call (no virtual dispatch, no delegate indirection).
/// </para>
/// </summary>
/// <typeparam name="TSim">Similarity algorithm type; must be a struct to enable JIT specialization.</typeparam>
internal sealed class FlatIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    private readonly IVectorStore _vectorStore;

    /// <summary>Set of internal IDs registered in the index. Vector data is managed by <see cref="_vectorStore"/>.</summary>
    private readonly HashSet<int> _ids = [];

    /// <summary>
    /// Cached snapshot of <see cref="_ids"/> as an array. Built lazily on first search after a mutation,
    /// then reused across every subsequent search until the next Add/Remove/Clear. This avoids the
    /// repeated multi-MB LOH allocation that <c>_ids.ToArray()</c> would otherwise incur per query.
    /// </summary>
    private int[]? _idsSnapshot;

    /// <summary>
    /// Parallel search threshold. When the count exceeds this value, <see cref="Parallel.ForEach"/> multi-threaded search is used.
    /// Below this value, sequential traversal is faster (avoids thread-scheduling and <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/> synchronization overhead).
    /// </summary>
    private const int ParallelThreshold = 10_000;

    internal FlatIndex(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <inheritdoc />
    public int Count => _ids.Count;

    /// <inheritdoc />
    public void Add(int id)
    {
        if (_ids.Add(id)) _idsSnapshot = null;
    }

    /// <inheritdoc />
    public void Remove(int id)
    {
        if (_ids.Remove(id)) _idsSnapshot = null;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _ids.Clear();
        _idsSnapshot = null;
    }

    /// <summary>Returns the cached id snapshot, rebuilding it if a mutation invalidated it.</summary>
    private int[] GetIdsSnapshot()
    {
        var snap = _idsSnapshot;
        if (snap is null || snap.Length != _ids.Count)
        {
            snap = new int[_ids.Count];
            int i = 0;
            foreach (var id in _ids) snap[i++] = id;
            _idsSnapshot = snap;
        }
        return snap;
    }

    /// <summary>
    /// Searches for the Top-K results most similar to the query vector.
    /// Automatically selects sequential or parallel search strategy based on the current vector count.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of (internal ID, similarity) pairs sorted by similarity in descending order.</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_ids.Count == 0) return [];

        // Sequential traversal is faster for small datasets; parallel computation is better for large ones
        return _ids.Count > ParallelThreshold
            ? ParallelSearchCore(query, topK)
            : SequentialSearchCore(query, topK);
    }

    /// <summary>
    /// Searches for all vectors whose similarity is at or above the given threshold. The number of results varies with data distribution.
    /// Always uses sequential traversal (threshold search typically requires scanning all data; parallel gains are limited).
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="threshold">Similarity lower bound (inclusive); results below this value are filtered out.</param>
    /// <returns>A list of (internal ID, similarity) pairs meeting the threshold condition, in no particular order.</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        var results = new List<(int Id, float Similarity)>();
        var ids = GetIdsSnapshot();
        for (int i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var sim = TSim.Compute(query, _vectorStore.Get(id));
            if (sim >= threshold)
                results.Add((id, sim));
        }
        return results;
    }

    /// <summary>
    /// Sequential search: single-threaded traversal using a bounded min-heap of size <paramref name="topK"/>.
    /// Allocates only O(topK) memory regardless of dataset size — no intermediate list of all candidates,
    /// no full sort. Suitable when the vector count is ≤ <see cref="ParallelThreshold"/>.
    /// </summary>
    private List<(int Id, float Similarity)> SequentialSearchCore(float[] query, int topK)
    {
        var heap = new TopKHeap(topK);
        var ids = GetIdsSnapshot();
        for (int i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            heap.Push(id, TSim.Compute(query, _vectorStore.Get(id)));
        }
        return heap.DrainDescending();
    }

    /// <summary>
    /// Parallel search: partitions the ID set across worker threads. Each worker maintains a thread-local
    /// bounded min-heap of size <paramref name="topK"/> while scanning its partition, then a single-threaded
    /// merge step combines the per-thread heaps into the final Top-K.
    /// <para>
    /// Allocation per query: O(numThreads × topK), independent of dataset size. No <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/>,
    /// no LINQ full-sort, no LOH pressure.
    /// </para>
    /// </summary>
    private List<(int Id, float Similarity)> ParallelSearchCore(float[] query, int topK)
    {
        var ids = GetIdsSnapshot();

        // Capture readonly locals for the closure (avoids field access in the hot loop).
        var store = _vectorStore;
        int k = topK;

        var rangePartitioner = Partitioner.Create(0, ids.Length);

        // Each Parallel.ForEach partition produces a local TopKHeap; we then merge them serially.
        // Using the localInit / localFinally overload avoids any shared synchronization on the hot path.
        var locals = new System.Collections.Concurrent.ConcurrentQueue<TopKHeap>();

        Parallel.ForEach(
            rangePartitioner,
            () => new TopKHeap(k),
            (range, _, local) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var id = ids[i];
                    local.Push(id, TSim.Compute(query, store.Get(id)));
                }
                return local;
            },
            local => locals.Enqueue(local));

        // Merge per-thread heaps into a final heap.
        var merged = new TopKHeap(k);
        foreach (var local in locals)
        {
            foreach (var (id, sim) in local.EnumerateUnordered())
                merged.Push(id, sim);
        }
        return merged.DrainDescending();
    }

    /// <summary>
    /// Fixed-capacity min-heap keyed by similarity, used to maintain the running Top-K.
    /// The smallest similarity sits at the root; once full, a new candidate replaces the root
    /// only when it is strictly greater. No allocations after construction.
    /// </summary>
    private struct TopKHeap
    {
        private readonly int _capacity;
        private readonly int[] _ids;
        private readonly float[] _sims;
        private int _count;

        public TopKHeap(int capacity)
        {
            _capacity = capacity;
            _ids = new int[capacity];
            _sims = new float[capacity];
            _count = 0;
        }

        public readonly int Count => _count;

        public void Push(int id, float sim)
        {
            if (_count < _capacity)
            {
                _ids[_count] = id;
                _sims[_count] = sim;
                _count++;
                SiftUp(_count - 1);
            }
            else if (sim > _sims[0])
            {
                _ids[0] = id;
                _sims[0] = sim;
                SiftDown(0);
            }
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_sims[i] < _sims[parent])
                {
                    (_sims[i], _sims[parent]) = (_sims[parent], _sims[i]);
                    (_ids[i], _ids[parent]) = (_ids[parent], _ids[i]);
                    i = parent;
                }
                else break;
            }
        }

        private void SiftDown(int i)
        {
            int n = _count;
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                int smallest = i;
                if (l < n && _sims[l] < _sims[smallest]) smallest = l;
                if (r < n && _sims[r] < _sims[smallest]) smallest = r;
                if (smallest == i) break;
                (_sims[i], _sims[smallest]) = (_sims[smallest], _sims[i]);
                (_ids[i], _ids[smallest]) = (_ids[smallest], _ids[i]);
                i = smallest;
            }
        }

        /// <summary>Returns the heap entries in descending similarity order as a new list.</summary>
        public List<(int Id, float Similarity)> DrainDescending()
        {
            var result = new List<(int Id, float Similarity)>(_count);
            for (int i = 0; i < _count; i++)
                result.Add((_ids[i], _sims[i]));
            result.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
            return result;
        }

        /// <summary>Enumerates entries in arbitrary (heap) order without allocating.</summary>
        public IEnumerable<(int Id, float Similarity)> EnumerateUnordered()
        {
            for (int i = 0; i < _count; i++)
                yield return (_ids[i], _sims[i]);
        }
    }
}