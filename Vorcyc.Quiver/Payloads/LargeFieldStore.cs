using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 文件后端的大字段切片仓库，负责 lazy 物化和可选的 LRU 负载缓存。
/// </summary>
internal sealed class LargeFieldStore : IDisposable
{
    /// <summary>LRU 缓存节点中保存的键和值。</summary>
    private sealed class CacheEntry
    {
        /// <summary>缓存键：实体内部 ID 与字段名。</summary>
        public (int Id, string FieldName) Key { get; }

        /// <summary>缓存的原始负载字节。</summary>
        public byte[] Value { get; }

        /// <summary>创建缓存项。</summary>
        public CacheEntry((int Id, string FieldName) key, byte[] value)
        {
            Key = key;
            Value = value;
        }
    }

    private readonly Dictionary<(int Id, string FieldName), LargeFieldSlice> _slices = [];
    private readonly Dictionary<(int Id, string FieldName), LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _lru = [];
    private readonly int _maxCachedPayloads;
    private readonly bool _cacheEnabled;
    private readonly object _gate = new();

    /// <summary>
    /// 创建大字段仓库。
    /// </summary>
    /// <param name="cacheEnabled">是否启用 LRU 缓存。</param>
    /// <param name="maxCachedPayloads">最多缓存的大字段负载数量。</param>
    public LargeFieldStore(bool cacheEnabled, int maxCachedPayloads = 128)
    {
        _cacheEnabled = cacheEnabled;
        _maxCachedPayloads = Math.Max(1, maxCachedPayloads);
    }

    /// <summary>
    /// 将一个 <see cref="LargeFieldRegion"/> 绑定为指定字段的可读取切片集合。
    /// </summary>
    /// <param name="fieldName">大字段字段名。</param>
    /// <param name="filePath">区域所在数据库文件路径。</param>
    /// <param name="region">大字段段区域描述。</param>
    /// <param name="rowIds">区域行号到实体内部 ID 的映射；<c>-1</c> 表示 tombstone 行。</param>
    public void Bind(string fieldName, string filePath, LargeFieldRegion region, int[] rowIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            var n = Math.Min(region.RowCount, rowIds.Length);
            for (int row = 0; row < n; row++)
            {
                var id = rowIds[row];
                if (id < 0) continue;

                var isNull = region.NullBitmap is { } nb && ((nb[row >> 3] >> (row & 7)) & 1) != 0;
                var start = region.Offsets[row];
                var end = region.Offsets[row + 1];
                var length = checked((int)(end - start));
                _slices[(id, fieldName)] = new LargeFieldSlice(filePath, region.PayloadOffset + start, length, isNull);
            }
        }
    }

    /// <summary>
    /// 查找指定实体与字段的原文件切片，用于保存时复用未物化的大字段数据。
    /// </summary>
    /// <param name="id">实体内部 ID。</param>
    /// <param name="fieldName">大字段字段名。</param>
    /// <param name="slice">找到时返回切片元数据。</param>
    public bool TryGetSlice(int id, string fieldName, out LargeFieldSlice slice)
    {
        lock (_gate)
            return _slices.TryGetValue((id, fieldName), out slice);
    }

    /// <summary>
    /// 按实体内部 ID 和字段名读取大字段负载；启用缓存时会维护 LRU 顺序。
    /// </summary>
    /// <param name="id">实体内部 ID。</param>
    /// <param name="fieldName">大字段字段名。</param>
    public byte[]? Get(int id, string fieldName)
    {
        var key = (id, fieldName);
        LargeFieldSlice slice;
        lock (_gate)
        {
            if (_cacheEnabled && _cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return (byte[])node.Value.Value.Clone();
            }

            if (!_slices.TryGetValue(key, out slice!)) return null;
            if (slice.IsNull) return null;
        }

        var value = ReadSlice(slice);
        if (_cacheEnabled)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                }
                else
                {
                    var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, value));
                    _lru.AddFirst(node);
                    _cache[key] = node;
                    while (_cache.Count > _maxCachedPayloads)
                    {
                        var last = _lru.Last;
                        if (last is null) break;
                        _lru.RemoveLast();
                        _cache.Remove(last.Value.Key);
                    }
                }
            }
        }

        return (byte[])value.Clone();
    }

    /// <summary>
    /// 清空所有切片绑定和缓存内容。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _slices.Clear();
            _cache.Clear();
            _lru.Clear();
        }
    }

    /// <summary>释放仓库并清空内部状态。</summary>
    public void Dispose() => Clear();

    private static byte[] ReadSlice(LargeFieldSlice slice)
    {
        if (slice.Length == 0) return [];
        var buffer = new byte[slice.Length];
        using var fs = new FileStream(slice.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        fs.Position = slice.Offset;
        fs.ReadExactly(buffer);
        return buffer;
    }
}
