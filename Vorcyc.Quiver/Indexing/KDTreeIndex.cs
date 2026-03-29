namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// KD-Tree（K-Dimensional Tree）索引：空间二叉划分树，用于精确最近邻搜索。
/// <para>
/// <b>核心思想</b>：沿各维度交替切分空间，构建一棵二叉搜索树。
/// 搜索时利用剪枝策略跳过不可能包含更近邻居的子树，避免全量遍历。
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>低维向量（维度 &lt; 20）：O(log n) 搜索，优于暴力扫描</item>
///   <item>需要精确结果（非近似）：KD-Tree 不会遗漏最近邻</item>
/// </list>
/// </para>
/// <para>
/// <b>维度诅咒</b>：维度超过约 20 时，剪枝效果急剧下降，几乎每个子树都需要访问，
/// 退化为 O(n) 暴力搜索。高维场景应使用 <see cref="HnswIndex"/>。
/// </para>
/// <para>
/// <b>延迟构建</b>：树在首次搜索时构建。每次 <see cref="Add"/> 或 <see cref="Remove"/> 后标记需要重建，
/// 下次搜索时自动重新构建整棵树（静态重建策略，简单但不适合频繁增删场景）。
/// </para>
/// </summary>
internal sealed class KDTreeIndex : IVectorIndex
{
    /// <summary>相似度计算函数，由外部注入。</summary>
    private readonly SimilarityFunc _similarityFunc;

    /// <summary>
    /// 内部 ID → 向量数据的完整映射。作为构建树和阈值搜索的数据源。
    /// KD-Tree 节点直接引用此字典中的向量（不额外复制）。
    /// </summary>
    private readonly Dictionary<int, float[]> _vectors = [];

    /// <summary>KD-Tree 的根节点。空集合或未构建时为 <c>null</c>。</summary>
    private KDNode? _root;

    /// <summary>树是否已构建。Add/Remove 后标记为 false，下次搜索时触发重建。</summary>
    private bool _isBuilt;

    #region 节点定义

    /// <summary>
    /// KD-Tree 的节点。每个节点存储一个向量，并沿某一维度将空间二分。
    /// <para>
    /// 结构示意（3 维空间，按 x → y → z 循环切分）：
    /// <code>
    ///          [x=5]          ← 根节点，沿 x 轴切分
    ///         /     \
    ///      [y=3]   [y=7]     ← 第 1 层，沿 y 轴切分
    ///      /  \    /  \
    ///   [z=1] ... ...  ...   ← 第 2 层，沿 z 轴切分
    /// </code>
    /// </para>
    /// </summary>
    private sealed class KDNode
    {
        /// <summary>节点对应的向量内部 ID。</summary>
        public int Id;

        /// <summary>节点存储的向量数据。</summary>
        public float[] Vector = [];

        /// <summary>
        /// 切分维度（0-based）。按 <c>depth % 总维度数</c> 循环选择。
        /// 例如 128 维向量：第 0 层切 dim[0]，第 1 层切 dim[1]，…，第 128 层回到 dim[0]。
        /// </summary>
        public int SplitDimension;

        /// <summary>切分值：该节点在 <see cref="SplitDimension"/> 维度上的坐标值。</summary>
        public float SplitValue;

        /// <summary>左子树：<see cref="SplitDimension"/> 维度上坐标值 ≤ <see cref="SplitValue"/> 的节点。</summary>
        public KDNode? Left;

        /// <summary>右子树：<see cref="SplitDimension"/> 维度上坐标值 &gt; <see cref="SplitValue"/> 的节点。</summary>
        public KDNode? Right;
    }

    #endregion

    /// <summary>
    /// 创建 KD-Tree 索引实例。
    /// </summary>
    /// <param name="similarityFunc">相似度计算函数。</param>
    public KDTreeIndex(SimilarityFunc similarityFunc)
    {
        _similarityFunc = similarityFunc;
    }

    /// <inheritdoc />
    public int Count => _vectors.Count;

    /// <inheritdoc />
    /// <remarks>添加后标记树需要重建。新向量不会立即插入树中，而是在下次搜索时触发全量重建。</remarks>
    public void Add(int id, float[] vector)
    {
        _vectors[id] = vector;
        _isBuilt = false; // 标记需要重建
    }

