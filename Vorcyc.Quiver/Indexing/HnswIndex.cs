using Vorcyc.Quiver.Similarity;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// HNSW (Hierarchical Navigable Small World) index: a layered navigable small-world graph for approximate nearest-neighbor search.
/// <para>
/// <b>Core idea</b>: builds a multi-layer proximity graph. Upper layers are sparse with wide reach for fast region location;
/// lower layers are dense with many connections for fine-grained search — analogous to highway → arterial → local road navigation.
/// </para>
/// <para>
/// <b>Performance characteristics</b>:
/// <list type="bullet">
///   <item>Search complexity: O(log n), n = number of vectors</item>
///   <item>Insert complexity: O(log n) × efConstruction</item>
///   <item>Space complexity: O(n × M), M = maximum connections per layer</item>
/// </list>
/// </para>
/// <para>
/// <b>Parameter tuning guide</b>:
/// <list type="bullet">
///   <item><c>M</c>: max neighbors per layer. Larger values improve recall but increase memory and build time. Recommended: 12–48.</item>
///   <item><c>efConstruction</c>: candidate set size during construction. Larger values improve graph quality but slow insertion. Recommended: 100–500.</item>
///   <item><c>efSearch</c>: candidate set size during search. Larger values improve recall but slow search. Must be ≥ topK.</item>
/// </list>
/// </para>
/// </summary>
/// <seealso href="https://arxiv.org/abs/1603.09320">Paper: Efficient and robust approximate nearest neighbor search using HNSW graphs</seealso>
/// <typeparam name="TSim">Similarity algorithm type; must be a struct to enable JIT specialization.</typeparam>
internal sealed class HnswIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    // ──────────────────────────────────────────────────────────────
    // Algorithm parameters (immutable after construction)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Vector data store, injected externally. Nodes no longer hold vectors; they are read via this interface when needed.</summary>
    private readonly IVectorStore _vectorStore;

    /// <summary>
    /// Maximum number of neighbor connections per layer (layer 1 and above).
    /// Paper parameter M; controls graph density. Larger M = higher recall + more memory.
    /// </summary>
    private readonly int _m;

    /// <summary>
    /// Maximum connections for layer 0 (the bottom layer); defaults to <c>M × 2</c>.
    /// The bottom layer hosts all nodes and needs more connections to maintain search quality.
    /// </summary>
    private readonly int _mMax0;

    /// <summary>
    /// Candidate set size during the build phase. When inserting a new node, <c>efConstruction</c> neighbor candidates are searched per layer.
    /// Larger value → higher graph quality (more accurate neighbor selection), but slower insertion.
    /// </summary>
    private readonly int _efConstruction;

    /// <summary>
    /// Level-randomization scale factor: <c>1 / ln(M)</c>.
    /// Produces an exponentially decaying level distribution, keeping upper layers sparse and lower layers dense.
    /// </summary>
    private readonly double _levelMultiplier;

    /// <summary>
    /// Candidate set size during the search phase. Adjustable at runtime; actual value used is <c>max(efSearch, topK)</c>.
    /// Larger value → higher recall, but slower search.
    /// </summary>
    private int _efSearch;

    // ──────────────────────────────────────────────────────────────
    // Graph structure
    // ──────────────────────────────────────────────────────────────

    /// <summary>Mapping of internal ID → HNSW node. Each node stores neighbor lists per layer.</summary>
    private readonly Dictionary<int, HnswNode> _nodes = [];

    /// <summary>
    /// Entry point node ID. Search starts from this node and navigates down layer by layer.
    /// -1 when the graph is empty. Always points to one of the nodes at the highest layer.
    /// </summary>
    private int _entryPointId = -1;

    /// <summary>Current maximum layer in the graph. -1 when the graph is empty.</summary>
    private int _maxLevel = -1;

    /// <summary>Level RNG used by <see cref="RandomLevel"/> to generate a new node's level.</summary>
    private readonly Random _rng = new();

    #region Node definition

    /// <summary>
    /// A single node in the HNSW graph, storing an internal ID and per-layer neighbor connections.
    /// Vector data is managed centrally by <see cref="IVectorStore"/>; the node holds no vector reference.
    /// </summary>
    /// <param name="id">The node's internal ID (matches the internal ID used by QuiverSet).</param>
    /// <param name="maxLevel">
    /// The highest layer this node belongs to. The node exists from layer 0 through layer maxLevel.
    /// Generated randomly by <see cref="RandomLevel"/> following an exponential decay distribution.
    /// </param>
    private sealed class HnswNode(int id, int maxLevel)
    {
        /// <summary>The node's internal ID.</summary>
        public readonly int Id = id;

        /// <summary>The highest layer this node belongs to.</summary>
        public readonly int MaxLevel = maxLevel;

        /// <summary>
        /// Neighbor ID lists per layer. <c>Neighbors[level]</c> stores this node's neighbors at layer <c>level</c>.
        /// Layer 0 allows up to <see cref="_mMax0"/> neighbors; all other layers allow up to <see cref="_m"/>.
        /// </summary>
        public readonly List<int>[] Neighbors = InitNeighbors(maxLevel);

        /// <summary>Initializes empty neighbor lists for each layer.</summary>
        private static List<int>[] InitNeighbors(int maxLevel)
        {
            var arr = new List<int>[maxLevel + 1];
            for (int i = 0; i <= maxLevel; i++) arr[i] = [];
            return arr;
        }
    }

    #endregion

    /// <summary>
    /// Creates an HNSW index instance.
    /// </summary>
    /// <param name="vectorStore">Vector data store.</param>
    /// <param name="m">
    /// Maximum neighbors per layer (layer 1 and above). Layer 0 is automatically set to <c>m × 2</c>. Default: 16.
    /// </param>
    /// <param name="efConstruction">Candidate set size during construction. Default: 200.</param>
    /// <param name="efSearch">Candidate set size during search. Default: 50. Adjustable at runtime via the <see cref="EfSearch"/> property.</param>
    public HnswIndex(
        IVectorStore vectorStore,
        int m = 16, int efConstruction = 200, int efSearch = 50)
    {
        _vectorStore = vectorStore;
        _m = m;
        _mMax0 = m * 2;           // Layer-0 connections doubled, as recommended by the paper
        _efConstruction = efConstruction;
        _efSearch = efSearch;
        _levelMultiplier = 1.0 / Math.Log(m);  // Level scaling factor: ml = 1/ln(M)
    }

    /// <summary>Candidate set size during search. Adjustable at runtime without rebuilding the index.</summary>
    public int EfSearch { get => _efSearch; set => _efSearch = value; }

    /// <inheritdoc />
    public int Count => _nodes.Count;

    #region Insert

    /// <summary>
    /// Inserts a new vector into the HNSW graph. Algorithm steps:
    /// <list type="number">
    ///   <item>Randomly generate the new node's level <c>l</c> (exponential decay distribution)</item>
    ///   <item>Starting from the entry point, perform greedy search on layers above <c>l</c> to quickly locate the target region</item>
    ///   <item>On layers <c>l</c> down to 0, search for the best neighbors using an efConstruction candidate set</item>
    ///   <item>Establish bidirectional connections (new node ↔ neighbors); prune when the connection limit is exceeded</item>
    ///   <item>If the new node's level exceeds the current maximum, update the entry point</item>
    /// </list>
    /// </summary>
    /// <param name="id">Internal ID; the vector has already been written to <see cref="IVectorStore"/>.</param>
    public void Add(int id)
    {
        // Step 1: randomly generate the level (exponential decay distribution; most nodes end up on layer 0)
        var level = RandomLevel();
        var node = new HnswNode(id, level);
        _nodes[id] = node;

        // For an empty graph, the first node becomes the entry point directly
        if (_entryPointId == -1)
        {
            _entryPointId = id;
            _maxLevel = level;
            return;
        }

        // Read the vector from the store for graph construction (ReadOnlySpan is safe in a synchronous context)
        var vector = _vectorStore.Get(id);

        var ep = _entryPointId;

        // Step 2: greedy search (ef=1) on layers above the new node's level — fast highway-style navigation, wide span but low precision
        for (int l = _maxLevel; l > level; l--)
        {
            var nearest = SearchLayer(vector, ep, 1, l);
            if (nearest.Count > 0)
                ep = nearest.MaxBy(x => x.Similarity).Id;
        }

        // Steps 3-4: establish neighbor connections on each layer the new node occupies
        for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
        {
            // Layer 0 allows more connections (mMax0 = M×2); other layers use M
            var mMax = l == 0 ? _mMax0 : _m;

            // Search the best neighbors at the current layer using an efConstruction candidate set
            var candidates = SearchLayer(vector, ep, _efConstruction, l);

            // Select the top-mMax neighbors by similarity
            var selected = candidates
                .OrderByDescending(x => x.Similarity)
                .Take(mMax)
                .ToList();

            foreach (var neighbor in selected)
            {
                // Bidirectional link: new node → neighbor
                node.Neighbors[l].Add(neighbor.Id);

                if (!_nodes.TryGetValue(neighbor.Id, out var neighborNode)) continue;

                // Bidirectional link: neighbor → new node
                neighborNode.Neighbors[l].Add(id);

                // Prune the neighbor's connections when the limit is exceeded, keeping the highest-similarity ones
                if (neighborNode.Neighbors[l].Count > mMax)
                    PruneConnections(neighborNode, l, mMax);
            }

            // Use the best neighbor at the current layer as the entry point for the next layer
            if (selected.Count > 0)
                ep = selected[0].Id;
        }

        // Step 5: if the new node's level exceeds the current maximum, update the global entry point
        if (level > _maxLevel)
        {
            _entryPointId = id;
            _maxLevel = level;
        }
    }

    /// <summary>
    /// Prunes a node's neighbor connections at the specified layer. Retains the top <paramref name="maxConnections"/> neighbors by similarity
    /// and removes references to already-deleted (invalid) nodes.
    /// </summary>
    /// <param name="node">The node whose connections are to be pruned.</param>
    /// <param name="level">The layer to prune.</param>
    /// <param name="maxConnections">Maximum number of connections allowed at this layer.</param>
    private void PruneConnections(HnswNode node, int level, int maxConnections)
    {
        // Materialize to float[] — ReadOnlySpan is a ref struct and cannot be captured in a lambda
        var nodeVector = _vectorStore.Get(node.Id).ToArray();
        node.Neighbors[level] = [.. node.Neighbors[level]
            .Where(nId => _nodes.ContainsKey(nId))                                   // Filter already-deleted invalid neighbors
            .Select(nId => (Id: nId, Sim: TSim.Compute(nodeVector, _vectorStore.Get(nId))))  // Compute similarity
            .OrderByDescending(x => x.Sim)                                           // Sort by similarity descending
            .Take(maxConnections)                                                     // Keep top N
            .Select(x => x.Id)];
    }

    #endregion

    #region Delete

    /// <summary>
    /// Removes a node from the graph using a lazy strategy: only the node itself is deleted;
    /// back-references in other nodes are not cleaned up.
    /// Residual invalid references are automatically skipped and cleaned up during subsequent search and pruning operations.
    /// <para>
    /// If the entry point is deleted, the node with the highest level is elected as the new entry point.
    /// </para>
    /// </summary>
    /// <param name="id">Internal ID of the node to remove.</param>
    public void Remove(int id)
    {
        if (!_nodes.Remove(id)) return;

        // Reset entry point when graph becomes empty
        if (_nodes.Count == 0)
        {
            _entryPointId = -1;
            _maxLevel = -1;
            return;
        }

        // When the deleted node was the entry point, elect the node with the highest level
        if (id == _entryPointId)
        {
            var best = _nodes.Values.MaxBy(n => n.MaxLevel)!;
            _entryPointId = best.Id;
            _maxLevel = best.MaxLevel;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _nodes.Clear();
        _entryPointId = -1;
        _maxLevel = -1;
    }

    #endregion

    #region Search

    /// <summary>
    /// Searches for the Top-K nodes most similar to the query vector. Algorithm steps:
    /// <list type="number">
    ///   <item>Starting from the entry point, perform greedy search (ef=1) on layers maxLevel down to layer 1 for fast targeting</item>
    ///   <item>Perform fine-grained search on layer 0 (the bottom layer) using a candidate set of size <c>max(efSearch, topK)</c></item>
    ///   <item>Select the topK results with the highest similarity from the candidate set</item>
    /// </list>
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of (internal ID, similarity) pairs sorted by similarity in descending order.</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_nodes.Count == 0) return [];

        var ep = _entryPointId;

        // Phase 1: greedy navigation on upper layers (ef=1, keep only the best node per layer)
        // Search down from the highest layer, quickly converging on the target region
        for (int l = _maxLevel; l > 0; l--)
        {
            var nearest = SearchLayer(query, ep, 1, l);
            if (nearest.Count > 0)
                ep = nearest.MaxBy(x => x.Similarity).Id;
        }

        // Phase 2: fine-grained search on the bottom layer (ef = max(efSearch, topK))
        // Breadth-first search on layer 0 with a larger candidate set
        var results = SearchLayer(query, ep, Math.Max(_efSearch, topK), 0);

        // Select Top-K from the candidate set
        return results
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Searches for all nodes whose similarity is at or above the given threshold.
    /// Expands the search range (ef = 10% of dataset size) to improve recall, then filters results below the threshold.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="threshold">Similarity lower bound (inclusive).</param>
    /// <returns>A list of (internal ID, similarity) pairs meeting the threshold condition.</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        // Expand ef to improve recall, capped at 10% of the total dataset size
        var ef = Math.Max(_efSearch, _nodes.Count / 10);
        return Search(query, ef).Where(x => x.Similarity >= threshold).ToList();
    }

    #endregion

    #region Core algorithm — SearchLayer (single-layer beam search)

    /// <summary>
    /// Performs beam search on the specified layer, returning up to <paramref name="ef"/> most similar nodes.
    /// <para>
    /// This is the core HNSW algorithm. It uses two priority queues:
    /// <list type="bullet">
    ///   <item><b>candidates</b> (max-heap via negated similarity): candidate nodes to explore, prioritizing the highest similarity</item>
    ///   <item><b>results</b> (min-heap by similarity): current best result set; the heap top is the worst (lowest similarity) result</item>
    /// </list>
    /// </para>
    /// <para>
    /// Termination condition: the best candidate's similarity is lower than the worst result in <c>results</c> and <c>results</c> already has <c>ef</c> entries.
    /// At that point, no better result can be found by continuing.
    /// </para>
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="entryPointId">Starting node ID for the search.</param>
    /// <param name="ef">Maximum candidate set size. Larger → more precise search, but slower.</param>
    /// <param name="level">Graph layer to search.</param>
    /// <returns>A list of up to <c>ef</c> most similar nodes as (ID, similarity) pairs.</returns>
    private List<(int Id, float Similarity)> SearchLayer(
        ReadOnlySpan<float> query, int entryPointId, int ef, int level)
    {
        if (!_nodes.TryGetValue(entryPointId, out var epNode))
            return [];

        // Visited set to prevent redundant computation
        var visited = new HashSet<int> { entryPointId };
        var epSim = TSim.Compute(query, _vectorStore.Get(epNode.Id));

        // candidates: max-heap (simulated via negated similarity), prioritizing the highest-similarity candidate
        var candidates = new PriorityQueue<int, float>();
        candidates.Enqueue(entryPointId, -epSim);  // Negated value → max-heap

        // results: min-heap; the heap top is the worst (lowest similarity) result, for easy eviction
        var results = new PriorityQueue<int, float>();
        results.Enqueue(entryPointId, epSim);       // Positive value → min-heap

        while (candidates.Count > 0)
        {
            // Dequeue the candidate with the highest similarity
            candidates.TryDequeue(out var currentId, out var negCurrentSim);
            var currentSim = -negCurrentSim;

            // Get the similarity of the worst result in results
            results.TryPeek(out _, out var worstResultSim);

            // Termination: best candidate < worst result and results is full
            // The graph's greedy property guarantees no better result can be found after this point
            if (currentSim < worstResultSim && results.Count >= ef)
                break;

            if (!_nodes.TryGetValue(currentId, out var currentNode)) continue;
            if (level >= currentNode.Neighbors.Length) continue;

            // Traverse all neighbors of the current node at this layer
            foreach (var neighborId in currentNode.Neighbors[level])
            {
                // Skip already-visited nodes (HashSet.Add returns false if already present)
                if (!visited.Add(neighborId)) continue;
                if (!_nodes.ContainsKey(neighborId)) continue;

                var neighborSim = TSim.Compute(query, _vectorStore.Get(neighborId));

                results.TryPeek(out _, out var currentWorstSim);

                // Add to candidates and results if neighbor is better than the worst result or results is not yet full
                if (neighborSim > currentWorstSim || results.Count < ef)
                {
                    candidates.Enqueue(neighborId, -neighborSim);
                    results.Enqueue(neighborId, neighborSim);

                    // Evict the worst result (min-heap top) when results exceeds the ef limit
                    if (results.Count > ef)
                        results.Dequeue();
                }
            }
        }

        // Convert the priority queue to a list and return
        var output = new List<(int, float)>(results.Count);
        while (results.Count > 0)
        {
            results.TryDequeue(out var id, out var sim);
            output.Add((id, sim));
        }
        return output;
    }

    #endregion

    /// <summary>
    /// Generates a random level following an exponential decay distribution: <c>floor(-ln(uniform(0,1)) × ml)</c>.
    /// <para>
    /// Most nodes are assigned to layer 0 (highest probability); upper layers decay exponentially.
    /// For example with M=16: layer 0 ≈ 64%, layer 1 ≈ 23%, layer 2 ≈ 8%, layer 3 ≈ 3%...
    /// This ensures sparseness in upper layers (fast wide-range search) and density in the bottom layer (precise retrieval).
    /// </para>
    /// </summary>
    /// <returns>A non-negative integer level.</returns>
    private int RandomLevel()
    {
        return (int)(-Math.Log(1.0 - _rng.NextDouble()) * _levelMultiplier);
    }
}