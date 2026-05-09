using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 内存映射文件支撑的向量存储。已持久化到 v4 <c>VectorBlob</c> 段的向量通过
/// <see cref="MemoryMappedFile"/> 直接暴露给搜索路径，<b>不</b>在托管堆上保留 <c>float[]</c>；
/// 新增 / 修改的向量进入小容量堆 overflow，下次 <c>SaveAsync</c> 写盘后由调用方
/// 通过 <see cref="Rebind"/> 切回 mmap 视图。
/// <para>
/// 仅在 <see cref="VectorMemoryMode.MemoryMapped"/> 或 <see cref="VectorMemoryMode.Auto"/> 自动选择下启用。
/// 单个 store 实例服务于单个 <c>(类型, 向量字段)</c>，可绑定多个 mmap 区域以承接多次 append 的段。
/// </para>
/// </summary>
internal sealed unsafe class MmapVectorStore : IVectorStore
{
    private readonly int _dim;
    private readonly List<MmapRegion> _regions = [];
    private readonly Dictionary<int, (int RegionIndex, int Row)> _idToRow = [];
    private readonly Dictionary<int, float[]> _overflow = [];

    // SQ8 解码线程局部缓冲；同线程内逐次调用 Get 会复用同一段内存。
    [ThreadStatic] private static float[]? _tlDecodeBuffer;

    /// <summary>构造一个尚未绑定 mmap 区域的空存储。</summary>
    /// <param name="dimensions">向量维度，所有持久化段和 overflow 都必须等于该值。</param>
    public MmapVectorStore(int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Vector dimensions must be positive.");
        _dim = dimensions;
    }

    /// <inheritdoc />
    public int Count => _idToRow.Count + _overflow.Count;

    /// <inheritdoc />
    public long HeapByteSize
    {
        get
        {
            long total = 0;
            foreach (var arr in _overflow.Values) total += (long)arr.Length * sizeof(float);
            return total;
        }
    }

    /// <inheritdoc />
    public int EffectiveDim => _dim;

    /// <inheritdoc />
    public IEnumerable<int> Ids
    {
        get
        {
            foreach (var id in _idToRow.Keys) yield return id;
            foreach (var id in _overflow.Keys)
                if (!_idToRow.ContainsKey(id))
                    yield return id;
        }
    }

    /// <inheritdoc />
    public void Store(int id, ReadOnlySpan<float> vector)
    {
        if (vector.Length != _dim)
            throw new ArgumentException($"Vector dim {vector.Length} mismatches store dim {_dim}.", nameof(vector));
        _overflow[id] = vector.ToArray();
    }

