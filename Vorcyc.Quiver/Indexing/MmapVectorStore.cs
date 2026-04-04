using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// 基于 <see cref="MemoryMappedFile"/> 的向量存储。向量数据驻留在 OS 管理的映射区域，
/// 不占用 GC 托管堆，物理内存由操作系统按需换入/换出（后备存储为 arena 文件）。
/// <para>
/// <b>内存布局</b>：arena 文件是一块连续的 float 数组区域，每个向量占用
/// <c>dimensions × sizeof(float)</c> 字节，按槽位（slot）紧密排列：
/// <code>
/// [slot 0: float × dim] [slot 1: float × dim] [slot 2: float × dim] ...
/// </code>
/// </para>
/// <para>
/// <b>槽位管理</b>：删除向量后槽位回收到空闲队列，下次 <see cref="Store"/> 优先复用，
/// 避免 arena 文件无限增长。
/// </para>
/// <para>
/// <b>生命周期</b>：映射在 <see cref="Dispose"/> 时释放。
/// 在此之前，通过 <see cref="Get"/> 返回的 <see cref="ReadOnlySpan{T}"/> 始终有效。
/// </para>
/// </summary>
/// <seealso cref="IVectorStore"/>
/// <seealso cref="HeapVectorStore"/>
internal sealed unsafe class MmapVectorStore : IVectorStore
{
    private readonly int _dimensions;
    private readonly int _vectorByteSize;
    private readonly string _arenaPath;

    /// <summary>内部 ID → 槽位索引。</summary>
    private readonly Dictionary<int, int> _idToSlot = [];

    /// <summary>已回收的空闲槽位（删除后可复用）。</summary>
    private readonly Queue<int> _freeSlots = new();

    private int _nextSlot;
    private int _capacity;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte* _basePtr;

    /// <summary>初始容量（槽位数），首次创建时分配。</summary>
    private const int InitialCapacity = 1024;

    /// <summary>扩容倍率。容量不足时按此倍率增长。</summary>
    private const double GrowthFactor = 2.0;

    /// <summary>
    /// 创建内存映射向量存储。
    /// </summary>
    /// <param name="arenaPath">
    /// arena 文件路径（如 <c>mydb.db.Document.Embedding.vec</c>）。不存在时自动创建。
    /// </param>
    /// <param name="dimensions">每个向量的维度数。构造后不可变。</param>
    internal MmapVectorStore(string arenaPath, int dimensions)
    {
        _arenaPath = arenaPath;
        _dimensions = dimensions;
        _vectorByteSize = dimensions * sizeof(float);
        _capacity = InitialCapacity;
        EnsureDirectoryExists();
        CreateMapping(_capacity);
    }

    /// <inheritdoc />
    public int Count => _idToSlot.Count;

    /// <inheritdoc />
    public IEnumerable<int> Ids => _idToSlot.Keys;

    /// <inheritdoc />
    public void Store(int id, ReadOnlySpan<float> vector)
    {
        int slot;
        if (_freeSlots.Count > 0)
            slot = _freeSlots.Dequeue();
        else
        {
            slot = _nextSlot++;
            if (slot >= _capacity)
                Grow();
        }

        _idToSlot[id] = slot;

        var dest = new Span<float>(_basePtr + (long)slot * _vectorByteSize, _dimensions);
        vector.CopyTo(dest);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> Get(int id)
    {
        var slot = _idToSlot[id];
        return new ReadOnlySpan<float>(_basePtr + (long)slot * _vectorByteSize, _dimensions);
    }

    /// <inheritdoc />
    public bool Contains(int id) => _idToSlot.ContainsKey(id);

    /// <inheritdoc />
    public void Remove(int id)
    {
        if (_idToSlot.Remove(id, out var slot))
            _freeSlots.Enqueue(slot);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _idToSlot.Clear();
        _freeSlots.Clear();
        _nextSlot = 0;
    }

    /// <summary>扩容：按 <see cref="GrowthFactor"/> 倍增长，重新创建映射。</summary>
    private void Grow()
    {
        var newCapacity = Math.Max(_capacity + 1, (int)(_capacity * GrowthFactor));

        ReleaseMapping();
        CreateMapping(newCapacity);
        _capacity = newCapacity;
    }

    /// <summary>确保 arena 文件所在目录存在。</summary>
    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_arenaPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>创建或重新映射 arena 文件。</summary>
    private void CreateMapping(int capacity)
    {
        var fileSize = (long)capacity * _vectorByteSize;
        if (fileSize <= 0)
            fileSize = (long)InitialCapacity * _vectorByteSize;

        _mmf = MemoryMappedFile.CreateFromFile(
            _arenaPath, FileMode.OpenOrCreate, mapName: null,
            capacity: fileSize, MemoryMappedFileAccess.ReadWrite);

        _accessor = _mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _basePtr = ptr;
    }

    /// <summary>释放当前映射资源。</summary>
    private void ReleaseMapping()
    {
        if (_accessor != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _accessor = null;
        }

        if (_mmf != null)
        {
            _mmf.Dispose();
            _mmf = null;
        }

        _basePtr = null;
    }

    /// <inheritdoc />
    public void Dispose() => ReleaseMapping();
}
