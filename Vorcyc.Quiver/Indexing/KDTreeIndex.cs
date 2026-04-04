using Vorcyc.Quiver.Similarity;

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
/// 退化为 O(n) 暴力搜索。高维场景应使用 <see cref="HnswIndex{TSim}"/>。
/// </para>
/// <para>
/// <b>延迟构建</b>：树在首次搜索时构建。每次 <see cref="Add"/> 或 <see cref="Remove"/> 后标记需要重建，
/// 下次搜索时自动重新构建整棵树（静态重建策略，简单但不适合频繁增删场景）。
/// </para>
/// </summary>
/// <typeparam name="TSim">相似度算法类型，须为 struct 以启用 JIT 特化。</typeparam>
internal sealed class KDTreeIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    /// <summary>向量数据存储，由外部注入。</summary>
    private readonly IVectorStore _vectorStore;

    /// <summary>索引中已注册的内部 ID 集合。向量数据由 <see cref="_vectorStore"/> 管理。</summary>
    private readonly HashSet<int> _ids = [];

    /// <summary>KD-Tree 的根节点。空集合或未构建时为 <c>null</c>。</summary>
    private KDNode? _root;

    /// <summary>树是否已构建。Add/Remove 后标记为 false，下次搜索时触发重建。</summary>
    private bool _isBuilt;

    #region 节点定义

    /// <summary>
    /// KD-Tree 的节点。每个节点存储内部 ID 和切分信息，沿某一维度将空间二分。
    /// 向量数据由 <see cref="IVectorStore"/> 统一管理，节点不持有向量引用。
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

        /// <summary>
        /// 切分维度（0-based）。按 <c>depth % 总维度数</c> 循环选择。
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
    /// <param name="vectorStore">向量数据存储。</param>
    public KDTreeIndex(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <inheritdoc />
    public int Count => _ids.Count;

    /// <inheritdoc />
    /// <remarks>添加后标记树需要重建。新向量不会立即插入树中，而是在下次搜索时触发全量重建。</remarks>
    public void Add(int id)
    {
        _ids.Add(id);
        _isBuilt = false;
    }

    /// <inheritdoc />
    /// <remarks>删除后标记树需要重建。</remarks>
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
    /// 搜索与查询向量最相似的 Top-K 个结果。利用 KD-Tree 的空间剪枝避免遍历所有节点。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
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
    /// 搜索所有相似度不低于阈值的向量。
    /// KD-Tree 的剪枝难以直接应用于阈值搜索，因此退化为暴力遍历所有向量。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含）。</param>
    /// <returns>满足阈值条件的 (内部ID, 相似度) 列表，无特定排序。</returns>
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

    #region KD-Tree 构建

    /// <summary>
    /// 确保 KD-Tree 已构建。首次搜索或数据变更后触发全量重建。
    /// </summary>
    private void EnsureBuilt()
    {
        if (_isBuilt && _root != null) return;

        // 从 store 读取向量并物化，供排序和切分使用
        var items = _ids.Select(id => (Id: id, Vector: _vectorStore.Get(id).ToArray())).ToList();
        _root = BuildTree(items, 0);
        _isBuilt = true;
    }

    /// <summary>
    /// 递归构建平衡 KD-Tree。每层选择一个维度，按该维度排序取中位数作为切分点。
    /// <para>
    /// 中位数切分保证树是平衡的（左右子树大小最多相差 1），树高为 O(log n)。
    /// </para>
    /// </summary>
    /// <param name="items">待构建的 (ID, 向量) 列表。向量从 store 物化，仅在构建期间使用。</param>
    /// <param name="depth">当前递归深度，用于决定切分维度。</param>
    /// <returns>子树根节点；空列表时返回 <c>null</c>。</returns>
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

    #region KD-Tree 搜索（带剪枝的深度优先搜索）

    /// <summary>
    /// 递归搜索 KD-Tree 节点，使用最小堆维护 Top-K 结果并利用空间剪枝优化。
    /// </summary>
    private void SearchNode(
        KDNode? node,
        float[] query,
        int topK,
        PriorityQueue<int, float> results,
        ref float worstSim)
    {
        if (node == null) return;

        // 从 store 读取节点向量并计算相似度
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
    /// 将相似度转换为估计的欧几里得搜索半径，用于 KD-Tree 的空间剪枝。
    /// </summary>
    private static float EstimateRadius(float similarity, int dim)
    {
        var approxDist = MathF.Sqrt(2 * (1 - similarity));
        return approxDist;
    }

    #endregion
}