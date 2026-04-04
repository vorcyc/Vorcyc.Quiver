using System.Collections.Concurrent;
using Vorcyc.Quiver.Similarity;

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
/// <para>
/// 类型参数 <typeparamref name="TSim"/> 在 JIT 编译期特化，
/// <c>TSim.Compute()</c> 被内联为直接调用（无虚分派、无委托间接调用）。
/// </para>
/// </summary>
/// <typeparam name="TSim">相似度算法类型，须为 struct 以启用 JIT 特化。</typeparam>
internal sealed class FlatIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    private readonly IVectorStore _vectorStore;

    /// <summary>索引中已注册的内部 ID 集合。向量数据由 <see cref="_vectorStore"/> 管理。</summary>
    private readonly HashSet<int> _ids = [];

    /// <summary>
    /// 并行搜索阈值。超过此数量时使用 <see cref="Parallel.ForEach"/> 多线程搜索。
    /// 低于此值时顺序遍历更快（避免线程调度和 ConcurrentBag 的同步开销）。
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
    /// 搜索与查询向量最相似的 Top-K 个结果。
    /// 根据当前向量数量自动选择顺序搜索或并行搜索策略。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_ids.Count == 0) return [];

        // 小数据量顺序遍历更快，大数据量并行计算更优
        return _ids.Count > ParallelThreshold
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
        foreach (var id in _ids)
        {
            var sim = TSim.Compute(query, _vectorStore.Get(id));
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
        var results = new List<(int Id, float Sim)>(_ids.Count);
        foreach (var id in _ids)
            results.Add((id, TSim.Compute(query, _vectorStore.Get(id))));

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
        // 拍摄 ID 快照供并行遍历（避免跨线程枚举 HashSet）
        var ids = _ids.ToArray();
        var results = new ConcurrentBag<(int Id, float Similarity)>();

        Parallel.ForEach(ids, id =>
        {
            results.Add((id, TSim.Compute(query, _vectorStore.Get(id))));
        });

        return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
    }
}