using System.Numerics.Tensors;
using Vorcyc.Quiver.Similarity;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// IVF（Inverted File Index）倒排文件索引：基于 K-Means 聚类的近似最近邻搜索。
/// <para>
/// <b>核心思想</b>：先用 K-Means 将向量空间划分为 K 个 Voronoi 单元（聚类），
/// 每个聚类维护一个倒排列表（属于该聚类的向量 ID 列表）。
/// 搜索时只扫描与查询向量最近的 nProbe 个聚类，大幅减少计算量。
/// </para>
/// <para>
/// <b>性能特征</b>：
/// <list type="bullet">
///   <item>构建复杂度：O(n × k × d × iter)，n=向量数，k=聚类数，d=维度，iter=迭代次数</item>
///   <item>搜索复杂度：O(k × d + nProbe × n/k × d)，远小于暴力搜索的 O(n × d)</item>
///   <item>空间复杂度：O(n × d + k × d)，原始向量 + 聚类质心</item>
/// </list>
/// </para>
/// <para>
/// <b>参数调优指南</b>：
/// <list type="bullet">
///   <item><c>numClusters</c>：聚类数。默认为 0 时自动取 √n。增大减少每个聚类的向量数但增加质心比较开销</item>
///   <item><c>numProbes</c>：搜索时探测的聚类数。增大提高召回率但减慢搜索。推荐 1~20</item>
/// </list>
/// </para>
/// <para>
/// <b>延迟构建 + 自动重建</b>：索引在首次搜索时构建（惰性），
/// 当数据量增长超过上次构建时的 <see cref="RebuildRatio"/> 倍（1.5x）时自动标记需要重建。
/// </para>
/// </summary>
internal sealed class IvfIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    // ──────────────────────────────────────────────────────────────
    // 算法参数
    // ──────────────────────────────────────────────────────────────

    /// <summary>向量数据存储，由外部注入。</summary>
    private readonly IVectorStore _vectorStore;

    /// <summary>
    /// 搜索时探测的聚类数量。值越大召回率越高，但搜索越慢。
    /// 设为 k（聚类总数）时等价于暴力搜索。
    /// </summary>
    private readonly int _numProbes;

    /// <summary>
    /// 聚类数量。为 0 时在 <see cref="Build"/> 中自动取 <c>√n</c>。
    /// 构建后固定为实际使用的值。
    /// </summary>
    private int _numClusters;

    // ──────────────────────────────────────────────────────────────
    // ID 跟踪
    // ──────────────────────────────────────────────────────────────

    /// <summary>索引中已注册的内部 ID 集合。向量数据由 <see cref="_vectorStore"/> 管理。</summary>
    private readonly HashSet<int> _ids = [];

    // ──────────────────────────────────────────────────────────────
    // 聚类结构（由 Build() 构建）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// K-Means 聚类质心数组。<c>_centroids[i]</c> 为第 i 个聚类的中心向量。
    /// 搜索时先计算查询向量与所有质心的相似度，选最近的 nProbe 个聚类探测。
    /// </summary>
    private float[][] _centroids = [];

    /// <summary>
    /// 倒排列表数组。<c>_invertedLists[i]</c> 存储分配到第 i 个聚类的所有向量内部 ID。
    /// 搜索时遍历选中聚类的倒排列表，计算精确相似度。
    /// </summary>
    private List<int>[] _invertedLists = [];

    /// <summary>索引是否已构建。首次搜索时触发构建，数据量增长过大时标记为 false 触发重建。</summary>
    private bool _isBuilt;

    // ──────────────────────────────────────────────────────────────
    // 自动重建控制
    // ──────────────────────────────────────────────────────────────

    /// <summary>上次构建索引时的向量数量。用于与当前数量对比判断是否需要重建。</summary>
    private int _lastBuildCount;

    /// <summary>
    /// 自动重建阈值倍率。当 <c>当前向量数 &gt; 上次构建时向量数 × RebuildRatio</c> 时，
    /// 标记索引需要重建。1.5 表示数据量增长 50% 后触发。
    /// </summary>
    private const double RebuildRatio = 1.5;

    /// <summary>
    /// 创建 IVF 索引实例。
    /// </summary>
    /// <param name="vectorStore">向量数据存储。</param>
    /// <param name="numClusters">
    /// 聚类数量。为 0 时自动取 <c>√n</c>（n 为首次构建时的向量数量）。
    /// 数据量大时建议显式指定。
    /// </param>
    /// <param name="numProbes">
    /// 搜索时探测的聚类数量。默认 10。增大可提高召回率，减小可提高速度。
    /// </param>
    public IvfIndex(IVectorStore vectorStore, int numClusters = 0, int numProbes = 10)
    {
        _vectorStore = vectorStore;
        _numClusters = numClusters;
        _numProbes = numProbes;
    }

    /// <inheritdoc />
    public int Count => _ids.Count;

    /// <summary>
    /// 将指定 ID 注册到索引。新增后检查是否需要标记重建
    /// （当数据量超过上次构建时的 <see cref="RebuildRatio"/> 倍时触发）。
    /// </summary>
    /// <param name="id">内部 ID，向量已存入 <see cref="IVectorStore"/>。</param>
    public void Add(int id)
    {
        _ids.Add(id);

        // 数据量增长超过阈值时，标记需要重建聚类
        if (_isBuilt && _ids.Count > _lastBuildCount * RebuildRatio)
            _isBuilt = false;
    }

    /// <inheritdoc />
    public void Remove(int id) => _ids.Remove(id);

    /// <inheritdoc />
    /// <remarks>清空 ID 集合、聚类质心和倒排列表，重置构建标志。</remarks>
    public void Clear()
    {
        _ids.Clear();
        _centroids = [];
        _invertedLists = [];
        _isBuilt = false;
    }

    /// <summary>
    /// 搜索与查询向量最相似的 Top-K 个结果。算法流程：
    /// <list type="number">
    ///   <item>计算查询向量与所有 K 个聚类质心的相似度</item>
    ///   <item>选取相似度最高的 nProbe 个聚类</item>
    ///   <item>遍历选中聚类的倒排列表，计算精确相似度</item>
    ///   <item>从候选结果中选取 Top-K 返回</item>
    /// </list>
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_ids.Count == 0) return [];

        // 确保聚类索引已构建（惰性构建 / 数据增长后自动重建）
        EnsureBuilt();

        // 步骤 1：计算查询向量与所有聚类质心的相似度
        var clusterSims = new (int Index, float Similarity)[_centroids.Length];
        for (int i = 0; i < _centroids.Length; i++)
            clusterSims[i] = (i, TSim.Compute(query, _centroids[i]));

        // 步骤 2：选取最相似的 nProbe 个聚类进行探测
        var probeClusters = clusterSims
            .OrderByDescending(c => c.Similarity)
            .Take(Math.Min(_numProbes, _centroids.Length));

        // 步骤 3：遍历选中聚类的倒排列表，计算与每个向量的精确相似度
        var results = new List<(int Id, float Similarity)>();
        foreach (var (clusterIdx, _) in probeClusters)
        {
            foreach (var id in _invertedLists[clusterIdx])
            {
                // 跳过已被删除但倒排列表中可能残留的无效 ID
                if (!_vectorStore.Contains(id)) continue;
                results.Add((id, TSim.Compute(query, _vectorStore.Get(id))));
            }
        }

        // 步骤 4：从候选集中选取 Top-K
        return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
    }

    /// <summary>
    /// 搜索所有相似度不低于阈值的向量。
    /// 为提高召回率，探测的聚类数扩大为 <c>nProbe × 2</c>（阈值搜索需要覆盖更多区域）。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含）。</param>
    /// <returns>满足阈值条件的 (内部ID, 相似度) 列表，无特定排序。</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        if (_ids.Count == 0) return [];
        EnsureBuilt();

        var results = new List<(int Id, float Similarity)>();

        // 阈值搜索扩大探测范围（2 倍 nProbe），降低因聚类划分导致的漏检
        var probeClusters = Math.Min(_numProbes * 2, _centroids.Length);

        // 计算查询向量与所有质心的相似度
        var clusterSims = new (int Index, float Similarity)[_centroids.Length];
        for (int i = 0; i < _centroids.Length; i++)
            clusterSims[i] = (i, TSim.Compute(query, _centroids[i]));

        // 探测最近的聚类，仅收集超过阈值的结果
        foreach (var (clusterIdx, _) in clusterSims
            .OrderByDescending(c => c.Similarity).Take(probeClusters))
        {
            foreach (var id in _invertedLists[clusterIdx])
            {
                if (!_vectorStore.Contains(id)) continue;
                var sim = TSim.Compute(query, _vectorStore.Get(id));
                if (sim >= threshold) results.Add((id, sim));
            }
        }
        return results;
    }

    #region K-Means 聚类（SIMD 加速）

    /// <summary>
    /// 确保聚类索引已构建。首次搜索或数据量增长触发重建标志后，调用 <see cref="Build"/> 构建。
    /// </summary>
    private void EnsureBuilt()
    {
        if (_isBuilt) return;
        Build();
    }

    /// <summary>
    /// 构建 K-Means 聚类索引。完整流程：
    /// <list type="number">
    ///   <item>确定聚类数 K（显式指定或自动取 √n）</item>
    ///   <item>使用 K-Means++ 初始化质心（比随机初始化收敛更快、聚类质量更高）</item>
    ///   <item>迭代 Lloyd 算法：分配 → 更新质心，直到收敛或达到最大迭代次数</item>
    ///   <item>构建倒排列表：将每个向量分配到最近的聚类</item>
    /// </list>
    /// <para>
    /// 质心更新使用 <see cref="TensorPrimitives.Add"/> 和 <see cref="TensorPrimitives.Divide"/>
    /// 实现 SIMD 加速的向量累加和均值计算。
    /// </para>
    /// </summary>
    private void Build()
    {
        if (_ids.Count == 0) return;

        // ── 步骤 1：确定聚类数 K ──
        // 未显式指定（为 0）时自动取 √n，在搜索速度和聚类粒度间取平衡
        var k = _numClusters > 0
            ? _numClusters
            : Math.Max(1, (int)Math.Sqrt(_ids.Count));
        _numClusters = k;

        // 提取所有 ID 和向量（从 store 读取并物化为数组，供 K-Means 迭代使用）
        var allIds = _ids.ToList();
        var allVectors = allIds.Select(id => _vectorStore.Get(id).ToArray()).ToList();
        var dim = allVectors[0].Length;

        // ── 步骤 2：K-Means++ 质心初始化 ──
        _centroids = KMeansPlusPlusInit(allVectors, k, dim);

        // ── 步骤 3：Lloyd 迭代（分配 + 更新质心）──
        var assignments = new int[allVectors.Count]; // assignments[i] = 第 i 个向量所属的聚类编号
        const int maxIterations = 50;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // 分配阶段：每个向量分配到最近的聚类质心
            bool changed = false;
            for (int i = 0; i < allVectors.Count; i++)
            {
                var bestCluster = FindNearestCentroid(allVectors[i]);
                if (bestCluster != assignments[i])
                {
                    assignments[i] = bestCluster;
                    changed = true;
                }
            }

            // 所有分配均未变化 → 已收敛，提前终止
            if (!changed) break;

            // 更新阶段：重新计算每个聚类的质心（所有成员向量的均值）
            var sums = new float[k][];   // 各聚类的向量累加和
            var counts = new int[k];     // 各聚类的成员数量
            for (int c = 0; c < k; c++)
                sums[c] = new float[dim];

            // 累加阶段：使用 TensorPrimitives.Add 实现 SIMD 加速的向量加法
            for (int i = 0; i < allVectors.Count; i++)
            {
                var c = assignments[i];
                counts[c]++;
                TensorPrimitives.Add(sums[c], allVectors[i], sums[c]);
            }

            // 除以成员数得到均值：使用 TensorPrimitives.Divide 实现 SIMD 加速
            for (int c = 0; c < k; c++)
            {
                // 空聚类保留原质心不变（避免除以零）
                if (counts[c] == 0) continue;
                TensorPrimitives.Divide(sums[c], (float)counts[c], _centroids[c]);
            }
        }

        // ── 步骤 4：构建倒排列表 ──
        _invertedLists = new List<int>[k];
        for (int c = 0; c < k; c++)
            _invertedLists[c] = [];

        // 将每个向量的内部 ID 添加到其所属聚类的倒排列表中
        for (int i = 0; i < allIds.Count; i++)
            _invertedLists[assignments[i]].Add(allIds[i]);

        _isBuilt = true;
        _lastBuildCount = _ids.Count; // 记录本次构建时的数据量，用于判断是否需要重建
    }

    /// <summary>
    /// 查找与给定向量最相似的聚类质心，返回其索引。
    /// 线性扫描所有质心（质心数 K 通常较小，无需更复杂的加速结构）。
    /// </summary>
    /// <param name="vector">待分配的向量。</param>
    /// <returns>最近聚类质心的索引（0-based）。</returns>
    private int FindNearestCentroid(float[] vector)
    {
        int best = 0;
        float bestSim = float.MinValue;
        for (int i = 0; i < _centroids.Length; i++)
        {
            var sim = TSim.Compute(vector, _centroids[i]);
            if (sim > bestSim) { bestSim = sim; best = i; }
        }
        return best;
    }

    /// <summary>
    /// K-Means++ 质心初始化算法。比随机初始化产生更分散的初始质心，
    /// 加速收敛并减少陷入局部最优的概率。
    /// <para>
    /// 算法流程：
    /// <list type="number">
    ///   <item>随机选择第 1 个质心</item>
    ///   <item>对每个数据点，计算其到最近已选质心的距离²</item>
    ///   <item>以距离²为权重进行轮盘赌采样，距离越远被选中概率越高</item>
    ///   <item>重复 2~3 直到选满 K 个质心</item>
    /// </list>
    /// </para>
    /// <para>
    /// 使用固定种子（42）保证可复现性。距离计算使用 <see cref="TensorPrimitives.Distance"/> SIMD 加速。
    /// </para>
    /// </summary>
    /// <param name="vectors">所有向量数据。</param>
    /// <param name="k">要选择的质心数量。</param>
    /// <param name="dim">向量维度。</param>
    /// <returns>初始化后的质心数组（已克隆，不引用原始数据）。</returns>
    private static float[][] KMeansPlusPlusInit(List<float[]> vectors, int k, int dim)
    {
        var rng = new Random(42);  // 固定种子保证可复现性
        var centroids = new float[k][];

        // 步骤 1：随机选择第 1 个质心
        centroids[0] = (float[])vectors[rng.Next(vectors.Count)].Clone();

        // distances[i] = 第 i 个向量到最近已选质心的距离²
        var distances = new float[vectors.Count];

        // 步骤 2~3：依次选择剩余 k-1 个质心
        for (int c = 1; c < k; c++)
        {
            // 计算每个数据点到最近已选质心的距离²
            float totalDist = 0;
            for (int i = 0; i < vectors.Count; i++)
            {
                float minDist = float.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    // 使用 TensorPrimitives.Distance 计算欧几里得距离（SIMD 加速）
                    var d = TensorPrimitives.Distance(vectors[i], centroids[j]);
                    minDist = Math.Min(minDist, d * d);  // 取距离²（避免开根号后再平方）
                }
                distances[i] = minDist;
                totalDist += minDist;
            }

            // 轮盘赌采样：距离²越大 → 被选为下一个质心的概率越高
            // 这确保新质心尽量远离已有质心，提高聚类分散性
            var threshold = rng.NextSingle() * totalDist;
            float cumulative = 0;
            for (int i = 0; i < vectors.Count; i++)
            {
                cumulative += distances[i];
                if (cumulative >= threshold)
                {
                    centroids[c] = (float[])vectors[i].Clone();
                    break;
                }
            }

            // 极端情况兜底：所有距离为 0（例如重复向量）时随机选择
            centroids[c] ??= (float[])vectors[rng.Next(vectors.Count)].Clone();
        }
        return centroids;
    }

    #endregion
}