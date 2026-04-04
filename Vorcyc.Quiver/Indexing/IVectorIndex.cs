namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 向量索引的统一接口。定义了所有索引实现（Flat、HNSW、IVF、KDTree）必须提供的操作。
/// <para>
/// <b>职责</b>：管理内部 ID 的拓扑结构（图/树/倒排列表），并提供基于相似度的搜索能力。
/// 向量数据的物理存储由 <see cref="IVectorStore"/> 负责，索引通过其引用获取向量。
/// </para>
/// <para>
/// <b>线程安全</b>：由上层 <c>QuiverSet&lt;TEntity&gt;</c> 的读写锁保证，索引实现本身无需处理并发。
/// </para>
/// </summary>
/// <seealso cref="FlatIndex{TSim}"/>
/// <seealso cref="HnswIndex{TSim}"/>
/// <seealso cref="IvfIndex{TSim}"/>
/// <seealso cref="KDTreeIndex{TSim}"/>
internal interface IVectorIndex
{
    /// <summary>当前索引中的向量数量。</summary>
    int Count { get; }

    /// <summary>
    /// 将指定 ID 注册到索引拓扑结构中。
    /// <para>
    /// 向量数据已由调用方写入 <see cref="IVectorStore"/>，
    /// 索引实现通过 <c>IVectorStore.Get(id)</c> 读取向量进行建图/建树。
    /// </para>
    /// </summary>
    /// <param name="id">内部 ID，向量已存入 <see cref="IVectorStore"/>。</param>
    void Add(int id);

    /// <summary>
    /// 从索引拓扑结构中移除指定 ID。ID 不存在时静默返回。
    /// <para>对应的向量数据由 <see cref="IVectorStore"/> 单独管理，此方法不删除向量。</para>
    /// </summary>
    /// <param name="id">要移除的内部 ID。</param>
    void Remove(int id);

    /// <summary>清空索引拓扑结构。</summary>
    void Clear();

    /// <summary>
    /// 搜索与查询向量最相似的 Top-K 个结果。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的 (内部ID, 相似度) 列表。</returns>
    List<(int Id, float Similarity)> Search(float[] query, int topK);

    /// <summary>
    /// 搜索所有相似度不低于阈值的向量。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="threshold">相似度下限（含）。</param>
    /// <returns>(内部ID, 相似度) 列表，无特定排序保证。</returns>
    List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold);
}