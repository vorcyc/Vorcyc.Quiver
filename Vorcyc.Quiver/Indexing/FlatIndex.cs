using System.Collections.Concurrent;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 平坦索引（暴力搜索）：遍历所有向量计算相似度，返回 Top-K 结果。
/// <para>
/// <b>精确搜索</b>：结果无近似误差，召回率 100%。适合数据量较小或需要精确结果的场景。
/// </para>
/// <para>
/// <b>自动并行化</b>：当向量数量超过 <see cref="ParallelThreshold"/>（10,000）时，
/// 自动切换到 <see cref="Parallel.ForEach"/> 多线程搜索，充分利用多核 CPU。
/// </para>
/// <para>
/// 时间复杂度：O(n × d)，其中 n 为向量数量，d 为向量维度。
/// </para>
/// </summary>
/// <param name="similarityFunc">
/// 相似度计算函数。由 <see cref="QuiverSet{TEntity}"/> 根据距离度量注入，
/// 通常为 <c>TensorPrimitives.Dot</c>、<c>CosineSimilarity</c> 或欧几里得变换。
/// </param>
internal sealed class FlatIndex(SimilarityFunc similarityFunc) : IVectorIndex
{
    /// <summary>内部 ID → 向量数据的映射。向量在写入时已完成归一化或防御性复制。</summary>
    private readonly Dictionary<int, float[]> _vectors = [];

    /// <summary>
    /// 并行搜索阈值。超过此数量时使用 <see cref="Parallel.ForEach"/> 多线程搜索。
    /// 低于此值时顺序遍历更快（避免线程调度和 ConcurrentBag 的同步开销）。
    /// </summary>
    private const int ParallelThreshold = 10_000;

    /// <inheritdoc />
    public int Count => _vectors.Count;

    /// <inheritdoc />
    /// <remarks>使用字典索引器赋值，相同 ID 会覆盖旧向量。</remarks>
    public void Add(int id, float[] vector) => _vectors[id] = vector;

    /// <inheritdoc />
    public void Remove(int id) => _vectors.Remove(id);

    /// <inheritdoc />
    public void Clear() => _vectors.Clear();

    /// <summary>
    /// 搜索与查询向量最相似的 Top-K 个结果。
    /// 根据当前向量数量自动选择顺序搜索或并行搜索策略。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_vectors.Count == 0) return [];

        // 小数据量顺序遍历更快，大数据量并行计算更优
        return _vectors.Count > ParallelThreshold
            ? ParallelSearchCore(query, topK)
            : SequentialSearchCore(query, topK);
    }

    /// <summary>
    /// 搜索所有相似度不低于阈值的向量。结果数量不固定，取决于数据分布。
    /// 始终使用顺序遍历（阈值搜索通常需要检查全部数据，并行收益有限）。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含），低于此值的结果被过滤。</param>
    /// <returns>满足阈值条件的 (内部ID, 相似度) 列表，无特定排序。</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        var results = new List<(int Id, float Similarity)>();
        foreach (var (id, vector) in _vectors)
        {
            var sim = similarityFunc(query, vector);
            if (sim >= threshold)
                results.Add((id, sim));
        }
        return results;
    }

    /// <summary>
    /// 顺序搜索：单线程遍历所有向量计算相似度，LINQ 排序取 Top-K。
    /// 适合向量数量 ≤ <see cref="ParallelThreshold"/> 的场景，避免多线程调度开销。
    /// </summary>
    private List<(int Id, float Similarity)> SequentialSearchCore(float[] query, int topK)
    {
        // 预分配完整容量，避免 List 扩容
        var results = new List<(int Id, float Sim)>(_vectors.Count);
        foreach (var (id, vector) in _vectors)
            results.Add((id, similarityFunc(query, vector)));

        // OrderByDescending + Take 实现 Top-K 选择
        return results.OrderByDescending(r => r.Sim).Take(topK).ToList();
    }

    /// <summary>
    /// 并行搜索：使用 <see cref="Parallel.ForEach"/> 将相似度计算分摊到多个线程池线程。
    /// 适合向量数量 &gt; <see cref="ParallelThreshold"/> 的场景。
    /// <para>
    /// 使用 <see cref="ConcurrentBag{T}"/> 收集结果，线程安全但有轻微同步开销。
    /// 最终排序在单线程上执行。
    /// </para>
    /// </summary>
    private List<(int Id, float Similarity)> ParallelSearchCore(float[] query, int topK)
    {
        var results = new ConcurrentBag<(int Id, float Similarity)>();

        // 每个线程独立计算分配到的向量的相似度，结果汇入 ConcurrentBag
        Parallel.ForEach(_vectors, kvp =>
        {
            results.Add((kvp.Key, similarityFunc(query, kvp.Value)));
        });

        // 汇总后排序取 Top-K（排序在单线程执行）
        return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
    }
}