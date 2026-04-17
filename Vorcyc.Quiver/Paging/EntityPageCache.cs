using System.Reflection;
using System.Runtime.InteropServices;

namespace Vorcyc.Quiver.Paging;

/// <summary>
/// 实体分页缓存，支持两种运行模式：
/// <list type="bullet">
///   <item><b>FullMemory 模式</b>（默认）：所有实体常驻内存字典，行为与原版完全一致，零额外开销。</item>
///   <item><b>LazyLoading 模式</b>：内存中最多保留 <see cref="_maxPages"/> 页，
///   超限时将最久未使用的冷页序列化到页文件（.page），按需读回，
///   实现可控的内存上限。</item>
/// </list>
/// <para>
/// 无论哪种模式，外部接口完全相同：<see cref="Set"/>、<see cref="TryGetValue"/>、
/// <see cref="Remove"/>、<see cref="Clear"/>、<see cref="Count"/>、<see cref="Values"/>。
/// 上层代码无需感知内部模式。
/// </para>
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
internal sealed class EntityPageCache<TEntity> : IDisposable
    where TEntity : class, new()
{
    // ──────────────────────────────────────────────────────────────
    // 常量
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 页文件的魔数，用于格式校验。对应 ASCII "QVPG"（Quiver Page）。
    /// </summary>
    private const uint Magic = 0x51_56_50_47; // "QVPG"

    /// <summary>
    /// 页文件格式版本（v1 = 紧凑二进制）。
    /// <para>
    /// 页文件二进制布局：
    /// <code>
    /// [4B uint32]  Magic = 0x51565047 ("QVPG")
    /// [1B byte]    Version = 0x01
    /// [4B int32]   PropCount              ← 属性描述符数量
    /// PropDescriptor × PropCount:
    ///   [string]   PropName               ← BinaryWriter 长度前缀 UTF-8
    /// [4B int32]   EntityCount            ← 本页实体数
    /// Entity × EntityCount:
    ///   [4B int32] InternalId
    ///   按描述符顺序逐字段：[1B bool null标志] + 字段值（类型编码同 BinaryStorageProvider）
    /// </code>
    /// </para>
    /// </summary>
    private const byte FormatVersion = 1;

    // ──────────────────────────────────────────────────────────────
    // FullMemory 模式专用
    // ──────────────────────────────────────────────────────────────

    /// <summary>FullMemory 模式下的直通字典（即原始 _entities）。</summary>
    private readonly Dictionary<int, TEntity>? _flat;

    // ──────────────────────────────────────────────────────────────
    // LazyLoading 模式专用
    // ──────────────────────────────────────────────────────────────

    /// <summary>内存中的活跃页。键为页 ID，值为页内容。</summary>
    private readonly Dictionary<int, Page>? _loadedPages;

    /// <summary>LRU 链表，头部最热，尾部最冷。存储的是页 ID。</summary>
    private readonly LinkedList<int>? _lru;

    /// <summary>页 ID → LRU 链表节点的映射，O(1) 定位。</summary>
    private readonly Dictionary<int, LinkedListNode<int>>? _lruNodes;

    /// <summary>内部 ID → 页 ID 的全局目录，常驻内存（每条约 8 字节）。</summary>
    private readonly Dictionary<int, int>? _idToPage;

    /// <summary>页 ID → 页文件路径的映射（冷页文件目录）。</summary>
    private readonly Dictionary<int, string>? _pageFiles;

    /// <summary>最大活跃页数量上限，超出时淘汰最冷页。</summary>
    private readonly int _maxPages;

    /// <summary>每页最大实体数量，超出时开新页。</summary>
    private readonly int _pageSize;

    /// <summary>当前页 ID 的最大值（分配新页时递增）。</summary>
    private int _nextPageId;

    /// <summary>当前活跃写入页（新实体写入此页）。</summary>
    private int _currentWritePageId;

    /// <summary>实体属性描述符缓存（属性名 + PropertyInfo），按名称排序，与写文件顺序一致。</summary>
    private static readonly (string Name, PropertyInfo Info)[] PropDescriptors =
        typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => (p.Name, p))
            .ToArray();

    /// <summary>页文件存放目录。</summary>
    private readonly string? _pageDir;

    // ──────────────────────────────────────────────────────────────
    // 共用
    // ──────────────────────────────────────────────────────────────

    /// <summary>是否处于 LazyLoading 模式。</summary>
    public bool IsLazy { get; }

    // ──────────────────────────────────────────────────────────────
    // 构造
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 以 FullMemory 模式初始化（与原版行为完全一致）。
    /// </summary>
    public EntityPageCache()
    {
        IsLazy = false;
        _flat = [];
    }

    /// <summary>
    /// 以 LazyLoading 模式初始化。
    /// </summary>
    /// <param name="pageDir">页文件存放目录，不存在时自动创建。</param>
    /// <param name="maxPages">内存中最多保留的页数，超限时淘汰最冷的页。</param>
    /// <param name="pageSize">每页最大实体数。</param>
    public EntityPageCache(string pageDir, int maxPages, int pageSize)
    {
        IsLazy = true;
        _pageDir = pageDir;
        _maxPages = maxPages > 0 ? maxPages : 8;
        _pageSize = pageSize > 0 ? pageSize : 512;
        _loadedPages = [];
        _lru = new LinkedList<int>();
        _lruNodes = [];
        _idToPage = [];
        _pageFiles = [];
        _currentWritePageId = AllocatePage();
        Directory.CreateDirectory(pageDir);
    }

    // ──────────────────────────────────────────────────────────────
    // 公共接口
    // ──────────────────────────────────────────────────────────────

    /// <summary>当前实体总数。</summary>
    public int Count => IsLazy
        ? _idToPage!.Count
        : _flat!.Count;

    /// <summary>
    /// 写入或更新一个实体。
    /// </summary>
    public void Set(int id, TEntity entity)
    {
        if (!IsLazy)
        {
            _flat![id] = entity;
            return;
        }

        // LazyLoading 模式
        if (_idToPage!.TryGetValue(id, out var existingPageId))
        {
            // 更新已存在的实体
            var page = GetOrLoadPage(existingPageId);
            page.Entities[id] = entity;
            page.IsDirty = true;
            return;
        }

        // 新实体：写入当前写入页
        var writePage = GetOrLoadPage(_currentWritePageId);
        writePage.Entities[id] = entity;
        writePage.IsDirty = true;
        _idToPage[id] = _currentWritePageId;

        // 当前写入页已满，分配新页
        if (writePage.Entities.Count >= _pageSize)
            _currentWritePageId = AllocatePage();
    }

    /// <summary>
    /// 尝试获取实体。命中内存缓存时 O(1)，否则触发页面加载（磁盘 I/O）。
    /// </summary>
    public bool TryGetValue(int id, out TEntity entity)
    {
        if (!IsLazy)
            return _flat!.TryGetValue(id, out entity!);

        if (!_idToPage!.TryGetValue(id, out var pageId))
        {
            entity = null!;
            return false;
        }

        var page = GetOrLoadPage(pageId);
        return page.Entities.TryGetValue(id, out entity!);
    }

    /// <summary>
    /// 删除一个实体。
    /// </summary>
    public bool Remove(int id)
    {
        if (!IsLazy)
            return _flat!.Remove(id);

        if (!_idToPage!.TryGetValue(id, out var pageId))
            return false;

        _idToPage.Remove(id);

        // 若页在内存中，直接从页中移除
        if (_loadedPages!.TryGetValue(pageId, out var page))
        {
            var removed = page.Entities.Remove(id);
            page.IsDirty = true;
            return removed;
        }

        // 页在磁盘上：加载后移除（让 LRU 决定何时写回）
        var diskPage = GetOrLoadPage(pageId);
        var result = diskPage.Entities.Remove(id);
        diskPage.IsDirty = true;
        return result;
    }

    /// <summary>
    /// 清空所有实体并删除所有页文件。
    /// </summary>
    public void Clear()
    {
        if (!IsLazy)
        {
            _flat!.Clear();
            return;
        }

        // 删除所有页文件
        foreach (var filePath in _pageFiles!.Values)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        _loadedPages!.Clear();
        _lru!.Clear();
        _lruNodes!.Clear();
        _idToPage!.Clear();
        _pageFiles.Clear();
        _nextPageId = 0;
        _currentWritePageId = AllocatePage();
    }

    /// <summary>
    /// 枚举所有实体值。在 LazyLoading 模式下，会按顺序加载所有页（可能触发大量 I/O）。
    /// </summary>
    public IEnumerable<TEntity> Values
    {
        get
        {
            if (!IsLazy)
            {
                foreach (var v in _flat!.Values)
                    yield return v;
                yield break;
            }

            // 收集所有页 ID（包括内存页和磁盘页）
            var allPageIds = new HashSet<int>(_idToPage!.Values);
            foreach (var pageId in allPageIds)
            {
                var page = GetOrLoadPage(pageId);
                foreach (var entity in page.Entities.Values)
                    yield return entity;
            }
        }
    }

    /// <summary>
    /// 将所有脏页强制刷回磁盘。在 FullMemory 模式下无操作。
    /// </summary>
    public void FlushDirty()
    {
        if (!IsLazy) return;
        foreach (var (pageId, page) in _loadedPages!)
        {
            if (page.IsDirty)
                WritePage(pageId, page);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // LazyLoading 内部实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 分配一个新页，注册到 LRU 缓存中。
    /// </summary>
    private int AllocatePage()
    {
        var pageId = _nextPageId++;
        var page = new Page();
        AddToLru(pageId, page);
        return pageId;
    }

    /// <summary>
    /// 获取或从磁盘加载指定页，并更新 LRU 热度。
    /// </summary>
    private Page GetOrLoadPage(int pageId)
    {
        // 命中内存缓存
        if (_loadedPages!.TryGetValue(pageId, out var page))
        {
            TouchLru(pageId);
            return page;
        }

        // 缓存已满，先淘汰最冷的页
        if (_loadedPages.Count >= _maxPages)
            EvictColdest();

        // 从磁盘加载
        page = _pageFiles!.TryGetValue(pageId, out var filePath) && File.Exists(filePath)
            ? ReadPage(filePath)
            : new Page();

        AddToLru(pageId, page);
        return page;
    }

    /// <summary>
    /// 淘汰 LRU 尾部最冷的页：若脏则先写回磁盘，再从内存移除。
    /// </summary>
    private void EvictColdest()
    {
        var coldPageId = _lru!.Last!.Value;
        _lru.RemoveLast();
        _lruNodes!.Remove(coldPageId);

        if (_loadedPages!.TryGetValue(coldPageId, out var coldPage))
        {
            if (coldPage.IsDirty)
                WritePage(coldPageId, coldPage);
            _loadedPages.Remove(coldPageId);
        }
    }

    private void AddToLru(int pageId, Page page)
    {
        _loadedPages![pageId] = page;
        _lruNodes![pageId] = _lru!.AddFirst(pageId);
    }

    private void TouchLru(int pageId)
    {
        if (_lruNodes!.TryGetValue(pageId, out var node))
        {
            _lru!.Remove(node);
            _lruNodes[pageId] = _lru.AddFirst(pageId);
        }
    }

    /// <summary>
    /// 将页序列化到页文件（紧凑二进制格式 v1）。
    /// <para>
    /// 格式：[魔数 uint32] [版本 byte=1] [属性数 int32]
    ///   ┗ 每个属性描述符：[属性名 string]
    ///   [实体数 int32]
    ///     ┗ 每个实体：[internalId int32] + 按描述符顺序的属性值（null标志 bool + 数据）
    /// </para>
    /// </summary>
    private void WritePage(int pageId, Page page)
    {
        var filePath = GetPageFilePath(pageId);
        _pageFiles![pageId] = filePath;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: false);
        using var bw = new BinaryWriter(fs);

        bw.Write(Magic);
        bw.Write(FormatVersion);

        // 写属性描述符（名称列表，供读取时按序匹配）
        bw.Write(PropDescriptors.Length);
        foreach (var (name, _) in PropDescriptors)
            bw.Write(name);

        bw.Write(page.Entities.Count);
        foreach (var (id, entity) in page.Entities)
        {
            bw.Write(id);
            foreach (var (_, prop) in PropDescriptors)
                WritePropertyValue(bw, prop.PropertyType, prop.GetValue(entity));
        }

        page.IsDirty = false;
    }

    /// <summary>
    /// 从页文件反序列化页内容。支持 v1（二进制）格式。
    /// </summary>
    private static Page ReadPage(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: false);
        using var br = new BinaryReader(fs);

        var magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Invalid page file format: {filePath}");

        var version = br.ReadByte();
        if (version != 1)
            throw new InvalidDataException($"Unsupported page file version {version}: {filePath}");

        // 读属性描述符，与当前类型属性建立映射
        var propCount = br.ReadInt32();
        var propMap = new PropertyInfo?[propCount];
        var propTypes = new Type?[propCount];
        for (var i = 0; i < propCount; i++)
        {
            var name = br.ReadString();
            var match = Array.Find(PropDescriptors, d => d.Name == name);
            if (match.Info != null)
            {
                propMap[i] = match.Info;
                propTypes[i] = match.Info.PropertyType;
            }
        }

        var count = br.ReadInt32();
        var page = new Page();

        for (var e = 0; e < count; e++)
        {
            var id = br.ReadInt32();
            var entity = new TEntity();
            for (var p = 0; p < propCount; p++)
            {
                var value = ReadPropertyValue(br, propTypes[p]);
                if (value != null && propMap[p] != null)
                    propMap[p]!.SetValue(entity, value);
            }
            page.Entities[id] = entity;
        }

        return page;
    }

    // ── 属性值二进制写入 ──

    private static void WritePropertyValue(BinaryWriter bw, Type type, object? value)
    {
        if (value == null) { bw.Write(false); return; }
        bw.Write(true);

        if (type == typeof(string))          bw.Write((string)value);
        else if (type == typeof(int))         bw.Write((int)value);
        else if (type == typeof(long))        bw.Write((long)value);
        else if (type == typeof(float))       bw.Write((float)value);
        else if (type == typeof(double))      bw.Write((double)value);
        else if (type == typeof(bool))        bw.Write((bool)value);
        else if (type == typeof(DateTime))    bw.Write(((DateTime)value).ToBinary());
        else if (type == typeof(Guid))        bw.Write(((Guid)value).ToByteArray());
        else if (type == typeof(decimal))     bw.Write((decimal)value);
        else if (type == typeof(byte))        bw.Write((byte)value);
        else if (type == typeof(short))       bw.Write((short)value);
        else if (type == typeof(Half))        bw.Write((Half)value);
        else if (type == typeof(DateTimeOffset))
        {
            var dto = (DateTimeOffset)value;
            bw.Write(dto.Ticks);
            bw.Write((short)dto.Offset.TotalMinutes);
        }
        else if (type == typeof(TimeSpan))    bw.Write(((TimeSpan)value).Ticks);
        else if (type == typeof(float[]))
        {
            var arr = (float[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(string[]))
        {
            var arr = (string[])value;
            bw.Write(arr.Length);
            foreach (var s in arr) bw.Write(s);
        }
        else if (type == typeof(byte[]))
        {
            var arr = (byte[])value;
            bw.Write(arr.Length);
            bw.Write(arr);
        }
        else if (type == typeof(double[]))
        {
            var arr = (double[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            // 兜底：toString 存储（理论上不触发，因 QuiverSet 已校验属性类型）
            bw.Write(value.ToString() ?? string.Empty);
        }
    }

    // ── 属性值二进制读取 ──

    private static object? ReadPropertyValue(BinaryReader br, Type? type)
    {
        if (!br.ReadBoolean()) return null;
        if (type == null) { SkipUnknown(br); return null; }

        if (type == typeof(string))           return br.ReadString();
        if (type == typeof(int))              return br.ReadInt32();
        if (type == typeof(long))             return br.ReadInt64();
        if (type == typeof(float))            return br.ReadSingle();
        if (type == typeof(double))           return br.ReadDouble();
        if (type == typeof(bool))             return br.ReadBoolean();
        if (type == typeof(DateTime))         return DateTime.FromBinary(br.ReadInt64());
        if (type == typeof(Guid))             return new Guid(br.ReadBytes(16));
        if (type == typeof(decimal))          return br.ReadDecimal();
        if (type == typeof(byte))             return br.ReadByte();
        if (type == typeof(short))            return br.ReadInt16();
        if (type == typeof(Half))             return br.ReadHalf();
        if (type == typeof(DateTimeOffset))
        {
            var ticks = br.ReadInt64();
            var mins  = br.ReadInt16();
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(mins));
        }
        if (type == typeof(TimeSpan))         return TimeSpan.FromTicks(br.ReadInt64());
        if (type == typeof(float[]))
        {
            var len = br.ReadInt32();
            var bytes = br.ReadBytes(len * sizeof(float));
            var arr = new float[len];
            MemoryMarshal.Cast<byte, float>(bytes).CopyTo(arr);
            return arr;
        }
        if (type == typeof(string[]))
        {
            var len = br.ReadInt32();
            var arr = new string[len];
            for (var i = 0; i < len; i++) arr[i] = br.ReadString();
            return arr;
        }
        if (type == typeof(byte[]))
        {
            var len = br.ReadInt32();
            return br.ReadBytes(len);
        }
        if (type == typeof(double[]))
        {
            var len = br.ReadInt32();
            var bytes = br.ReadBytes(len * sizeof(double));
            var arr = new double[len];
            MemoryMarshal.Cast<byte, double>(bytes).CopyTo(arr);
            return arr;
        }
        // 兜底 string（对应写入时的兜底）
        return br.ReadString();
    }

    /// <summary>跳过未知类型属性值（类型为 null 时用于前向兼容）。读取 string 作为跳过策略。</summary>
    private static void SkipUnknown(BinaryReader br) => br.ReadString();

    private string GetPageFilePath(int pageId)
        => Path.Combine(_pageDir!, $"page_{pageId:D8}.qvpg");

    // ──────────────────────────────────────────────────────────────
    // 内部数据结构
    // ──────────────────────────────────────────────────────────────

    private sealed class Page
    {
        public Dictionary<int, TEntity> Entities { get; } = [];
        public bool IsDirty { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // Dispose
    // ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (IsLazy)
            FlushDirty();
    }
}
