namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 基于 GC 托管堆的向量存储。行为等同重构前各索引内部的 <c>Dictionary&lt;int, float[]&gt;</c>。
/// <para>
/// 所有向量以 <c>float[]</c> 形式驻留在托管堆上，GC 全权管理生命周期。
/// 适合中小规模数据集（实体数 &lt; 50 万），搜索延迟最低。
/// </para>
/// </summary>
/// <seealso cref="IVectorStore"/>
/// <seealso cref="MmapVectorStore"/>
internal sealed class HeapVectorStore : IVectorStore
{
    private readonly Dictionary<int, float[]> _vectors = [];

    /// <inheritdoc />
    public int Count => _vectors.Count;

    /// <inheritdoc />
    public IEnumerable<int> Ids => _vectors.Keys;

    /// <inheritdoc />
    public void Store(int id, ReadOnlySpan<float> vector) => _vectors[id] = vector.ToArray();

    /// <inheritdoc />
    public ReadOnlySpan<float> Get(int id) => _vectors[id];

    /// <inheritdoc />
    public bool Contains(int id) => _vectors.ContainsKey(id);

    /// <inheritdoc />
    public void Remove(int id) => _vectors.Remove(id);

    /// <inheritdoc />
    public void Clear() => _vectors.Clear();

    /// <inheritdoc />
    public void Dispose() { /* GC 管理，无需手动释放 */ }
}
