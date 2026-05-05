using System.Reflection;
using System.Runtime.InteropServices;

namespace Vorcyc.Quiver.Paging;

/// <summary>
/// Entity paging cache that supports two operating modes:
/// <list type="bullet">
///   <item><b>FullMemory mode</b> (default): all entities reside in an in-memory dictionary, behavior identical to the original, zero additional overhead.</item>
///   <item><b>LazyLoading mode</b>: at most <see cref="_maxPages"/> pages are kept in memory;
///   when the limit is exceeded, the least-recently-used cold page is serialized to a page file (.qvpg) and reloaded on demand,
///   providing a controllable memory ceiling.</item>
/// </list>
/// <para>
/// Regardless of mode, the external interface is identical: <see cref="Set"/>, <see cref="TryGetValue"/>,
/// <see cref="Remove"/>, <see cref="Clear"/>, <see cref="Count"/>, <see cref="Values"/>.
/// Upper-layer code does not need to be aware of the internal mode.
/// </para>
/// </summary>
/// <typeparam name="TEntity">Entity type.</typeparam>
internal sealed class EntityPageCache<TEntity> : IDisposable
    where TEntity : class, new()
{
    // ──────────────────────────────────────────────────────────────
    // Constants
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Magic number for page file format validation. Corresponds to ASCII "QVPG" (Quiver Page).
    /// </summary>
    private const uint Magic = 0x51_56_50_47; // "QVPG"

    /// <summary>
    /// Page file format version (v1 = compact binary).
    /// <para>
    /// Page file binary layout:
    /// <code>
    /// [4B uint32]  Magic = 0x51565047 ("QVPG")
    /// [1B byte]    Version = 0x01
    /// [4B int32]   PropCount              ← number of property descriptors
    /// PropDescriptor × PropCount:
    ///   [string]   PropName               ← BinaryWriter length-prefixed UTF-8
    /// [4B int32]   EntityCount            ← number of entities on this page
    /// Entity × EntityCount:
    ///   [4B int32] InternalId
    ///   per-descriptor fields in order: [1B bool null flag] + field value (type encoding same as BinaryStorageProvider)
    /// </code>
    /// </para>
    /// </summary>
    private const byte FormatVersion = 1;

    // ──────────────────────────────────────────────────────────────
    // FullMemory mode only
    // ──────────────────────────────────────────────────────────────

    /// <summary>Pass-through dictionary in FullMemory mode (equivalent to the original _entities).</summary>
    private readonly Dictionary<int, TEntity>? _flat;

    // ──────────────────────────────────────────────────────────────
    // LazyLoading mode only
    // ──────────────────────────────────────────────────────────────

    /// <summary>Active pages in memory. Key is page ID, value is page content.</summary>
    private readonly Dictionary<int, Page>? _loadedPages;

    /// <summary>LRU linked list; head is hottest, tail is coldest. Stores page IDs.</summary>
    private readonly LinkedList<int>? _lru;

    /// <summary>Page ID → LRU linked-list node mapping for O(1) lookup.</summary>
    private readonly Dictionary<int, LinkedListNode<int>>? _lruNodes;

    /// <summary>Global directory of internal ID → page ID, always resident in memory (≈8 bytes per entry).</summary>
    private readonly Dictionary<int, int>? _idToPage;

    /// <summary>Page ID → page file path mapping (cold-page file directory).</summary>
    private readonly Dictionary<int, string>? _pageFiles;

    /// <summary>Maximum number of active pages; the coldest page is evicted when this limit is exceeded.</summary>
    private readonly int _maxPages;

    /// <summary>Maximum number of entities per page; a new page is allocated when this is exceeded.</summary>
    private readonly int _pageSize;

    /// <summary>Highest allocated page ID (incremented when a new page is created).</summary>
    private int _nextPageId;

    /// <summary>Currently active write page (new entities are written here).</summary>
    private int _currentWritePageId;

    /// <summary>Cached entity property descriptors (property name + PropertyInfo), sorted by name to match file write order.</summary>
    private static readonly (string Name, PropertyInfo Info)[] PropDescriptors =
        typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => (p.Name, p))
            .ToArray();

    /// <summary>Directory where page files are stored.</summary>
    private readonly string? _pageDir;

    // ──────────────────────────────────────────────────────────────
    // Shared
    // ──────────────────────────────────────────────────────────────

    /// <summary>Whether the cache is operating in LazyLoading mode.</summary>
    public bool IsLazy { get; }

    // ──────────────────────────────────────────────────────────────
    // Construction
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the cache in FullMemory mode (behavior identical to the original).
    /// </summary>
    public EntityPageCache()
    {
        IsLazy = false;
        _flat = [];
    }

    /// <summary>
    /// Initializes the cache in LazyLoading mode.
    /// </summary>
    /// <param name="pageDir">Directory where page files are stored; created automatically if it does not exist.</param>
    /// <param name="maxPages">Maximum number of pages to keep in memory; the coldest page is evicted when exceeded.</param>
    /// <param name="pageSize">Maximum number of entities per page.</param>
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
    // Public interface
    // ──────────────────────────────────────────────────────────────

    /// <summary>Total number of entities currently stored.</summary>
    public int Count => IsLazy
        ? _idToPage!.Count
        : _flat!.Count;

    /// <summary>
    /// Writes or updates an entity.
    /// </summary>
    public void Set(int id, TEntity entity)
    {
        if (!IsLazy)
        {
            _flat![id] = entity;
            return;
        }

        // LazyLoading mode
        if (_idToPage!.TryGetValue(id, out var existingPageId))
        {
            // Update existing entity
            var page = GetOrLoadPage(existingPageId);
            page.Entities[id] = entity;
            page.IsDirty = true;
            return;
        }

        // New entity: write to the current write page
        var writePage = GetOrLoadPage(_currentWritePageId);
        writePage.Entities[id] = entity;
        writePage.IsDirty = true;
        _idToPage[id] = _currentWritePageId;

        // Current write page is full — allocate a new one
        if (writePage.Entities.Count >= _pageSize)
            _currentWritePageId = AllocatePage();
    }

    /// <summary>
    /// Attempts to retrieve an entity. O(1) on memory cache hit; otherwise triggers a page load (disk I/O).
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
    /// Removes an entity.
    /// </summary>
    public bool Remove(int id)
    {
        if (!IsLazy)
            return _flat!.Remove(id);

        if (!_idToPage!.TryGetValue(id, out var pageId))
            return false;

        _idToPage.Remove(id);

        // Page is in memory — remove directly
        if (_loadedPages!.TryGetValue(pageId, out var page))
        {
            var removed = page.Entities.Remove(id);
            page.IsDirty = true;
            return removed;
        }

        // Page is on disk: load it and remove (let LRU decide when to write back)
        var diskPage = GetOrLoadPage(pageId);
        var result = diskPage.Entities.Remove(id);
        diskPage.IsDirty = true;
        return result;
    }

    /// <summary>
    /// Clears all entities and deletes all page files.
    /// </summary>
    public void Clear()
    {
        if (!IsLazy)
        {
            _flat!.Clear();
            return;
        }

        // Delete all page files
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
    /// Enumerates all entity values. In LazyLoading mode, all pages are loaded sequentially (may trigger significant I/O).
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

            // Collect all page IDs (both in-memory and on-disk pages)
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
    /// Flushes all dirty pages to disk. No-op in FullMemory mode.
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

    /// <summary>
    /// Flushes all dirty pages to disk and evicts all loaded pages from memory, minimizing the memory footprint.
    /// Pages are reloaded from disk on next access.
    /// No-op in FullMemory mode.
    /// <para>
    /// <b>Note</b>: Vector index structures are not affected and always remain in memory.
    /// </para>
    /// </summary>
    public void CompactMemory()
    {
        if (!IsLazy) return;

        // 1. Write back all dirty pages
        foreach (var (pageId, page) in _loadedPages!)
        {
            if (page.IsDirty)
                WritePage(pageId, page);
        }

        // 2. Evict all loaded pages from memory; directory indices (_idToPage, _pageFiles) are preserved
        _loadedPages.Clear();
        _lru!.Clear();
        _lruNodes!.Clear();

        // 3. Allocate a fresh write page so subsequent writes can proceed immediately
        _currentWritePageId = AllocatePage();
    }

    // ──────────────────────────────────────────────────────────────
    // LazyLoading internals
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Allocates a new page and registers it in the LRU cache.
    /// </summary>
    private int AllocatePage()
    {
        var pageId = _nextPageId++;
        var page = new Page();
        AddToLru(pageId, page);
        return pageId;
    }

    /// <summary>
    /// Gets or loads the specified page from disk, updating its LRU position.
    /// </summary>
    private Page GetOrLoadPage(int pageId)
    {
        // Memory cache hit
        if (_loadedPages!.TryGetValue(pageId, out var page))
        {
            TouchLru(pageId);
            return page;
        }

        // Cache full — evict the coldest page first
        if (_loadedPages.Count >= _maxPages)
            EvictColdest();

        // Load from disk
        page = _pageFiles!.TryGetValue(pageId, out var filePath) && File.Exists(filePath)
            ? ReadPage(filePath)
            : new Page();

        AddToLru(pageId, page);
        return page;
    }

    /// <summary>
    /// Evicts the coldest (LRU tail) page: writes it back to disk if dirty, then removes it from memory.
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
    /// Serializes a page to a page file (compact binary format v1).
    /// <para>
    /// Format: [magic uint32] [version byte=1] [prop count int32]
    ///   ┗ per property descriptor: [property name string]
    ///   [entity count int32]
    ///     ┗ per entity: [internalId int32] + property values in descriptor order (null flag bool + data)
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

        // Write property descriptors (name list for ordered matching when reading)
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
    /// Deserializes page content from a page file. Supports v1 (binary) format.
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

        // Read property descriptors and build a mapping to the current type's properties
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

    // ── Property value binary write ──

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
            // Fallback: store as toString() (should never be reached since QuiverSet validates property types)
            bw.Write(value.ToString() ?? string.Empty);
        }
    }

    // ── Property value binary read ──

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
        // Fallback string (matches the write-side fallback)
        return br.ReadString();
    }

    /// <summary>Skips an unknown-type property value (used for forward compatibility when type is null). Reads a string as the skip strategy.</summary>
    private static void SkipUnknown(BinaryReader br) => br.ReadString();

    private string GetPageFilePath(int pageId)
        => Path.Combine(_pageDir!, $"page_{pageId:D8}.qvpg");

    // ──────────────────────────────────────────────────────────────
    // Internal data structures
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
