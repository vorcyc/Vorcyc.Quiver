namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 相似度计算委托。
/// <para>
/// 使用 <see cref="ReadOnlySpan{T}"/> 作为参数类型，可直接绑定
/// <c>TensorPrimitives.Dot</c>、<c>TensorPrimitives.CosineSimilarity</c> 等方法组，
/// 无需额外的 lambda 包装。<c>float[]</c> 传入时隐式零成本转换为 <c>ReadOnlySpan&lt;float&gt;</c>。
/// </para>
/// </summary>
/// <param name="a">第一个向量。</param>
/// <param name="b">第二个向量，维度须与 <paramref name="a"/> 一致。</param>
/// <returns>
/// 相似度分数。值越大越相似。
/// 具体范围取决于实现：Cosine/DotProduct 为 [-1, 1]，Euclidean 变换后为 (0, 1]。
/// </returns>
internal delegate float SimilarityFunc(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

/// <summary>
/// 向量索引的统一接口。定义了所有索引实现（Flat、HNSW、IVF、KDTree）必须提供的操作。
/// <para>
/// <b>职责</b>：管理内部 ID 到向量数据的映射，并提供基于相似度的搜索能力。
/// 线程安全由上层 <c>VectorSet&lt;TEntity&gt;</c> 的读写锁保证，索引实现本身无需处理并发。
/// </para>
/// <para>
/// <b>内部 ID</b>：由 <c>VectorSet</c> 分配的自增整数，索引实现不感知用户主键。
/// </para>
/// </summary>
/// <seealso cref="FlatIndex"/>
/// <seealso cref="HnswIndex"/>
/// <seealso cref="IvfIndex"/>
/// <seealso cref="KDTreeIndex"/>
internal interface IVectorIndex
{
    /// <summary>当前索引中的向量数量。</summary>
    int Count { get; }

    /// <summary>
    /// 向索引中添加一个向量。若 <paramref name="id"/> 已存在，行为由具体实现决定
    /// （通常为覆盖，如 <c>Dictionary</c> 索引器语义）。
    /// </summary>
    /// <param name="id">
    /// 内部 ID，由 <c>VectorSet</c> 的 <c>_nextId</c> 自增分配。
    /// </param>
    /// <param name="vector">
    /// 向量数据。已由 <c>VectorSet.PrepareVectors</c> 完成归一化或防御性复制，
    /// 索引实现可直接持有引用，无需再次复制。
    /// </param>
    void Add(int id, float[] vector);

    /// <summary>
    /// 从索引中移除指定 ID 的向量。ID 不存在时静默返回，不抛异常。
    /// </summary>
    /// <param name="id">要移除的内部 ID。</param>
    void Remove(int id);

    /// <summary>
    /// 清空索引中的所有向量数据，重置内部状态。
    /// </summary>
    void Clear();

    /// <summary>
    /// 搜索与查询向量最相似的 Top-K 个结果。
    /// </summary>
    /// <param name="query">
    /// 查询向量。对于 Cosine 度量的字段，已由 <c>VectorSet.NormalizeIfNeeded</c> 完成归一化。
    /// </param>
    /// <param name="topK">返回结果数量上限。实际返回数量可能少于此值（当索引数据不足时）。</param>
    /// <returns>
    /// 按相似度降序排列的 (内部ID, 相似度) 列表。
    /// 调用方通过内部 ID 反查 <c>_entities</c> 字典获取对应实体。
    /// </returns>
    List<(int Id, float Similarity)> Search(float[] query, int topK);

    /// <summary>
    /// 搜索所有相似度不低于阈值的向量。结果数量不固定，取决于数据分布和阈值设置。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含）。低于此值的结果不返回。</param>
    /// <returns>(内部ID, 相似度) 列表，无特定排序保证。</returns>
    List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold);
}