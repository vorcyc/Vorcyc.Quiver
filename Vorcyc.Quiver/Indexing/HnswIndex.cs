namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// HNSW（Hierarchical Navigable Small World）索引：分层可导航小世界图，用于近似最近邻搜索。
/// <para>
/// <b>核心思想</b>：构建一个多层的近邻图。高层稀疏、跨度大，用于快速定位目标区域；
/// 低层稠密、连接多，用于精细搜索。类似"高速公路 → 省道 → 乡道"的分层导航。
/// </para>
/// <para>
/// <b>性能特征</b>：
/// <list type="bullet">
///   <item>搜索复杂度：O(log n)，n 为向量数量</item>
///   <item>插入复杂度：O(log n) × efConstruction</item>
///   <item>空间复杂度：O(n × M)，M 为每层最大连接数</item>
/// </list>
/// </para>
/// <para>
/// <b>参数调优指南</b>：
/// <list type="bullet">
///   <item><c>M</c>：每层最大邻居数。增大提高召回率但增加内存和构建时间。推荐 12~48</item>
///   <item><c>efConstruction</c>：构建时的候选集大小。增大提高图质量但减慢插入。推荐 100~500</item>
///   <item><c>efSearch</c>：搜索时的候选集大小。增大提高召回率但减慢搜索。须 ≥ topK</item>
/// </list>
/// </para>
/// </summary>
/// <seealso href="https://arxiv.org/abs/1603.09320">论文：Efficient and robust approximate nearest neighbor search using HNSW graphs</seealso>
internal sealed class HnswIndex : IVectorIndex
{
    // ──────────────────────────────────────────────────────────────
    // 算法参数（构造后不变）
    // ──────────────────────────────────────────────────────────────

    /// <summary>相似度计算函数，由外部注入（通常为 TensorPrimitives.Dot 或 CosineSimilarity）。</summary>
    private readonly SimilarityFunc _similarityFunc;

    /// <summary>
    /// 每层最大邻居连接数（第 1 层及以上）。
    /// 原论文参数 M，控制图的稠密度。更大的 M = 更高的召回率 + 更多内存。
    /// </summary>
    private readonly int _m;

    /// <summary>
    /// 第 0 层（最底层）的最大连接数，默认为 <c>M × 2</c>。
    /// 底层承载所有节点，需要更多连接来保证搜索质量。
    /// </summary>
    private readonly int _mMax0;

    /// <summary>
    /// 构建阶段的候选集大小。插入新节点时，在每层搜索 efConstruction 个候选邻居。
    /// 越大 → 图质量越高（更准确的邻居选择），但插入越慢。
    /// </summary>
    private readonly int _efConstruction;

    /// <summary>
    /// 层级随机数的缩放因子：<c>1 / ln(M)</c>。
    /// 用于生成指数衰减的层级分布，保证高层节点稀疏、低层节点稠密。
    /// </summary>
    private readonly double _levelMultiplier;

    /// <summary>
    /// 搜索阶段的候选集大小。运行时可调，实际使用 <c>max(efSearch, topK)</c>。
    /// 越大 → 召回率越高，但搜索越慢。
    /// </summary>
    private int _efSearch;

    // ──────────────────────────────────────────────────────────────
    // 图结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>内部 ID → HNSW 节点的映射。每个节点包含向量数据和各层的邻居列表。</summary>
    private readonly Dictionary<int, HnswNode> _nodes = [];

    /// <summary>
    /// 入口点节点 ID。搜索从此节点开始，逐层向下导航。
    /// 空图时为 -1。始终指向最高层级的节点之一。
    /// </summary>
    private int _entryPointId = -1;

    /// <summary>图中当前最高层级。空图时为 -1。</summary>
    private int _maxLevel = -1;

    /// <summary>层级随机数生成器，用于 <see cref="RandomLevel"/> 生成新节点的层级。</summary>
    private readonly Random _rng = new();

    #region 节点定义

    /// <summary>
    /// HNSW 图中的单个节点，存储向量数据和各层级的邻居连接。
    /// </summary>
    /// <param name="id">节点的内部 ID（与 VectorSet 的内部 ID 一致）。</param>
    /// <param name="vector">节点的向量数据（已归一化或已复制）。</param>
    /// <param name="maxLevel">
    /// 该节点存在的最高层级。节点存在于第 0 层到第 maxLevel 层。
    /// 由 <see cref="RandomLevel"/> 随机生成，服从指数衰减分布。
    /// </param>
    private sealed class HnswNode(int id, float[] vector, int maxLevel)
    {
        /// <summary>节点的内部 ID。</summary>
        public readonly int Id = id;