    /// <inheritdoc />
    /// <remarks>删除后标记树需要重建。</remarks>
    public void Remove(int id)
    {
        if (_vectors.Remove(id))
            _isBuilt = false; // 数据变更，标记需要重建
    }

    /// <inheritdoc />
    public void Clear()
    {
        _vectors.Clear();
        _root = null;
        _isBuilt = false;
    }

    /// <summary>
    /// 搜索与查询向量最相似的 Top-K 个结果。利用 KD-Tree 的空间剪枝避免遍历所有节点。
    /// <para>
    /// 内部使用最小堆维护当前 Top-K 结果：
    /// <list type="bullet">
    ///   <item>堆未满（&lt; topK）时直接入堆</item>
    ///   <item>堆已满时，新结果优于堆顶（当前最差结果）才替换</item>
    ///   <item>堆顶的相似度用于剪枝判断：若子树不可能包含更优结果则跳过</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_vectors.Count == 0) return [];

        // 确保 KD-Tree 已构建（惰性构建 / 数据变更后自动重建）
        EnsureBuilt();

        // 最小堆：堆顶为当前 Top-K 中相似度最低的（最差的），便于淘汰和剪枝
        var results = new PriorityQueue<int, float>();
        float worstSim = float.MinValue;

        // 递归搜索，利用空间剪枝跳过不必要的子树
        SearchNode(_root, query, topK, results, ref worstSim);

        // 将堆中结果转为列表
        var output = new List<(int Id, float Similarity)>(results.Count);
        while (results.Count > 0)
        {
            results.TryDequeue(out var id, out var sim);
            output.Add((id, sim));
        }

