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
    public void Add(int id) => _ids.Add(id);

    /// <inheritdoc />
    public void Remove(int id) => _ids.Remove(id);

    /// <inheritdoc />
    public void Clear() => _ids.Clear();

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
        foreach (var id in _ids)
        {
            var sim = TSim.Compute(query, _vectorStore.Get(id));
            if (sim >= threshold)
                results.Add((id, sim));
        }
        return results;
    }

    /// <summary>
    /// Sequential search: single-threaded traversal over all vectors to compute similarity, then LINQ sort to take Top-K.
    /// Suitable when the vector count is ≤ <see cref="ParallelThreshold"/>, avoiding multi-thread scheduling overhead.
    /// </summary>
    private List<(int Id, float Similarity)> SequentialSearchCore(float[] query, int topK)
    {
        var results = new List<(int Id, float Sim)>(_ids.Count);
        foreach (var id in _ids)
            results.Add((id, TSim.Compute(query, _vectorStore.Get(id))));

        return results.OrderByDescending(r => r.Sim).Take(topK).ToList();
    }

    /// <summary>
    /// Parallel search: uses <see cref="Parallel.ForEach"/> to distribute similarity computation across multiple thread-pool threads.
    /// Suitable when the vector count is &gt; <see cref="ParallelThreshold"/>.
    /// <para>
    /// Results are collected via <see cref="ConcurrentBag{T}"/>, which is thread-safe but incurs slight synchronization overhead.
    /// Final sorting is performed on a single thread.
    /// </para>
    /// </summary>
    private List<(int Id, float Similarity)> ParallelSearchCore(float[] query, int topK)
    {
        // Take a snapshot of IDs for parallel traversal (avoids enumerating HashSet across threads)
        var ids = _ids.ToArray();
        var results = new ConcurrentBag<(int Id, float Similarity)>();

        Parallel.ForEach(ids, id =>
        {
            results.Add((id, TSim.Compute(query, _vectorStore.Get(id))));
        });

        return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
    }
}