        /// <summary>节点的向量数据。</summary>
        public readonly float[] Vector = vector;

        /// <summary>该节点存在的最高层级。</summary>
        public readonly int MaxLevel = maxLevel;

        /// <summary>
        /// 各层级的邻居 ID 列表。<c>Neighbors[level]</c> 存储该节点在第 <c>level</c> 层的邻居。
        /// 第 0 层最多 <see cref="_mMax0"/> 个邻居，其他层最多 <see cref="_m"/> 个。
        /// </summary>
        public readonly List<int>[] Neighbors = InitNeighbors(maxLevel);

        /// <summary>初始化各层级的空邻居列表。</summary>
        private static List<int>[] InitNeighbors(int maxLevel)
        {
            var arr = new List<int>[maxLevel + 1];
            for (int i = 0; i <= maxLevel; i++) arr[i] = [];
            return arr;
        }
    }

    #endregion

    /// <summary>
    /// 创建 HNSW 索引实例。
    /// </summary>
    /// <param name="similarityFunc">相似度计算函数。</param>
    /// <param name="m">
    /// 每层最大邻居数（第 1 层及以上）。第 0 层自动设为 <c>m × 2</c>。默认 16。
    /// </param>
    /// <param name="efConstruction">构建阶段候选集大小。默认 200。</param>
    /// <param name="efSearch">搜索阶段候选集大小。默认 50。可通过 <see cref="EfSearch"/> 属性运行时调整。</param>
    public HnswIndex(
        SimilarityFunc similarityFunc,
        int m = 16, int efConstruction = 200, int efSearch = 50)
    {
        _similarityFunc = similarityFunc;
        _m = m;
        _mMax0 = m * 2;           // 底层连接数加倍，论文推荐值
        _efConstruction = efConstruction;
        _efSearch = efSearch;
        _levelMultiplier = 1.0 / Math.Log(m);  // 层级缩放因子：ml = 1/ln(M)
    }

    /// <summary>搜索阶段的候选集大小。运行时可调，无需重建索引。</summary>
    public int EfSearch { get => _efSearch; set => _efSearch = value; }

    /// <inheritdoc />
    public int Count => _nodes.Count;

    #region 插入操作

    /// <summary>
    /// 将新向量插入 HNSW 图。算法流程：
    /// <list type="number">
    ///   <item>随机生成新节点的层级 <c>l</c>（指数衰减分布）</item>
    ///   <item>从入口点开始，在高于 <c>l</c> 的层级做贪心搜索，快速定位目标区域</item>
    ///   <item>在第 <c>l</c> 层到第 0 层，用 efConstruction 候选集搜索最佳邻居</item>
    ///   <item>建立双向连接（新节点 ↔ 邻居），超出最大连接数时裁剪</item>
    ///   <item>若新节点层级超过当前最高层，更新入口点</item>
    /// </list>
    /// </summary>
    /// <param name="id">内部 ID。</param>
    /// <param name="vector">向量数据。</param>
    public void Add(int id, float[] vector)
    {
        // 步骤 1：随机生成层级（指数衰减分布，大多数节点在第 0 层）
        var level = RandomLevel();
        var node = new HnswNode(id, vector, level);
        _nodes[id] = node;

        // 空图时，第一个节点直接成为入口点
        if (_entryPointId == -1)
        {
            _entryPointId = id;
            _maxLevel = level;
            return;
        }

        var ep = _entryPointId;

        // 步骤 2：在高于新节点层级的层上做贪心搜索（ef=1），快速靠近目标区域
        // 类似"高速公路"导航，跨度大但精度低
        for (int l = _maxLevel; l > level; l--)
        {
            var nearest = SearchLayer(vector, ep, 1, l);
            if (nearest.Count > 0)
                ep = nearest.MaxBy(x => x.Similarity).Id;
        }

        // 步骤 3-4：在新节点存在的每一层建立邻居连接
        for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
        {
            // 第 0 层允许更多连接（mMax0 = M×2），其他层为 M
            var mMax = l == 0 ? _mMax0 : _m;

            // 用 efConstruction 大小的候选集搜索当前层的最佳邻居
            var candidates = SearchLayer(vector, ep, _efConstruction, l);

            // 选择相似度最高的 mMax 个作为邻居
            var selected = candidates
                .OrderByDescending(x => x.Similarity)
                .Take(mMax)
                .ToList();

            foreach (var neighbor in selected)
            {
                // 建立双向连接：新节点 → 邻居
                node.Neighbors[l].Add(neighbor.Id);

                if (!_nodes.TryGetValue(neighbor.Id, out var neighborNode)) continue;

                // 建立双向连接：邻居 → 新节点
                neighborNode.Neighbors[l].Add(id);

                // 邻居连接数超限时，裁剪保留相似度最高的连接
                if (neighborNode.Neighbors[l].Count > mMax)
                    PruneConnections(neighborNode, l, mMax);
            }

            // 用当前层最佳邻居作为下一层的入口点
            if (selected.Count > 0)
                ep = selected[0].Id;
        }

        // 步骤 5：新节点层级超过当前最高层，更新全局入口点
        if (level > _maxLevel)
        {
            _entryPointId = id;
            _maxLevel = level;
        }
    }

