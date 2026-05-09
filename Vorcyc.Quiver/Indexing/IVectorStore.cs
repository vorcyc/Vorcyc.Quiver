namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 向量元素的物理存储类型。决定 store 在堆 / mmap / 磁盘上以何种精度保存每个分量。
/// 索引与相似度计算始终面向 <see cref="float"/> 视图（<see cref="IVectorStore.Get"/>），
/// 与该枚举无关；它仅影响物理存储与持久化编码。
/// </summary>
internal enum VectorElementType : byte
{
    /// <summary>32-bit IEEE 单精度。默认，<c>float[]</c> 字段使用。</summary>
    Float32 = 0,

    /// <summary>16-bit IEEE 半精度（<see cref="System.Half"/>）。<c>Half[]</c> 字段使用，内存/磁盘减半。</summary>
    Float16 = 1,
}

/// <summary>
/// Storage abstraction for vector data. Separates vector ownership from index implementations,
/// allowing indices to manage only topological structure (graph/tree/inverted list)
/// while vector data is managed uniformly by implementations of this interface.
/// <para>
/// Current implementation: <see cref="HeapVectorStore"/> — vectors reside on the GC managed heap.
/// </para>
/// </summary>
/// <seealso cref="IVectorIndex"/>
internal interface IVectorStore : IDisposable
{
    /// <summary>Current number of stored vectors.</summary>
    int Count { get; }

    /// <summary>
    /// Stores a vector associated with the specified internal ID.
    /// <para>The implementation decides the physical storage location (GC heap / mmap region).</para>
    /// </summary>
    /// <param name="id">Internal ID, allocated by <c>QuiverSet._nextId</c>.</param>
    /// <param name="vector">Vector data. The caller guarantees the correct dimension and normalization (if required).</param>
    void Store(int id, ReadOnlySpan<float> vector);

    /// <summary>
    /// Stores a vector by taking ownership of the supplied array reference (zero-copy).
    /// <para>
    /// Callers must guarantee that the array is not subsequently mutated; the store assumes
    /// exclusive ownership for the lifetime of the entry. Used by <c>QuiverSet</c> to avoid
    /// duplicating large embeddings already owned by the entity object.
    /// </para>
    /// </summary>
    /// <param name="id">Internal ID, allocated by <c>QuiverSet._nextId</c>.</param>
    /// <param name="vector">Vector array; stored by reference, not copied.</param>
    void StoreByRef(int id, float[] vector);

    /// <summary>
    /// 以零拷贝方式存入一个 <see cref="System.Half"/>（fp16）向量。仅对元素类型为
    /// <see cref="VectorElementType.Float16"/> 的 store（<see cref="HalfHeapVectorStore"/> /
    /// Half 模式的 <see cref="MmapVectorStore"/>）具有原生无损语义；其它实现的默认行为是
    /// 加宽为 <c>float[]</c> 后调用 <see cref="StoreByRef(int, float[])"/>（兜底，存在精度提升但无损）。
    /// </summary>
    /// <param name="id">Internal ID。</param>
    /// <param name="vector">fp16 向量数组；Half store 直接持有引用，不复制。</param>
    void StoreByRefHalf(int id, System.Half[] vector)
    {
        var widened = Vorcyc.Quiver.Numerics.VectorMath.WidenHalfToFloat(vector);
        StoreByRef(id, widened);
    }

    /// <summary>
    /// 取回指定 ID 的向量并以 <see cref="System.Half"/>（fp16）数组形式返回（始终是新分配的拷贝）。
    /// 用于 lazy / mmap 物化 <c>Half[]</c> 字段，使物化结果与字段声明类型一致。
    /// <para>
    /// 对 <see cref="VectorElementType.Float16"/> store 是原始 fp16 的无损拷贝；对 Float32 store
    /// 的默认实现会把 float 视图收窄为 fp16（仅在类型不匹配的误用场景触发）。ID 不存在时返回 <c>null</c>。
    /// </para>
    /// </summary>
    System.Half[]? GetHalfCopy(int id)
    {
        if (!Contains(id)) return null;
        return Vorcyc.Quiver.Numerics.VectorMath.NarrowFloatToHalf(Get(id));
    }

    /// <summary>
    /// Returns a read-only view of the vector for the specified internal ID.
    /// <para>
    /// The lifetime of the returned <see cref="ReadOnlySpan{T}"/> is determined by the implementation:
    /// it points to a <c>float[]</c> on the GC heap and is valid until the GC collects it.
    /// Callers should use it synchronously within a read lock and must not hold it long-term.
    /// </para>
    /// </summary>
    /// <param name="id">Internal ID.</param>
    /// <returns>A read-only view of the vector data.</returns>
    ReadOnlySpan<float> Get(int id);

    /// <summary>Returns whether a vector with the specified ID exists.</summary>
    bool Contains(int id);

    /// <summary>Removes the vector with the specified ID. Silently returns if the ID does not exist.</summary>
    void Remove(int id);

    /// <summary>Clears all vector data.</summary>
    void Clear();

    /// <summary>
    /// Gets all stored internal IDs.
    /// <para>Used by indices for traversal (e.g., FlatIndex brute-force search needs to iterate all IDs).</para>
    /// </summary>
    IEnumerable<int> Ids { get; }

    /// <summary>
    /// Approximate number of bytes occupied by this store on the managed heap (mmap-resident
/// bytes are excluded). Used by <see cref="Vorcyc.Quiver.QuiverVectorOptions.MaxInMemoryBytes"/>
    /// to decide whether vectors should be promoted to <see cref="MmapVectorStore"/>.
    /// </summary>
    long HeapByteSize { get; }

    /// <summary>
    /// The dimension exposed to indexes and search at runtime. Equal to the declared
    /// <see cref="QuiverVectorAttribute.Dimensions"/> unless Matryoshka truncation is active,
    /// in which case it matches <see cref="QuiverVectorAttribute.EffectiveDimensions"/>.
    /// </summary>
    int EffectiveDim { get; }

    /// <summary>
    /// 本 store 的物理元素存储类型。默认 <see cref="VectorElementType.Float32"/>；
    /// <see cref="HalfHeapVectorStore"/> 与 Half 模式的 mmap store 返回 <see cref="VectorElementType.Float16"/>。
    /// 持久化层据此决定 <see cref="Vorcyc.Quiver.Storage.VectorBlobEncoding"/>，物化层据此决定 lazy 字段返回类型。
    /// </summary>
    VectorElementType ElementType => VectorElementType.Float32;
}
