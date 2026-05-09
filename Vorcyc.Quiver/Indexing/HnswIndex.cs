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

        // Read the vector from the store for graph construction.
        // 复制为独立数组：对 Float16 / SQ8 等"边界解码"型 store，Get 返回的是线程局部 widen/decode 缓冲，
        // 其有效期只到本线程下一次 Get；而下面的 SearchLayer 会反复 Get(neighbor) 覆盖该缓冲，
        // 因此必须先固化当前向量（Float32 heap store 的 ToArray 仅一次小拷贝，开销可忽略）。
        var vector = _vectorStore.Get(id).ToArray();

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
    /// 批量构建：把一批 id 高效地插入图中，并行化最昂贵的"邻居搜索"阶段。适用于把字段从 Flat
    /// 切换到 HNSW 后的首次全量重建。
    /// <para>
    /// <b>并发模型（分波次：并行只读搜索 + 串行提交）</b>：
    /// <list type="number">
    ///   <item>串行预生成每个新节点的随机层级（<see cref="RandomLevel"/> 使用的 <see cref="Random"/> 非线程安全）。</item>
    ///   <item>按波次处理。每个波次内，所有新节点针对"已提交、当前不可变的图"并行执行 <see cref="SearchLayer"/>
    ///         （纯只读，零数据竞争），各自得出每层应连接的邻居候选。</item>
    ///   <item>串行提交本波次：创建节点、建立双向连接并按需剪枝，然后更新入口点 / 最高层。</item>
    /// </list>
    /// 同一波次内的节点彼此不互联（搜索时它们尚未提交），波次大小被刻意控制得较小以将这种近似损失降到最低；
    /// HNSW 本身是近似算法，召回基本不受影响。
    /// </para>
    /// </summary>
    /// <param name="ids">待插入的内部 ID 列表，其向量已写入 <see cref="IVectorStore"/>。</param>
    /// <param name="degreeOfParallelism">最大并行度；<see langword="null"/> 取 <see cref="Environment.ProcessorCount"/>，小于等于 1 时退化为串行。</param>
    public void BuildBulk(IReadOnlyList<int> ids, int? degreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;

        int dop = degreeOfParallelism ?? Environment.ProcessorCount;

        // 小数据量或不并行时直接走串行 Add，行为与逐条插入完全一致。
        if (dop <= 1 || ids.Count < 2)
        {
            for (int i = 0; i < ids.Count; i++) Add(ids[i]);
            return;
        }

        // 阶段 1：串行预生成层级（Random 非线程安全）。
        var entries = new (int Id, int Level)[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            entries[i] = (ids[i], RandomLevel());

        int start = 0;

        // 空图时先串行播种第一个节点作为入口点。
        if (_entryPointId == -1)
        {
            var (fid, flevel) = entries[0];
            _nodes[fid] = new HnswNode(fid, flevel);
            _entryPointId = fid;
            _maxLevel = flevel;
            start = 1;
        }

        // 波次大小：兼顾并行度与"同波次节点不互联"带来的近似损失。
        // 取较大的波次以摊薄波次间同步屏障与 Parallel 调度开销（对高核数尤为重要），
        // 相对百万级数据，每波数千节点的近似损失对召回可忽略。
        int chunkSize = Math.Max(dop * 32, 256);
        var parallelOptions = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = dop };

        for (int batchStart = start; batchStart < entries.Length; batchStart += chunkSize)
        {
            int batchEnd = Math.Min(batchStart + chunkSize, entries.Length);
            int batchCount = batchEnd - batchStart;

            // 冻结本波次的入口点与最高层；并行搜索阶段图保持不变。
            int epFrozen = _entryPointId;
            int maxLevelFrozen = _maxLevel;

            // 每个元素保存：该节点各层选中的邻居 id 列表（索引 = 层号）。
            var selectedByLevel = new List<int>[batchCount][];

            // 阶段 2：并行邻居搜索（只读，针对冻结图）。
            System.Threading.Tasks.Parallel.For(0, batchCount, parallelOptions, k =>
            {
                var (id, level) = entries[batchStart + k];

                // ReadOnlySpan 是 ref struct，不能跨方法/闭包持有；物化为 float[]。
                var vector = _vectorStore.Get(id).ToArray();

                var ep = epFrozen;

                // 上层贪心导航（ef=1），快速定位区域。
                for (int l = maxLevelFrozen; l > level; l--)
                {
                    var nearest = SearchLayer(vector, ep, 1, l);
                    if (nearest.Count > 0)
                        ep = nearest.MaxBy(x => x.Similarity).Id;
                }

                var perLevel = new List<int>[level + 1];
                for (int l = Math.Min(level, maxLevelFrozen); l >= 0; l--)
                {
                    int mMax = l == 0 ? _mMax0 : _m;
                    var candidates = SearchLayer(vector, ep, _efConstruction, l);
                    var selected = candidates
                        .OrderByDescending(x => x.Similarity)
                        .Take(mMax)
                        .ToList();

                    perLevel[l] = [.. selected.Select(x => x.Id)];

                    if (selected.Count > 0)
                        ep = selected[0].Id;
                }

                selectedByLevel[k] = perLevel;
            });

            // 阶段 3：串行提交本波次——只做廉价的加边，把昂贵的剪枝推迟到并行阶段。
            // 串行段内联剪枝会让搜索结束后所有线程空等单核做 PruneConnections（相似度计算+排序），
            // 是多核利用率上不去的主因。这里改为：先无剪枝地建立双向连接，记录可能超限的邻居，
            // 随后在阶段 4 并行修剪这些节点。
            var overflowNodes = new HashSet<int>();
            for (int k = 0; k < batchCount; k++)
            {
                var (id, level) = entries[batchStart + k];
                var node = new HnswNode(id, level);
                _nodes[id] = node;

                var perLevel = selectedByLevel[k];
                for (int l = Math.Min(level, maxLevelFrozen); l >= 0; l--)
                {
                    int mMax = l == 0 ? _mMax0 : _m;
                    var neighborIds = perLevel[l];
                    if (neighborIds is null) continue;

                    foreach (var neighborId in neighborIds)
                    {
                        // 双向连接：新节点 → 邻居（新节点本层至多 mMax 个，永不超限，无需修剪）
                        node.Neighbors[l].Add(neighborId);

                        if (!_nodes.TryGetValue(neighborId, out var neighborNode)) continue;

                        // 双向连接：邻居 → 新节点（仅 List.Add，廉价）
                        neighborNode.Neighbors[l].Add(id);

                        // 超过上限的邻居推迟修剪
                        if (neighborNode.Neighbors[l].Count > mMax)
                            overflowNodes.Add(neighborId);
                    }
                }

                // 新节点层级超过当前最高层时更新全局入口点
                if (level > _maxLevel)
                {
                    _entryPointId = id;
                    _maxLevel = level;
                }
            }

            // 阶段 4：并行修剪本波次被追加反向边而超限的节点。
            // 每个任务只读取向量、只改写自身节点的邻居表（节点 id 互不相同），与阶段 2 一样零数据竞争。
            // overflowNodes 全部来自本波次之前已提交的节点（搜索基于冻结图，邻居只引用旧节点），
            // 因此并行修剪期间没有任何对 _nodes 的写入。
            if (overflowNodes.Count > 0)
            {
                System.Threading.Tasks.Parallel.ForEach(overflowNodes, parallelOptions, nid =>
                {
                    if (!_nodes.TryGetValue(nid, out var nn)) return;
                    for (int l = 0; l < nn.Neighbors.Length; l++)
                    {
                        int cap = l == 0 ? _mMax0 : _m;
                        if (nn.Neighbors[l].Count > cap)
                            PruneConnections(nn, l, cap);
                    }
                });
            }
        }
    }
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
    /// 与底层 <see cref="IVectorStore"/> 对账：移除所有"节点存在于图中、但对应向量已不在 store"的悬空节点。
    /// <para>
    /// 主要用于从快照恢复后修复非正常退出造成的不一致：磁盘上的 <c>IndexSnapshot</c> 段可能引用了某些 id，
    /// 但这些 id 的向量并未真正落盘 / 绑定到 store（实体被 tombstone、向量为 null、或写盘中途崩溃）。
    /// 若不清理，后续 <see cref="Add"/> / <see cref="Search"/> 在 <see cref="SearchLayer"/> 沿邻居遍历到这些
    /// 节点并执行 <c>_vectorStore.Get(id)</c> 时会抛 <see cref="KeyNotFoundException"/>
    /// （例如 "Vector id N not found in mmap store."）。
    /// </para>
    /// <para>
    /// 删除节点本身后，残留在其它节点邻居表里的反向引用会被 <see cref="SearchLayer"/> 现有的
    /// <c>_nodes.ContainsKey</c> 守卫与 <see cref="PruneConnections"/> 的有效性过滤自动跳过 / 清理，
    /// 因此无需逐边清扫。若入口点被移除，则重新选举最高层节点作为入口点。
    /// </para>
    /// </summary>
    public void ReconcileWithStore()
    {
        if (_nodes.Count == 0) return;

        // 收集所有向量已缺失的节点 id（先收集再删除，避免遍历时修改字典）。
        List<int>? dangling = null;
        foreach (var id in _nodes.Keys)
        {
            if (!_vectorStore.Contains(id))
                (dangling ??= []).Add(id);
        }

        if (dangling is null) return;

        foreach (var id in dangling)
            _nodes.Remove(id);

        // 修正入口点 / 最高层：图可能已空，或入口点恰被移除。
        if (_nodes.Count == 0)
        {
            _entryPointId = -1;
            _maxLevel = -1;
            return;
        }
        if (!_nodes.ContainsKey(_entryPointId))
        {
            var best = _nodes.Values.MaxBy(n => n.MaxLevel)!;
            _entryPointId = best.Id;
            _maxLevel = best.MaxLevel;
        }
    }

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

    #region Snapshot (persistence)

    // 快照二进制格式（小端）：
    //   magic        : 4B "QHNS"
    //   version      : u16 = 1
    //   simName      : string (BinaryWriter.Write)   — TSim 的类型名，用于指纹校验
    //   m            : i32
    //   mMax0        : i32
    //   efConstruct  : i32
    //   efSearch     : i32
    //   effectiveDim : i32                            — 与 VectorStore.EffectiveDim 匹配
    //   entryPointId : i32
    //   maxLevel     : i32
    //   nodeCount    : i32
    //   coveredNext  : i32                            — 快照覆盖的 id 上界（不含）
    //   nodes        : repeat nodeCount
    //       id           : i32
    //       nodeMaxLevel : i32
    //       layers       : repeat (nodeMaxLevel+1)
    //           neighborCount : i32
    //           neighborIds   : i32[neighborCount]
    private const uint SnapshotMagic = 0x534E_4851u; // 'Q','H','N','S' little-endian
    private const ushort SnapshotVersion = 1;

    /// <inheritdoc />
    public bool TrySaveSnapshot(System.IO.BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(SnapshotMagic);
        writer.Write(SnapshotVersion);
        writer.Write(typeof(TSim).FullName ?? typeof(TSim).Name);
        writer.Write(_m);
        writer.Write(_mMax0);
        writer.Write(_efConstruction);
        writer.Write(_efSearch);
        writer.Write(_vectorStore.EffectiveDim);
        writer.Write(_entryPointId);
        writer.Write(_maxLevel);
        writer.Write(_nodes.Count);

        int coveredNext = 0;
        foreach (var id in _nodes.Keys)
        {
            if (id >= coveredNext) coveredNext = id + 1;
        }
        writer.Write(coveredNext);

        foreach (var kv in _nodes)
        {
            var node = kv.Value;
            writer.Write(node.Id);
            writer.Write(node.MaxLevel);
            for (int layer = 0; layer <= node.MaxLevel; layer++)
            {
                var nbrs = node.Neighbors[layer];
                writer.Write(nbrs.Count);
                for (int i = 0; i < nbrs.Count; i++)
                    writer.Write(nbrs[i]);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryLoadSnapshot(System.IO.BinaryReader reader, out int snapshotCoveredNextId)
    {
        snapshotCoveredNextId = 0;
        ArgumentNullException.ThrowIfNull(reader);

        try
        {
            var magic = reader.ReadUInt32();
            if (magic != SnapshotMagic) return false;
            var version = reader.ReadUInt16();
            if (version != SnapshotVersion) return false;

            var simName = reader.ReadString();
            var expectedSim = typeof(TSim).FullName ?? typeof(TSim).Name;
            if (!string.Equals(simName, expectedSim, StringComparison.Ordinal)) return false;

            int m = reader.ReadInt32();
            int mMax0 = reader.ReadInt32();
            int efC = reader.ReadInt32();
            int efS = reader.ReadInt32();
            int dim = reader.ReadInt32();

            // 仅当与当前实例的关键参数一致时才接受快照。efSearch 是运行时可调参数，因此不强制一致。
            if (m != _m || mMax0 != _mMax0 || efC != _efConstruction) return false;
            if (dim != _vectorStore.EffectiveDim) return false;
            _ = efS; // 接受但不覆盖；保留构造时设定的 _efSearch。

            int entryPoint = reader.ReadInt32();
            int maxLevel = reader.ReadInt32();
            int nodeCount = reader.ReadInt32();
            int coveredNext = reader.ReadInt32();

            if (nodeCount < 0 || coveredNext < 0) return false;

            // 先准备一个临时字典，全部读取成功后再原子接管。
            var tmpNodes = new Dictionary<int, HnswNode>(nodeCount);
            for (int n = 0; n < nodeCount; n++)
            {
                int id = reader.ReadInt32();
                int nodeMax = reader.ReadInt32();
                if (nodeMax < 0) return false;
                var node = new HnswNode(id, nodeMax);
                for (int layer = 0; layer <= nodeMax; layer++)
                {
                    int nbrCount = reader.ReadInt32();
                    if (nbrCount < 0) return false;
                    var list = node.Neighbors[layer];
                    if (nbrCount > 0)
                    {
                        if (list.Capacity < nbrCount) list.Capacity = nbrCount;
                        for (int i = 0; i < nbrCount; i++)
                            list.Add(reader.ReadInt32());
                    }
                }
                tmpNodes[id] = node;
            }

            _nodes.Clear();
            foreach (var kv in tmpNodes) _nodes[kv.Key] = kv.Value;
            _entryPointId = entryPoint;
            _maxLevel = maxLevel;
            snapshotCoveredNextId = coveredNext;
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    #endregion
}