    /// <summary>
    /// 裁剪节点在指定层级的邻居连接。保留相似度最高的 <paramref name="maxConnections"/> 个邻居，
    /// 移除已被删除的无效节点引用。
    /// </summary>
    /// <param name="node">需要裁剪连接的节点。</param>
    /// <param name="level">要裁剪的层级。</param>
    /// <param name="maxConnections">该层允许的最大连接数。</param>
    private void PruneConnections(HnswNode node, int level, int maxConnections)
    {
        node.Neighbors[level] = [.. node.Neighbors[level]
            .Where(nId => _nodes.ContainsKey(nId))                                   // 过滤已删除的无效邻居
            .Select(nId => (Id: nId, Sim: _similarityFunc(node.Vector, _nodes[nId].Vector)))  // 计算相似度
            .OrderByDescending(x => x.Sim)                                           // 按相似度降序
            .Take(maxConnections)                                                     // 保留 Top-N
            .Select(x => x.Id)];
    }

    #endregion

    #region 删除操作

    /// <summary>
    /// 从图中移除节点。采用惰性策略：仅删除节点本身，不清理其他节点中的反向引用。
    /// 残留的无效引用会在后续搜索和裁剪操作中被自动跳过和清理。
    /// <para>
    /// 若删除的是入口点，则重新选择层级最高的节点作为新入口点。
    /// </para>
    /// </summary>
    /// <param name="id">要删除的节点内部 ID。</param>
    public void Remove(int id)
    {
        if (!_nodes.Remove(id)) return;

        // 图为空时重置入口点
        if (_nodes.Count == 0)
        {
            _entryPointId = -1;
            _maxLevel = -1;
            return;
        }

        // 删除的是入口点时，选择层级最高的节点作为新入口点
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

    #region 搜索操作

    /// <summary>
    /// 搜索与查询向量最相似的 Top-K 个节点。算法流程：
    /// <list type="number">
    ///   <item>从入口点出发，在高层（第 maxLevel 层到第 1 层）做贪心搜索（ef=1），快速定位</item>
    ///   <item>在第 0 层（最底层）用 <c>max(efSearch, topK)</c> 大小的候选集做精细搜索</item>
    ///   <item>从候选集中选取相似度最高的 topK 个结果返回</item>
    /// </list>
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_nodes.Count == 0) return [];

        var ep = _entryPointId;

        // 阶段 1：高层贪心导航（ef=1，每层只保留最优节点）
        // 从最高层向下逐层搜索，快速逼近目标区域
        for (int l = _maxLevel; l > 0; l--)
        {
            var nearest = SearchLayer(query, ep, 1, l);
            if (nearest.Count > 0)
                ep = nearest.MaxBy(x => x.Similarity).Id;
        }

        // 阶段 2：底层精细搜索（ef = max(efSearch, topK)）
        // 在第 0 层用较大的候选集做广度优先搜索
        var results = SearchLayer(query, ep, Math.Max(_efSearch, topK), 0);

