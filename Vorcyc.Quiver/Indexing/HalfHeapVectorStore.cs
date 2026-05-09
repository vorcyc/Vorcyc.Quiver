using Vorcyc.Quiver.Numerics;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// GC 托管堆上的 <see cref="Half"/>（fp16）向量存储。语义等同于 <see cref="HeapVectorStore"/>，
/// 但每个向量以 <c>Half[]</c> 保存，内存占用相比 <c>float[]</c> 减半。
/// <para>
/// 索引与相似度计算始终面向 <see cref="float"/> 视图：<see cref="Get(int)"/> 在读取时把对应行
/// 加宽（widen）到一个 <see langword="ThreadStatic"/> 的 <c>float[]</c> 缓冲并返回其 span。
/// 该 span 的有效期延续到<b>同一线程</b>下一次对任意 store 的 <see cref="Get(int)"/> 调用为止——
/// 调用方必须在读锁内同步消费（与 <see cref="MmapVectorStore"/> 的 SQ8 解码路径完全一致）。
/// </para>
/// <para>
/// 持久化层通过 <see cref="ElementType"/> 得知该字段应以 <see cref="Storage.VectorBlobEncoding.Float16"/>
/// 落盘；lazy 物化通过 <see cref="GetHalfCopy(int)"/> 取回原始 fp16，保证物化结果与声明类型 <c>Half[]</c> 一致。
/// </para>
/// </summary>
/// <seealso cref="IVectorStore"/>
/// <seealso cref="HeapVectorStore"/>
internal sealed class HalfHeapVectorStore : IVectorStore
{
    private readonly Dictionary<int, Half[]> _vectors = [];
    private readonly int _effectiveDim;
    private long _heapBytes;

    // widen 解码线程局部缓冲；同线程内逐次调用 Get 会复用同一段内存。
    [ThreadStatic] private static float[]? _tlWidenBuffer;

    public HalfHeapVectorStore(int effectiveDim = 0)
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
    public VectorElementType ElementType => VectorElementType.Float16;

    /// <inheritdoc />
    public void Store(int id, ReadOnlySpan<float> vector)
    {
        var copy = VectorMath.NarrowFloatToHalf(vector);
        if (_vectors.TryGetValue(id, out var old)) _heapBytes -= (long)old.Length * 2;
        _vectors[id] = copy;
        _heapBytes += (long)copy.Length * 2;
    }

    /// <inheritdoc />
    public void StoreByRef(int id, float[] vector)
    {
        // float[] 入口：窄化为 Half[] 后持有（无法零拷贝，因为物理存储是 fp16）。
        Store(id, vector);
    }

    /// <inheritdoc />
    public void StoreByRefHalf(int id, Half[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (_vectors.TryGetValue(id, out var old)) _heapBytes -= (long)old.Length * 2;
        _vectors[id] = vector;
        _heapBytes += (long)vector.Length * 2;
    }

    /// <inheritdoc />
    public ReadOnlySpan<float> Get(int id)
    {
        var half = _vectors[id];
        var buf = _tlWidenBuffer;
        if (buf is null || buf.Length < half.Length)
            buf = _tlWidenBuffer = new float[half.Length];
        VectorMath.WidenHalfToFloat(half, buf.AsSpan(0, half.Length));
        return buf.AsSpan(0, half.Length);
    }

    /// <inheritdoc />
    public Half[]? GetHalfCopy(int id)
        => _vectors.TryGetValue(id, out var half) ? (Half[])half.Clone() : null;

    /// <inheritdoc />
    public bool Contains(int id) => _vectors.ContainsKey(id);

    /// <inheritdoc />
    public void Remove(int id)
    {
        if (_vectors.TryGetValue(id, out var arr))
        {
            _heapBytes -= (long)arr.Length * 2;
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