    /// <inheritdoc />
    public void StoreByRef(int id, float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != _dim)
            throw new ArgumentException($"Vector dim {vector.Length} mismatches store dim {_dim}.", nameof(vector));
        _overflow[id] = vector;
    }

    /// <inheritdoc />
    public ReadOnlySpan<float> Get(int id)
    {
        if (_overflow.TryGetValue(id, out var arr))
            return arr;
        if (_idToRow.TryGetValue(id, out var loc))
        {
            var region = _regions[loc.RegionIndex];
            if (region.Encoding == VectorBlobEncoding.Float32)
                return region.RowSpan(loc.Row, _dim);

            // SQ8：解码到线程本地缓冲，搜索路径读取后立即消费，不会跨线程逃逸。
            var buf = _tlDecodeBuffer;
            if (buf is null || buf.Length < _dim)
                buf = _tlDecodeBuffer = new float[_dim];
            region.DecodeRow(loc.Row, _dim, buf);
            return buf.AsSpan(0, _dim);
        }
        throw new KeyNotFoundException($"Vector id {id} not found in mmap store.");
    }

    /// <inheritdoc />
    public bool Contains(int id) => _overflow.ContainsKey(id) || _idToRow.ContainsKey(id);

    /// <inheritdoc />
    public void Remove(int id)
    {
        _overflow.Remove(id);
        _idToRow.Remove(id);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _overflow.Clear();
        _idToRow.Clear();
        DisposeRegions();
    }

    /// <summary>
    /// 注册一个新的持久化向量区域（来自 v4 <c>VectorBlob</c> 段）。<paramref name="rowIds"/> 给出
    /// 区域内第 <c>i</c> 行所对应的实体 ID；已在 overflow 中存在同 ID 的条目不会被覆盖（写优先）。
    /// </summary>
    /// <param name="mmf">已打开的 <see cref="MemoryMappedFile"/>，所有权转移给本 store。</param>
    /// <param name="payloadOffset">向量数据相对于文件起始处的字节偏移（不含段头）。</param>
    /// <param name="rowCount">区域内向量行数。</param>
    /// <param name="rowIds">长度为 <paramref name="rowCount"/> 的 ID 数组；<c>-1</c> 表示该槽位为 null 占位（不参与索引）。</param>
    /// <param name="encoding">行编码（Float32 或 SQ8）。默认 Float32 用于向后兼容。</param>
    /// <param name="storageDim">磁盘行的实际维度。0 时取 <see cref="_dim"/>。</param>
    /// <param name="sq8Scales">仅 SQ8：长度为 <paramref name="rowCount"/> 的 per-row scale 数组。</param>
    public void BindRegion(
        MemoryMappedFile mmf,
        long payloadOffset,
        int rowCount,
        int[] rowIds,
        VectorBlobEncoding encoding = VectorBlobEncoding.Float32,
        int storageDim = 0,
        float[]? sq8Scales = null)
    {
        ArgumentNullException.ThrowIfNull(mmf);
        ArgumentNullException.ThrowIfNull(rowIds);
        if (rowIds.Length != rowCount)
            throw new ArgumentException("rowIds length must match rowCount.", nameof(rowIds));
        if (encoding == VectorBlobEncoding.Sq8 && (sq8Scales is null || sq8Scales.Length != rowCount))
            throw new ArgumentException("SQ8 encoding requires a per-row scale array matching rowCount.", nameof(sq8Scales));

        var effectiveStorageDim = storageDim > 0 ? storageDim : _dim;
        var region = new MmapRegion(mmf, payloadOffset, rowCount, _dim, encoding, effectiveStorageDim, sq8Scales);
        _regions.Add(region);
        int regionIndex = _regions.Count - 1;
        for (int r = 0; r < rowCount; r++)
        {
            var id = rowIds[r];
            if (id < 0) continue;
            if (_overflow.ContainsKey(id)) continue;
            _idToRow[id] = (regionIndex, r);
        }
    }

    /// <summary>
    /// 用一组新的 mmap 区域整体替换当前绑定（典型场景：<c>SaveAsync</c> 写盘后重新打开 mmap）。
    /// overflow 会被清空，所有 ID 由 <paramref name="bind"/> 回调重新登记。
    /// </summary>
    public void Rebind(Action<MmapVectorStore> bind)
    {
        DisposeRegions();
        _idToRow.Clear();
        _overflow.Clear();
        bind(this);
    }

    /// <inheritdoc />
    public void Dispose() => DisposeRegions();

    private void DisposeRegions()
    {
        foreach (var r in _regions) r.Dispose();
        _regions.Clear();
    }

    /// <summary>
    /// 单个 mmap 区域：持有 <see cref="MemoryMappedViewAccessor"/> 并在生命周期内固定 base pointer。
    /// 所有 <see cref="RowSpan"/> 返回的 <see cref="ReadOnlySpan{T}"/> 均直接指向 mmap 映射页，不复制。
    /// </summary>
    private sealed class MmapRegion : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly byte* _basePtr;
        private readonly long _payloadOffset;
        private readonly int _rowCount;
        private readonly int _dim;
        private readonly int _storageDim;
        private readonly int _rowStride;
        private readonly float[]? _sq8Scales;

        public VectorBlobEncoding Encoding { get; }

        public MmapRegion(
            MemoryMappedFile mmf,
            long payloadOffset,
            int rowCount,
            int dim,
            VectorBlobEncoding encoding,
            int storageDim,
            float[]? sq8Scales)
        {
            _mmf = mmf;
            _payloadOffset = payloadOffset;
            _rowCount = rowCount;
            _dim = dim;
            _storageDim = storageDim;
            Encoding = encoding;
            _sq8Scales = sq8Scales;
            _rowStride = VectorBlobFormat.GetRowStride(encoding, storageDim);
            long bytes = (long)rowCount * _rowStride;
            _accessor = mmf.CreateViewAccessor(payloadOffset, bytes, MemoryMappedFileAccess.Read);
            byte* p = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _basePtr = p + _accessor.PointerOffset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<float> RowSpan(int row, int dim)
        {
            if ((uint)row >= (uint)_rowCount)
                throw new ArgumentOutOfRangeException(nameof(row));
            if (Encoding != VectorBlobEncoding.Float32)
                throw new InvalidOperationException("RowSpan is only valid for Float32 regions; call DecodeRow instead.");
            if (dim > _storageDim)
                throw new ArgumentException("Dim exceeds region storage dim.", nameof(dim));
            long byteOffset = (long)row * _rowStride;
            return new ReadOnlySpan<float>(_basePtr + byteOffset, dim);
        }

        /// <summary>把第 <paramref name="row"/> 行解码到 <paramref name="destination"/>（前 <paramref name="dim"/> 个 float）。</summary>
        public void DecodeRow(int row, int dim, Span<float> destination)
        {
            if ((uint)row >= (uint)_rowCount)
                throw new ArgumentOutOfRangeException(nameof(row));
            if (dim > _storageDim)
                throw new ArgumentException("Dim exceeds region storage dim.", nameof(dim));
            if (destination.Length < dim)
                throw new ArgumentException("Destination too small.", nameof(destination));

            long byteOffset = (long)row * _rowStride;
            switch (Encoding)
            {
                case VectorBlobEncoding.Float32:
                {
                    var src = new ReadOnlySpan<float>(_basePtr + byteOffset, dim);
                    src.CopyTo(destination);
                    break;
                }
                case VectorBlobEncoding.Sq8:
                {
                    var codes = new ReadOnlySpan<sbyte>(_basePtr + byteOffset, dim);
                    var scale = _sq8Scales is not null && row < _sq8Scales.Length ? _sq8Scales[row] : 0f;
                    Sq8Codec.DecodeRow(codes, scale, destination[..dim]);
                    break;
                }
                case VectorBlobEncoding.Float16:
                {
                    // fp16 直读后 widen 到 float 视图（搜索路径以 float 计算）。
                    var src = new ReadOnlySpan<Half>(_basePtr + byteOffset, dim);
                    Vorcyc.Quiver.Numerics.VectorMath.WidenHalfToFloat(src, destination[..dim]);
                    break;
                }
                default:
                    throw new InvalidDataException($"Unknown VectorBlob encoding: {(byte)Encoding}.");
            }
        }

        public void Dispose()
        {
            try { _accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
            catch { /* released */ }
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }
}