        // 从候选集中选取 Top-K
        return results
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// 搜索所有相似度不低于阈值的节点。
    /// 通过扩大搜索范围（ef = 数据量的 10%）来提高召回率，再过滤低于阈值的结果。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含）。</param>
    /// <returns>满足阈值条件的 (内部ID, 相似度) 列表。</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        // 扩大 ef 以提高召回率，但不超过总数据量的 10%
        var ef = Math.Max(_efSearch, _nodes.Count / 10);
        return Search(query, ef).Where(x => x.Similarity >= threshold).ToList();
    }

    #endregion

    #region 核心算法 — SearchLayer（单层束搜索）

    /// <summary>
    /// 在指定层级执行束搜索（beam search），返回最多 <paramref name="ef"/> 个最相似的节点。
    /// <para>
    /// 这是 HNSW 的核心算法。使用两个优先队列：
    /// <list type="bullet">
    ///   <item><b>candidates</b>（最大堆，按负相似度排序）：待探索的候选节点，优先探索相似度最高的</item>
    ///   <item><b>results</b>（最小堆，按相似度排序）：当前最优结果集，堆顶为相似度最低的（最差的）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 终止条件：当前最佳候选的相似度低于 results 中最差结果，且 results 已满 ef 个。
    /// 此时继续搜索不会找到更好的结果。
    /// </para>
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="entryPointId">搜索起点节点 ID。</param>
    /// <param name="ef">候选集大小上限。越大 → 搜索越精确，但越慢。</param>
    /// <param name="level">搜索的图层级。</param>
    /// <returns>最多 ef 个最相似节点的 (ID, 相似度) 列表。</returns>
    private List<(int Id, float Similarity)> SearchLayer(
        float[] query, int entryPointId, int ef, int level)
    {
        if (!_nodes.TryGetValue(entryPointId, out var epNode))
            return [];

        // 已访问集合，防止重复计算
        var visited = new HashSet<int> { entryPointId };
        var epSim = _similarityFunc(query, epNode.Vector);

        // candidates：最大堆（用负相似度模拟），优先探索相似度最高的候选
        var candidates = new PriorityQueue<int, float>();
        candidates.Enqueue(entryPointId, -epSim);  // 负值 → 最大堆

        // results：最小堆，堆顶为相似度最低的结果（便于淘汰最差的）
        var results = new PriorityQueue<int, float>();
        results.Enqueue(entryPointId, epSim);       // 正值 → 最小堆

        while (candidates.Count > 0)
        {
            // 取出相似度最高的候选节点
            candidates.TryDequeue(out var currentId, out var negCurrentSim);
            var currentSim = -negCurrentSim;

            // 获取 results 中最差结果的相似度
            results.TryPeek(out _, out var worstResultSim);

            // 终止条件：最佳候选 < 最差结果 且 results 已满
            // 图的贪心特性保证此后不会找到更优结果
            if (currentSim < worstResultSim && results.Count >= ef)
                break;

            if (!_nodes.TryGetValue(currentId, out var currentNode)) continue;
            if (level >= currentNode.Neighbors.Length) continue;

            // 遍历当前节点在该层的所有邻居
            foreach (var neighborId in currentNode.Neighbors[level])
            {
                // 跳过已访问节点（HashSet.Add 返回 false 表示已存在）
                if (!visited.Add(neighborId)) continue;
                if (!_nodes.TryGetValue(neighborId, out var neighborNode)) continue;

                var neighborSim = _similarityFunc(query, neighborNode.Vector);

                results.TryPeek(out _, out var currentWorstSim);

                // 邻居优于 results 中最差结果，或 results 未满时，加入候选和结果
                if (neighborSim > currentWorstSim || results.Count < ef)
                {
                    candidates.Enqueue(neighborId, -neighborSim);
                    results.Enqueue(neighborId, neighborSim);

                    // results 超过 ef 上限时，淘汰最差结果（最小堆堆顶）
                    if (results.Count > ef)
                        results.Dequeue();
                }
            }
        }

        // 将优先队列转为列表返回
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
    /// 生成随机层级，服从指数衰减分布：<c>floor(-ln(uniform(0,1)) × ml)</c>。
    /// <para>
    /// 大多数节点分配在第 0 层（概率最高），高层节点呈指数衰减。
    /// 例如 M=16 时：第 0 层 ≈ 64%，第 1 层 ≈ 23%，第 2 层 ≈ 8%，第 3 层 ≈ 3%...
    /// 这保证了高层的稀疏性（快速跨越搜索）和底层的稠密性（精确检索）。
    /// </para>
    /// </summary>
    /// <returns>非负整数层级。</returns>
    private int RandomLevel()
    {
        return (int)(-Math.Log(1.0 - _rng.NextDouble()) * _levelMultiplier);
    }
}