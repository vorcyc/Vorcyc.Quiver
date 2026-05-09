namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 向量存储的可热替换包装。索引（FlatIndex / HnswIndex / ...）在构造期持有一个
/// <see cref="VectorStoreSlot"/> 引用而非具体 <see cref="IVectorStore"/>；运行时把
/// 内层实现从 <see cref="HeapVectorStore"/> 切换为 <see cref="MmapVectorStore"/>
/// （或反向）时只需替换 <see cref="Inner"/>，索引的 id 集合无需重建。
/// <para>
/// 该包装对所有 <see cref="IVectorStore"/> 调用做纯转发，零额外状态。
/// 替换必须在 <see cref="QuiverSet{TEntity}"/> 写锁内进行。
/// </para>
/// </summary>
internal sealed class VectorStoreSlot : IVectorStore
{
    private IVectorStore _inner;

    public VectorStoreSlot(IVectorStore inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>当前内层实现。调用方必须在写锁保护下访问/替换。</summary>
    public IVectorStore Inner => _inner;

    /// <summary>替换内层实现。旧实现不会被本方法自动释放。</summary>
    public void Replace(IVectorStore next)
    {
        _inner = next ?? throw new ArgumentNullException(nameof(next));
    }

    public int Count => _inner.Count;
    public long HeapByteSize => _inner.HeapByteSize;
    public int EffectiveDim => _inner.EffectiveDim;
    public VectorElementType ElementType => _inner.ElementType;
    public IEnumerable<int> Ids => _inner.Ids;

    public void Store(int id, ReadOnlySpan<float> vector) => _inner.Store(id, vector);
    public void StoreByRef(int id, float[] vector) => _inner.StoreByRef(id, vector);
    public void StoreByRefHalf(int id, Half[] vector) => _inner.StoreByRefHalf(id, vector);
    public ReadOnlySpan<float> Get(int id) => _inner.Get(id);
    public Half[]? GetHalfCopy(int id) => _inner.GetHalfCopy(id);
    public bool Contains(int id) => _inner.Contains(id);
    public void Remove(int id) => _inner.Remove(id);
    public void Clear() => _inner.Clear();

    public void Dispose() => _inner.Dispose();

    /// <summary>把任意 <see cref="IVectorStore"/> 解包成具体类型 <typeparamref name="T"/>，自动穿透 slot。</summary>
    internal static T? As<T>(IVectorStore? store) where T : class, IVectorStore
    {
        if (store is null) return null;
        if (store is T direct) return direct;
        if (store is VectorStoreSlot slot && slot._inner is T inner) return inner;
        return null;
    }
}
