using Vorcyc.Quiver.Similarity;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// KD-Tree (K-Dimensional Tree) index: a spatial binary partitioning tree for exact nearest-neighbor search.
/// <para>
/// <b>Core idea</b>: alternately partitions the space along each dimension to build a binary search tree.
/// During search, a pruning strategy skips subtrees that cannot contain a closer neighbor, avoiding full traversal.
/// </para>
/// <para>
/// <b>Suitable scenarios</b>:
/// <list type="bullet">
///   <item>Low-dimensional vectors (dimension &lt; 20): O(log n) search, outperforming brute-force scan</item>
///   <item>Exact results required (non-approximate): KD-Tree never misses the nearest neighbor</item>
/// </list>
/// </para>
/// <para>
/// <b>Curse of dimensionality</b>: when dimension exceeds ~20, pruning effectiveness drops sharply and nearly every subtree
/// must be visited, degrading to O(n) brute-force search. Use <see cref="HnswIndex{TSim}"/> for high-dimensional scenarios.
/// </para>
/// <para>
/// <b>Lazy build</b>: the tree is built on the first search. After each <see cref="Add"/> or <see cref="Remove"/>, the tree
/// is marked for rebuild and automatically reconstructed from scratch on the next search
/// (static rebuild strategy — simple, but not ideal for frequent insert/delete workloads).
/// </para>
/// </summary>
/// <typeparam name="TSim">Similarity algorithm type; must be a struct to enable JIT specialization.</typeparam>
internal sealed class KDTreeIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    /// <summary>Vector data store, injected externally.</summary>
    private readonly IVectorStore _vectorStore;

    /// <summary>Set of internal IDs registered in the index. Vector data is managed by <see cref="_vectorStore"/>.</summary>
    private readonly HashSet<int> _ids = [];

    /// <summary>Root node of the KD-Tree. <c>null</c> when the collection is empty or the tree has not yet been built.</summary>
    private KDNode? _root;

    /// <summary>Whether the tree has been built. Set to false after Add/Remove; triggers a rebuild on the next search.</summary>
    private bool _isBuilt;

    #region Node definition

    /// <summary>
    /// A node in the KD-Tree. Each node stores an internal ID and split information, partitioning the space along one dimension.
    /// Vector data is managed centrally by <see cref="IVectorStore"/>; nodes do not hold vector references.
    /// <para>
    /// Structure example (3D space, cycling through x → y → z splits):
    /// <code>
    ///          [x=5]          ← root node, split along x axis
    ///         /     \
    ///      [y=3]   [y=7]     ← depth 1, split along y axis
    ///      /  \    /  \
    ///   [z=1] ... ...  ...   ← depth 2, split along z axis
    /// </code>
    /// </para>
    /// </summary>
    private sealed class KDNode
    {
        /// <summary>The internal ID of the vector stored at this node.</summary>
        public int Id;

        /// <summary>
        /// Split dimension (0-based). Chosen cyclically as <c>depth % total dimensions</c>.
        /// </summary>
        public int SplitDimension;

        /// <summary>Split value: the coordinate of this node along <see cref="SplitDimension"/>.</summary>
        public float SplitValue;

        /// <summary>Left subtree: nodes whose coordinate along <see cref="SplitDimension"/> is ≤ <see cref="SplitValue"/>.</summary>
        public KDNode? Left;

        /// <summary>Right subtree: nodes whose coordinate along <see cref="SplitDimension"/> is &gt; <see cref="SplitValue"/>.</summary>
        public KDNode? Right;
    }

    #endregion

    /// <summary>
    /// Creates a KD-Tree index instance.
    /// </summary>
    /// <param name="vectorStore">Vector data store.</param>
    public KDTreeIndex(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <inheritdoc />
    public int Count => _ids.Count;

    /// <inheritdoc />
    /// <remarks>Marks the tree for rebuild after adding. The new vector is not inserted immediately; a full rebuild is triggered on the next search.</remarks>
    public void Add(int id)
    {
        _ids.Add(id);
        _isBuilt = false;
    }

    /// <inheritdoc />
    /// <remarks>Marks the tree for rebuild after removal.</remarks>
    public void Remove(int id)
    {
        if (_ids.Remove(id))
            _isBuilt = false;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _ids.Clear();
        _root = null;
        _isBuilt = false;
    }

    /// <summary>
    /// Searches for the Top-K results most similar to the query vector, using KD-Tree spatial pruning to avoid visiting all nodes.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of (internal ID, similarity) pairs sorted by similarity in descending order.</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_ids.Count == 0) return [];

        EnsureBuilt();

        var results = new PriorityQueue<int, float>();
        float worstSim = float.MinValue;

        SearchNode(_root, query, topK, results, ref worstSim);

        var output = new List<(int Id, float Similarity)>(results.Count);
        while (results.Count > 0)
        {
            results.TryDequeue(out var id, out var sim);
            output.Add((id, sim));
        }

        output.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return output;
    }

    /// <summary>
    /// Searches for all vectors whose similarity is at or above the given threshold.
    /// KD-Tree pruning cannot be applied directly to threshold searches, so this falls back to brute-force traversal of all vectors.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="threshold">Similarity lower bound (inclusive).</param>
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

    #region KD-Tree construction

    /// <summary>
    /// Ensures the KD-Tree is built. Triggers a full rebuild on the first search or after data changes.
    /// </summary>
    private void EnsureBuilt()
    {
        if (_isBuilt && _root != null) return;

        // Materialize vectors from the store for sorting and splitting
        var items = _ids.Select(id => (Id: id, Vector: _vectorStore.Get(id).ToArray())).ToList();
        _root = BuildTree(items, 0);
        _isBuilt = true;
    }

    /// <summary>
    /// Recursively builds a balanced KD-Tree. At each depth, selects a split dimension and uses the median as the split point.
    /// <para>
    /// Median splitting guarantees a balanced tree (left and right subtrees differ in size by at most 1), giving O(log n) height.
    /// </para>
    /// </summary>
    /// <param name="items">List of (ID, vector) pairs to build from. Vectors are materialized from the store for the duration of construction only.</param>
    /// <param name="depth">Current recursion depth, used to determine the split dimension.</param>
    /// <returns>The subtree root node; <c>null</c> when the list is empty.</returns>
    private static KDNode? BuildTree(List<(int Id, float[] Vector)> items, int depth)
    {
        if (items.Count == 0) return null;

        var dim = items[0].Vector.Length;
        var axis = depth % dim;

        items.Sort((a, b) => a.Vector[axis].CompareTo(b.Vector[axis]));
        var mid = items.Count / 2;

        return new KDNode
        {
            Id = items[mid].Id,
            SplitDimension = axis,
            SplitValue = items[mid].Vector[axis],
            Left = BuildTree(items[..mid], depth + 1),
            Right = BuildTree(items[(mid + 1)..], depth + 1)
        };
    }

    #endregion

    #region KD-Tree search (depth-first search with pruning)

    /// <summary>
    /// Recursively searches KD-Tree nodes, maintaining Top-K results in a min-heap and applying spatial pruning.
    /// </summary>
    private void SearchNode(
        KDNode? node,
        float[] query,
        int topK,
        PriorityQueue<int, float> results,
        ref float worstSim)
    {
        if (node == null) return;

        // Read the node's vector from the store and compute similarity
        var sim = TSim.Compute(query, _vectorStore.Get(node.Id));

        if (results.Count < topK)
        {
            results.Enqueue(node.Id, sim);
            if (results.Count == topK)
                results.TryPeek(out _, out worstSim);
        }
        else if (sim > worstSim)
        {
            results.DequeueEnqueue(node.Id, sim);
            results.TryPeek(out _, out worstSim);
        }

        var diff = query[node.SplitDimension] - node.SplitValue;
        var (first, second) = diff <= 0 ? (node.Left, node.Right) : (node.Right, node.Left);

        SearchNode(first, query, topK, results, ref worstSim);

        if (results.Count < topK || Math.Abs(diff) < EstimateRadius(worstSim, query.Length))
            SearchNode(second, query, topK, results, ref worstSim);
    }

    /// <summary>
    /// Converts a similarity score to an estimated Euclidean search radius for KD-Tree spatial pruning.
    /// </summary>
    private static float EstimateRadius(float similarity, int dim)
    {
        var approxDist = MathF.Sqrt(2 * (1 - similarity));
        return approxDist;
    }

    #endregion
}