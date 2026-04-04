namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 向量数据的存储抽象。将向量的所有权从索引实现中剥离，
/// 使索引仅管理拓扑结构（图/树/倒排列表），向量数据由此接口的实现统一管理。
/// <para>
/// <list type="bullet">
///   <item><see cref="HeapVectorStore"/>：全量内存模式，向量驻留在 GC 托管堆上。</item>
///   <item><see cref="MmapVectorStore"/>：内存映射模式，向量驻留在 OS 管理的映射区域，零 GC 压力。</item>
/// </list>
/// </para>
/// </summary>
/// <seealso cref="IVectorIndex"/>
internal interface IVectorStore : IDisposable
{
    /// <summary>当前存储的向量数量。</summary>
    int Count { get; }

    /// <summary>
    /// 存储一个向量，与指定的内部 ID 关联。
    /// <para>实现决定数据的物理存放位置（GC 堆 / mmap 区域）。</para>
    /// </summary>
    /// <param name="id">内部 ID，由 <c>QuiverSet._nextId</c> 分配。</param>
    /// <param name="vector">向量数据。调用方保证维度正确且已完成归一化（如需要）。</param>
    void Store(int id, ReadOnlySpan<float> vector);

    /// <summary>
    /// 按内部 ID 获取向量的只读视图。
    /// <para>
    /// 返回的 <see cref="ReadOnlySpan{T}"/> 生命周期由实现决定：
    /// <list type="bullet">
    ///   <item><see cref="HeapVectorStore"/>：指向 GC 堆上的 <c>float[]</c>，GC 回收前有效。</item>
    ///   <item><see cref="MmapVectorStore"/>：指向映射区域，store 未 Dispose 前有效。</item>
    /// </list>
    /// 调用方应在读锁内同步使用，不应长期持有。
    /// </para>
    /// </summary>
    /// <param name="id">内部 ID。</param>
    /// <returns>向量数据的只读视图。</returns>
    ReadOnlySpan<float> Get(int id);

    /// <summary>是否包含指定 ID 的向量。</summary>
    bool Contains(int id);

    /// <summary>移除指定 ID 的向量。ID 不存在时静默返回。</summary>
    void Remove(int id);

    /// <summary>清空所有向量数据。</summary>
    void Clear();

    /// <summary>
    /// 获取所有已存储的内部 ID 集合。
    /// <para>供索引遍历使用（如 FlatIndex 暴力搜索需遍历全部 ID）。</para>
    /// </summary>
    IEnumerable<int> Ids { get; }
}
