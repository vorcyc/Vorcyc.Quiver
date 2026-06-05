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
    private readonly int _effectiveDim;
    private long _heapBytes;

    public HeapVectorStore(int effectiveDim = 0)
    {
        _effectiveDim = effectiveDim;
    }

    /// <inheritdoc />
    public int Count => _vectors.Count;

    /// <inheritdoc />
    public IEnumerable<int> Ids => _vectors.Keys;

    /// <inheritdoc />
    public long HeapByteSize => _heapBytes;

    /// <inheritdoc />
    public int EffectiveDim => _effectiveDim;

    /// <inheritdoc />
    public void Store(int id, ReadOnlySpan<float> vector)
    {
        var copy = vector.ToArray();
        if (_vectors.TryGetValue(id, out var old)) _heapBytes -= (long)old.Length * sizeof(float);
        _vectors[id] = copy;
        _heapBytes += (long)copy.Length * sizeof(float);
    }

    /// <inheritdoc />
    public void StoreByRef(int id, float[] vector)
    {
        if (_vectors.TryGetValue(id, out var old)) _heapBytes -= (long)old.Length * sizeof(float);
        _vectors[id] = vector;
        _heapBytes += (long)vector.Length * sizeof(float);
    }

    /// <inheritdoc />
    public ReadOnlySpan<float> Get(int id) => _vectors[id];

    /// <inheritdoc />
    public float[]? GetArrayRef(int id) => _vectors.TryGetValue(id, out var arr) ? arr : null;

    /// <inheritdoc />
    public bool Contains(int id) => _vectors.ContainsKey(id);

    /// <inheritdoc />
    public void Remove(int id)
    {
        if (_vectors.TryGetValue(id, out var arr))
        {
            _heapBytes -= (long)arr.Length * sizeof(float);
            _vectors.Remove(id);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _vectors.Clear();
        _heapBytes = 0;
    }

    /// <inheritdoc />
    public void Dispose() { /* GC-managed; no manual release needed */ }
}
