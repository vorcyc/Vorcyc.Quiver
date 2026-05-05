namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// GC-managed heap-based vector store. Behavior is equivalent to the <c>Dictionary&lt;int, float[]&gt;</c>
/// that each index previously maintained internally.
/// <para>
/// All vectors reside on the managed heap as <c>float[]</c> arrays; the GC fully manages their lifetime.
/// Suitable for small-to-medium datasets (fewer than 500,000 entities) with the lowest search latency.
/// </para>
/// </summary>
/// <seealso cref="IVectorStore"/>
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
    public void Dispose() { /* GC-managed; no manual release needed */ }
}