        // 按相似度降序排列（堆出队顺序为升序）
        output.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return output;
    }

    /// <summary>
    /// 搜索所有相似度不低于阈值的向量。
    /// KD-Tree 的剪枝难以直接应用于阈值搜索，因此退化为暴力遍历所有向量。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含）。</param>
    /// <returns>满足阈值条件的 (内部ID, 相似度) 列表，无特定排序。</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        // 阈值搜索不使用 KD-Tree 结构，直接暴力遍历
        var results = new List<(int Id, float Similarity)>();
        foreach (var (id, vector) in _vectors)
        {
            var sim = _similarityFunc(query, vector);
            if (sim >= threshold)
                results.Add((id, sim));
        }
        return results;
    }

    #region KD-Tree 构建

    /// <summary>
    /// 确保 KD-Tree 已构建。首次搜索或数据变更后触发全量重建。
    /// </summary>
    private void EnsureBuilt()
    {
        if (_isBuilt && _root != null) return;

        // 将字典数据转为列表，递归构建平衡 KD-Tree
        var items = _vectors.Select(kv => (kv.Key, kv.Value)).ToList();
        _root = BuildTree(items, 0);
        _isBuilt = true;
    }

    /// <summary>
    /// 递归构建平衡 KD-Tree。每层选择一个维度，按该维度排序取中位数作为切分点。
    /// <para>
    /// 构建流程：
    /// <list type="number">
    ///   <item>选择切分维度：<c>depth % 总维度数</c>（循环切分）</item>
    ///   <item>按切分维度排序，取中位数作为当前节点</item>
    ///   <item>左半部分递归构建左子树，右半部分递归构建右子树</item>
    /// </list>
    /// </para>
    /// <para>
    /// 中位数切分保证树是平衡的（左右子树大小最多相差 1），树高为 O(log n)。
    /// </para>
    /// </summary>
    /// <param name="items">待构建的 (ID, 向量) 列表。</param>
    /// <param name="depth">当前递归深度，用于决定切分维度。</param>
    /// <returns>子树根节点；空列表时返回 <c>null</c>。</returns>
    private static KDNode? BuildTree(List<(int Id, float[] Vector)> items, int depth)
    {
        if (items.Count == 0) return null;

        var dim = items[0].Vector.Length;
        var axis = depth % dim; // 循环选择切分维度

        // 按切分维度排序，取中位数作为切分点
        items.Sort((a, b) => a.Vector[axis].CompareTo(b.Vector[axis]));
        var mid = items.Count / 2;

        return new KDNode
        {
            Id = items[mid].Id,
            Vector = items[mid].Vector,
            SplitDimension = axis,
            SplitValue = items[mid].Vector[axis],
            Left = BuildTree(items[..mid], depth + 1),       // 左半部分 → 左子树
            Right = BuildTree(items[(mid + 1)..], depth + 1)  // 右半部分 → 右子树
        };
    }

    #endregion

    #region KD-Tree 搜索（带剪枝的深度优先搜索）

    /// <summary>
    /// 递归搜索 KD-Tree 节点，使用最小堆维护 Top-K 结果并利用空间剪枝优化。
    /// <para>
    /// 剪枝策略：
    /// <list type="number">
    ///   <item>计算查询点到切分超平面的距离 <c>diff</c></item>
    ///   <item>优先搜索查询点所在的一侧（<c>first</c> 子树）</item>
    ///   <item>对另一侧（<c>second</c> 子树）：仅当结果不足 topK 或切分距离小于当前搜索半径时才探索。
    ///         否则该子树中不可能包含比当前 Top-K 更优的结果，安全跳过</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="node">当前搜索的树节点。</param>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">目标结果数量。</param>
    /// <param name="results">
    /// 最小堆，按相似度升序排列（堆顶为最差结果）。
    /// 用于快速判断新节点是否优于当前 Top-K 中最差的。
    /// </param>
    /// <param name="worstSim">
    /// 当前 Top-K 中最差的相似度（堆顶值）。用于剪枝判断。
    /// 通过 <c>ref</c> 传递，在整个递归过程中共享更新。
    /// </param>
    private void SearchNode(
        KDNode? node,
        float[] query,
        int topK,
        PriorityQueue<int, float> results,
        ref float worstSim)
    {
        if (node == null) return;

        // 计算查询向量与当前节点向量的相似度
        var sim = _similarityFunc(query, node.Vector);

        // 更新 Top-K 结果堆
        if (results.Count < topK)
        {
            // 堆未满，直接入堆
            results.Enqueue(node.Id, sim);
            if (results.Count == topK)
                results.TryPeek(out _, out worstSim); // 堆刚满时记录最差值
        }
        else if (sim > worstSim)
        {
            // 堆已满但新结果更优 → 淘汰最差结果，替换为新结果
            results.DequeueEnqueue(node.Id, sim);
            results.TryPeek(out _, out worstSim); // 更新最差值
        }

        // ── 空间剪枝决策 ──
        // diff > 0 → 查询点在切分面右侧，优先搜索右子树
        // diff ≤ 0 → 查询点在切分面左侧，优先搜索左子树
        var diff = query[node.SplitDimension] - node.SplitValue;
        var (first, second) = diff <= 0 ? (node.Left, node.Right) : (node.Right, node.Left);

        // 始终搜索查询点所在一侧的子树
        SearchNode(first, query, topK, results, ref worstSim);

        // 对另一侧子树进行剪枝判断：
        // - 结果不足 topK → 必须探索（还需要更多候选）
        // - |diff| < 搜索半径 → 切分超平面与搜索球相交，另一侧可能有更优结果
        if (results.Count < topK || Math.Abs(diff) < EstimateRadius(worstSim, query.Length))
            SearchNode(second, query, topK, results, ref worstSim);
    }

    /// <summary>
    /// 将相似度转换为估计的欧几里得搜索半径，用于 KD-Tree 的空间剪枝。
    /// <para>
    /// 转换公式基于余弦相似度与欧几里得距离的近似关系（归一化向量下精确成立）：
    /// <code>
    /// ‖a - b‖² = ‖a‖² + ‖b‖² - 2·(a·b) = 2 - 2·cos(θ) = 2·(1 - similarity)
    /// ∴ ‖a - b‖ = √(2·(1 - similarity))
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="similarity">当前 Top-K 中最差的相似度值。</param>
    /// <param name="dim">向量维度（当前未使用，保留用于未来维度相关的半径修正）。</param>
    /// <returns>估计的欧几里得搜索半径。</returns>
    private static float EstimateRadius(float similarity, int dim)
    {
        // 对归一化向量：distance² = 2(1 - cosine_similarity)
        var approxDist = MathF.Sqrt(2 * (1 - similarity));
        return approxDist;
    }

    #endregion
}