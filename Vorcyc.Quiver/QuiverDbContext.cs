namespace Vorcyc.Quiver;

using System.IO.MemoryMappedFiles;
using System.Reflection;
using Quiver.Storage;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Migration;

/// <summary>
/// Vector database context base class. Similar to EF Core's <c>DbContext</c> pattern,
/// subclasses register entity collections by declaring public properties of type <see cref="QuiverSet{TEntity}"/>.
/// <para>
/// <b>Auto-discovery</b>: During construction, all <c>QuiverSet&lt;T&gt;</c> properties on the subclass are scanned
/// via reflection; instances are automatically created and injected without manual initialization.
/// </para>
/// <para>
    /// <b>Persistence</b>: <see cref="SaveAsync"/> serializes all data to disk as a full snapshot.
    /// Calling it after append-heavy workloads rewrites the file into a compacted snapshot.
/// </para>
/// <para>
/// <b>Lifecycle</b>: Implements <see cref="IDisposable"/> and <see cref="IAsyncDisposable"/>.
/// Both <c>Dispose</c> and <c>DisposeAsync</c> release resources without saving by default.
/// Set <see cref="QuiverDbOptions.SaveOnDispose"/> to <c>true</c> to auto-save on <c>DisposeAsync</c>.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 1. Define the context
/// public class MyDb : QuiverDbContext
/// {
///     public QuiverSet&lt;Document&gt; Documents { get; set; }
///     public MyDb() : base(new QuiverDbOptions
///     {
///         DatabasePath = "my.db"
///     }) { }
/// }
/// </code>
/// </example>
public abstract partial class QuiverDbContext : IDisposable, IAsyncDisposable, IPromotionCoordinator
{
    // ──────────────────────────────────────────────────────────────
    // Internal state
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Entity type → QuiverSet instance mapping.
    /// Key is <c>typeof(TEntity)</c>; value is <c>QuiverSet&lt;TEntity&gt;</c> (stored as object to avoid generic erasure).
    /// </summary>
    private readonly Dictionary<Type, object> _sets = [];

    /// <summary>
    /// Type full name → Type object mapping. Used to look up CLR types from type name strings in persisted files.
    /// Key is <c>Type.FullName</c> (e.g. <c>"MyApp.Document"</c>).
    /// </summary>
    private readonly Dictionary<string, Type> _typeMap = [];

    /// <summary>Binary storage provider (the sole primary storage format).</summary>
    private readonly BinaryStorageProvider _storageProvider = new();

    /// <summary>Database configuration options (path, default metric, etc.).</summary>
    private readonly QuiverDbOptions _options;

    /// <summary>
    /// Cached reflection method references for <c>GetAll</c>.
    /// Used by <see cref="SaveAsync"/> and <see cref="ExportAsync"/> to avoid repeated reflection lookups.
    /// </summary>
    private readonly Dictionary<Type, MethodInfo> _getAllMethodCache = [];

    /// <summary>
    /// Schema migration rules per entity type. Key is CLR entity type; value is the migration rule.
    /// <para>
    /// Registered by subclasses via <see cref="ConfigureMigration{TEntity}"/>.
    /// Applied automatically at load time for transparent Schema migration with property renaming and value transformation.
    /// </para>
    /// </summary>
    private readonly Dictionary<Type, SchemaMigrationRule> _migrations = [];

    /// <summary>Dispose flag: 0 = not disposed, 1 = disposed. Uses <see cref="Interlocked.Exchange"/> for safe concurrent Dispose.</summary>
    private int _disposed;

    /// <summary>
    /// Promotion flight gate per entity type: 1 = a Heap → Mmap promotion task is currently scheduled or running for that type;
    /// 0 = idle. Uses <see cref="Interlocked.CompareExchange(ref int, int, int)"/> for single-flight semantics.
    /// </summary>
    private readonly Dictionary<Type, int> _promotionInFlight = [];

    /// <summary>Reentrancy guard for <see cref="_promotionInFlight"/> map mutation only.</summary>
    private readonly object _promotionLock = new();

    /// <summary>
    /// Creates a context with default options. Default metric is Cosine; primary storage is binary (QDB v3); no persistence path (memory-only mode).
    /// </summary>
    protected QuiverDbContext() : this(new QuiverDbOptions()) { }

    /// <summary>
    /// Creates a context with the specified options.
    /// </summary>
    /// <param name="options">
    /// Database configuration options including storage path, default distance metric, and serialization settings.
    /// </param>
    protected QuiverDbContext(QuiverDbOptions options)
    {
        _options = options;
        options.Validate();

        // Scan subclass properties via reflection and auto-create/inject all QuiverSet<T> instances
        InitializeSets();
    }

    #region Initialization

    /// <summary>
    /// Scans all <c>QuiverSet&lt;T&gt;</c> public properties on the subclass via reflection,
    /// creates the corresponding <see cref="QuiverSet{TEntity}"/> instances, and injects them.
    /// <para>
    /// Processing flow:
    /// <list type="number">
    ///   <item>Filter all properties on the subclass whose type is <c>QuiverSet&lt;T&gt;</c></item>
    ///   <item>Create instances via <c>Activator.CreateInstance</c> using the <c>internal</c> constructor</item>
    ///   <item>Register in <see cref="_sets"/> (by type) and <see cref="_typeMap"/> (by name)</item>
    ///   <item>Inject the instance into the subclass property via <c>PropertyInfo.SetValue</c></item>
    /// </list>
    /// </para>
    /// </summary>
    private void InitializeSets()
    {
        // Scan all QuiverSet<T>-typed properties on the subclass
        var setProperties = GetType()
            .GetProperties()
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(QuiverSet<>));

        foreach (var prop in setProperties)
        {
            // Extract the generic argument T (e.g. QuiverSet<Document> → typeof(Document))
            var entityType = prop.PropertyType.GetGenericArguments()[0];

            // Call the internal constructor of QuiverSet<T> with configuration parameters
            var setInstance = Activator.CreateInstance(
                typeof(QuiverSet<>).MakeGenericType(entityType),
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                 [_options.DatabasePath,
                   _options.LargeFields.MemoryMode,
                    _options.Vectors.MemoryMode, _options.Vectors.MemoryMapThresholdBytes,
                    _options.LargeFields.MaxCachedPayloads],
                null);

            // Register in internal dictionaries
            _sets[entityType] = setInstance!;
            // 注册持久化稳定名 → Type；若类型标注了 [QuiverEntity] 且其 Name 与 FullName 不同，
            // 也把 FullName 作为兼容别名注册，使旧文件（按 FullName 写入）仍能加载。
            var storedName = EntityTypeName.Resolve(entityType);
            _typeMap[storedName] = entityType;
            var legacyAlias = EntityTypeName.ResolveLegacyAliasOrNull(entityType);
            if (legacyAlias is not null)
                _typeMap[legacyAlias] = entityType;

            // Inject into the subclass property
            prop.SetValue(this, setInstance);

            // Cache reflection method references
            _getAllMethodCache[entityType] = setInstance!.GetType()
                .GetMethod("GetAll", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // 注入升级协调器：set 越限时回调 NotifyHeapBytesChanged，由 context 决定是否触发 mmap 升级。
            var attachM = setInstance!.GetType()
                .GetMethod("AttachPromotionCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
            attachM?.Invoke(setInstance, [this]);
        }
    }

    #endregion

    #region Schema Migration Configuration

    /// <summary>
    /// Registers Schema migration rules for the specified entity type.
    /// <para>
    /// Call this method in the subclass constructor to declare property rename and value transformation rules.
    /// These rules are automatically applied at load time (<see cref="LoadAsync"/>) for transparent Schema migration.
    /// </para>
    /// <para>
    /// <b>Simple scenarios (adding/removing fields) do not require configuration</b> — new fields default automatically; removed fields are silently skipped.
    /// This method is only needed when renaming fields or converting value types.
    /// </para>
    /// </summary>
    /// <typeparam name="TEntity">The entity type to configure migration for.</typeparam>
    /// <param name="configure">Configuration delegate for the migration builder.</param>
    /// <example>
    /// <code>
    /// public class MyDb : QuiverDbContext
    /// {
    ///     public QuiverSet&lt;Document&gt; Documents { get; set; }
    ///     public MyDb() : base(new QuiverDbOptions { DatabasePath = "my.db" })
    ///     {
    ///         ConfigureMigration&lt;Document&gt;(m => m
    ///             .RenameProperty("OldTitle", "Title")
    ///             .TransformValue("Score", v => v is int i ? (double)i : v));
    ///     }
    /// }
    /// </code>
    /// </example>
    protected void ConfigureMigration<TEntity>(Action<MigrationBuilder<TEntity>> configure)
        where TEntity : class, new()
    {
        var builder = new MigrationBuilder<TEntity>();
        configure(builder);
        _migrations[typeof(TEntity)] = builder.Rule;
    }

    #endregion

    #region Set Accessor

    /// <summary>
    /// Gets the vector collection for the specified entity type. Equivalent to accessing the subclass property directly, but supports dynamic type lookup.
    /// </summary>
    /// <typeparam name="TEntity">The entity type. A corresponding <c>QuiverSet&lt;TEntity&gt;</c> property must be declared in the subclass.</typeparam>
    /// <returns>The corresponding <see cref="QuiverSet{TEntity}"/> instance.</returns>
    /// <exception cref="InvalidOperationException">No QuiverSet for the specified type is registered in this context.</exception>
    /// <example>
    /// <code>
    /// // Both approaches are equivalent:
    /// var set1 = db.Documents;             // Direct property access
    /// var set2 = db.Set&lt;Document&gt;();  // Generic method access
    /// </code>
    /// </example>
    public QuiverSet<TEntity> Set<TEntity>() where TEntity : class, new()
    {
        if (_sets.TryGetValue(typeof(TEntity), out var set))
            return (QuiverSet<TEntity>)set;

        throw new InvalidOperationException($"No QuiverSet<{typeof(TEntity).Name}> found in context.");
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Asynchronously saves all vector collection data to disk as a full snapshot.
    /// </summary>
    /// <param name="path">
    /// The save path. When <c>null</c>, <see cref="QuiverDbOptions.DatabasePath"/> is used.
    /// </param>
    /// <exception cref="ArgumentException">Both <paramref name="path"/> and <c>DatabasePath</c> are empty.</exception>
    public async Task SaveAsync(string? path = null)
    {
        var filePath = path ?? _options.DatabasePath;
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        // Collect entity data from all QuiverSets
        var setsData = new Dictionary<string, (Type Type, List<object> Entities)>();
        var largeFieldSlices = new Dictionary<string, Vorcyc.Quiver.Payloads.ILargeFieldSliceSource>(StringComparer.Ordinal);

        foreach (var (type, set) in _sets)
        {
            var getAll = _getAllMethodCache[type];
            var entities = ((IEnumerable<object>)getAll.Invoke(set, null)!).ToList();
            var storedName = EntityTypeName.Resolve(type);
            setsData[storedName] = (type, entities);

            var captureSlices = set.GetType().GetMethod("CaptureLargeFieldSliceSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
            if (captureSlices?.Invoke(set, null) is Vorcyc.Quiver.Payloads.ILargeFieldSliceSource sliceSource)
                largeFieldSlices[storedName] = sliceSource;

            // Full rewrite supersedes pending tombstones — they are already absent from the snapshot.
            var clear = set.GetType().GetMethod("ClearPendingTombstones", BindingFlags.Instance | BindingFlags.NonPublic);
            clear?.Invoke(set, null);
        }

        bool useMmap = _options.Vectors.MemoryMode != GlobalVectorMemoryMode.InMemory;

        // Atomic write: write to a temp file first, then replace the original on success
        var tempPath = filePath + ".tmp";
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 收集每个 set 的 IVectorIndex 快照写入器（HNSW 等支持快照的索引才会真正产出 payload）。
        var snapshotWriters = new Dictionary<string, IReadOnlyList<(string FieldName, int NodeCount, Func<System.IO.BinaryWriter, bool> Writer)>>(_sets.Count, StringComparer.Ordinal);
        foreach (var (type, set) in _sets)
        {
            var enumM = set.GetType().GetMethod("EnumerateIndexSnapshotWriters", BindingFlags.Instance | BindingFlags.NonPublic);
            if (enumM is null) continue;
            var writers = (IReadOnlyList<(string, int, Func<System.IO.BinaryWriter, bool>)>?)enumM.Invoke(set, null);
            if (writers is null || writers.Count == 0) continue;
            snapshotWriters[EntityTypeName.Resolve(type)] = writers;
        }

        // 写入 tempPath 时，mmap 视图仍然绑定到原文件 filePath，这样非 InMemory 的向量字段
        // 可以在 WriteVectorBlobSegment 内通过属性 getter -> LazyVectorAccessor.Materialize ->
        // MmapVectorStore.Get(id) 正常拿到向量。若在此之前就 Clear() 掉 mmap，所有 lazy 向量都会
        // 读出 null，导致写盘的 VectorBlob 全行被标记为 null 并填零（即“写出 0 向量”）。
        await _storageProvider.SaveAsync(tempPath, setsData, snapshotWriters.Count > 0 ? snapshotWriters : null,
            largeFieldSlices.Count > 0 ? largeFieldSlices : null);

        // mmap 模式：在 File.Move 覆盖原文件前释放所有 MmapVectorStore 视图，否则 Windows 会持有
        // 目标文件的句柄阻止覆盖；写完成功后再 rebind 到新文件。
        if (useMmap)
        {
            foreach (var set in _sets.Values)
            {
                var m = set.GetType().GetMethod("DisposeMmapStoresForSave", BindingFlags.Instance | BindingFlags.NonPublic);
                m?.Invoke(set, null);
            }
        }

        File.Move(tempPath, filePath, overwrite: true);

        if (useMmap)
        {
            // 重新读取 footer，提取 VectorBlob 段元信息并 rebind 每个 set。
            var regions = BinaryStorageProvider.ReadVectorBlobRegions(filePath);
            if (regions.Count > 0)
            {
                foreach (var (type, set) in _sets)
                {
                    var rebind = set.GetType().GetMethod("RebindMmapStoresAfterSave", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (rebind is null) continue;
                    Func<MemoryMappedFile> opener = () => MemoryMappedFile.CreateFromFile(
                        filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
                    rebind.Invoke(set, [regions, opener]);
                }
            }
        }
    }

    /// <summary>
    /// Appends all currently in-memory entities as new segments to an existing v4 (<c>QDB\x04</c>) file
    /// without rewriting earlier segments. The file's footer is rewritten in place.
    /// <para>
    /// This is the v4 incremental persistence path that replaces pre-4.0 WAL semantics:
    /// large bulk writes can be flushed batch-by-batch with bounded I/O cost. Use
    /// <see cref="SaveAsync"/> periodically to defragment the resulting multi-segment file.
    /// </para>
    /// </summary>
    /// <param name="path">Target file path; defaults to <see cref="QuiverDbOptions.DatabasePath"/>.</param>
    /// <exception cref="InvalidDataException">Target file exists and is not a v4 file.</exception>
    public async Task AppendAsync(string? path = null)
    {
        var filePath = path ?? _options.DatabasePath;
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var setsData = new Dictionary<string, (Type Type, List<object> Entities)>();
        var tombstones = new Dictionary<string, int[]>();
        foreach (var (type, set) in _sets)
        {
            var getAll = _getAllMethodCache[type];
            var entities = ((IEnumerable<object>)getAll.Invoke(set, null)!).ToList();
            var storedName = EntityTypeName.Resolve(type);
            if (entities.Count > 0)
                setsData[storedName] = (type, entities);

            var drain = set.GetType().GetMethod("DrainPendingTombstones", BindingFlags.Instance | BindingFlags.NonPublic);
            if (drain?.Invoke(set, null) is int[] dead && dead.Length > 0)
                tombstones[storedName] = dead;
        }

        if (setsData.Count == 0 && tombstones.Count == 0) return;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await _storageProvider.AppendAsync(filePath, setsData, tombstones);

        // Background auto-merge: if enabled, check the file's current segment count and tombstone ratio
        // against configured thresholds and trigger a Rewrite (full snapshot defragmentation) when exceeded.
        if (_options.EnableBackgroundMerge)
            await MaybeAutoMergeAsync(filePath);
    }

    /// <summary>
    /// Flushes only pending tombstones (accumulated by <c>Remove</c>/<c>RemoveByKey</c> calls)
    /// as a <c>SegmentKind.Tombstone</c> segment, without re-writing any in-memory entities as
    /// new <c>EntityMeta</c>/<c>VectorBlob</c> segments. Useful for the "load → mutate in place → flush"
    /// pattern where <see cref="AppendAsync"/> would otherwise duplicate already-persisted live rows.
    /// <para>
    /// On the next <see cref="LoadAsync"/> the tombstoned internal-row ids are filtered out of the
    /// loaded set; a subsequent <see cref="SaveAsync"/> physically removes them.
    /// </para>
    /// </summary>
    /// <param name="path">Target file path; defaults to <see cref="QuiverDbOptions.DatabasePath"/>.</param>
    public async Task FlushTombstonesAsync(string? path = null)
    {
        var filePath = path ?? _options.DatabasePath;
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var tombstones = new Dictionary<string, int[]>();
        foreach (var (type, set) in _sets)
        {
            var drain = set.GetType().GetMethod("DrainPendingTombstones", BindingFlags.Instance | BindingFlags.NonPublic);
            if (drain?.Invoke(set, null) is int[] dead && dead.Length > 0)
                tombstones[EntityTypeName.Resolve(type)] = dead;
        }

        if (tombstones.Count == 0) return;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await _storageProvider.AppendAsync(filePath, new Dictionary<string, (Type, List<object>)>(), tombstones);

        if (_options.EnableBackgroundMerge)
            await MaybeAutoMergeAsync(filePath);
    }

    /// <summary>
    /// Inspects the v4 file's footer and triggers a <see cref="SaveAsync"/> when either the segment
    /// count exceeds <see cref="QuiverDbOptions.AutoMergeMaxSegments"/> or the tombstone-to-live ratio
    /// exceeds <see cref="QuiverDbOptions.AutoMergeTombstoneRatio"/>. Runs synchronously inline so that
    /// callers awaiting <see cref="AppendAsync"/> observe the post-merge state.
    /// </summary>
    private async Task MaybeAutoMergeAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var info = await QuiverDbFile.InspectAsync(filePath, verifyCrc: false);
            int segCount = info.Segments.Count;
            long liveEntities = 0;
            long tombstones = 0;
            foreach (var s in info.Segments)
            {
                if (s.Kind == Vorcyc.Quiver.Files.SegmentKind.Mixed
                    || s.Kind == Vorcyc.Quiver.Files.SegmentKind.EntityMeta)
                    liveEntities += s.EntityCount;
                else if (s.Kind == Vorcyc.Quiver.Files.SegmentKind.Tombstone)
                    tombstones += s.EntityCount;
            }
            double ratio = liveEntities > 0 ? (double)tombstones / liveEntities : 0.0;
            bool exceedSeg = segCount >= _options.AutoMergeMaxSegments;
            bool exceedRatio = ratio >= _options.AutoMergeTombstoneRatio;
            if (exceedSeg || exceedRatio)
                await SaveAsync(filePath);
        }
        catch (Exception ex)
        {
            // Auto-merge is best-effort; never propagate failures back into the user's Append call.
            // 但仍记录到 Trace，避免静默丢失诊断信息。
            System.Diagnostics.Trace.TraceWarning(
                "[Vorcyc.Quiver] Background auto-merge for '{0}' failed: {1}", filePath, ex);
        }
    }

    /// <summary>
    /// Asynchronously loads data from disk into all vector collections.
    /// <para>
    /// Returns silently (without throwing) when the file does not exist; suitable for first-run scenarios.
    /// </para>
    /// </summary>
    /// <param name="path">
    /// The load path. When <c>null</c>, <see cref="QuiverDbOptions.DatabasePath"/> is used.
    /// </param>
    public async Task LoadAsync(string? path = null)
    {
        var filePath = path ?? _options.DatabasePath;

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        // ── Unknown-type detection ────────────────────────────────────────────────
        // 在真正的 Load 之前，扫一遍 v4 footer，把"文件里存在但当前 context 未注册"的
        // TypeName 找出来。历史行为是 BinaryStorageProvider.LoadV4 直接 break 跳过这些段，
        // 调用方拿到一个"看起来加载成功但实际为空"的数据库——这是最容易让人踩坑的失败模式。
        // 用 UnknownTypeHandling 选项让用户显式选择策略。
        if (_options.UnknownTypeHandling != UnknownTypeHandling.Ignore)
        {
            try
            {
                var info = await QuiverDbFile.InspectAsync(filePath, verifyCrc: false);
                if (info.FormatVersion == 4 && info.Segments.Count > 0)
                {
                    var unknown = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var seg in info.Segments)
                    {
                        if (string.IsNullOrEmpty(seg.TypeName)) continue;
                        if (_typeMap.ContainsKey(seg.TypeName)) continue;
                        // 只统计承载实体的段类型；纯 VectorBlob 段没有 entity 计数，但 TypeName 与
                        // 同一类型的 Mixed/EntityMeta 段一致，会在上一行被命中。
                        if (seg.EntityCount <= 0) continue;
                        unknown.TryGetValue(seg.TypeName, out var prev);
                        unknown[seg.TypeName] = prev + seg.EntityCount;
                    }

                    if (unknown.Count > 0)
                    {
                        var detail = string.Join(", ",
                            unknown.Select(kv => $"\"{kv.Key}\" ({kv.Value} entities)"));
                        var known = _typeMap.Count == 0
                            ? "(none)"
                            : string.Join(", ", _typeMap.Keys.Select(k => $"\"{k}\""));

                        if (_options.UnknownTypeHandling == UnknownTypeHandling.Throw)
                        {
                            throw new InvalidOperationException(
                                $"Quiver: file '{filePath}' contains entity segment(s) {detail} " +
                                $"whose stored TypeName is not registered on this {GetType().Name}. " +
                                $"Registered types: {known}. " +
                                $"Fix options: (a) add a matching QuiverSet<T> property whose stored type name (from [QuiverEntity] or Type.FullName) equals the stored TypeName; " +
                                $"or (b) re-run QuiverMigrator.MigrateAsync with a typeMap that re-keys the segment; " +
                                $"or (c) set QuiverDbOptions.UnknownTypeHandling = UnknownTypeHandling.Ignore to suppress this error.");
                        }
                        else // Warn
                        {
                            System.Diagnostics.Trace.TraceWarning(
                                "[Vorcyc.Quiver] File '{0}' contains entity segment(s) {1} whose stored TypeName is not registered (registered: {2}). These segments will be skipped. Set QuiverDbOptions.UnknownTypeHandling = Throw to fail loudly, or Ignore to suppress this warning.",
                                filePath, detail, known);
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Re-throw our own deliberate error.
                throw;
            }
            catch (Exception ex)
            {
                // Inspection is purely advisory; never block load on inspect failures.
                System.Diagnostics.Trace.TraceWarning(
                    "[Vorcyc.Quiver] Pre-load inspect for unknown-type detection failed on '{0}': {1}",
                    filePath, ex.Message);
            }
        }

        // Build migration rule dictionary (type full name → rule), only for registered types
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null;
        if (_migrations.Count > 0)
        {
            migrationRules = _migrations.ToDictionary(
                kv => EntityTypeName.Resolve(kv.Key),
                kv => kv.Value);
        }

        // ── mmap-aware path: build a (typeName, fieldName) predicate from each set's IsMmapField. ──
        // 当 GlobalVectorMemoryMode 为 MemoryMapped/Auto/PerField 且 set 中存在以 MmapVectorStore 持有的字段时，
        // BinaryStorageProvider 会跳过该字段的 float[] 物化并输出 MmapVectorRegion。
        bool useMmap = _options.Vectors.MemoryMode != GlobalVectorMemoryMode.InMemory;
        bool useLazyLargeFields = _options.LargeFields.MemoryMode != GlobalLargeFieldMemoryMode.InMemory;
        Dictionary<string, List<object>> loadedSets;
        List<Vorcyc.Quiver.Storage.MmapVectorRegion>? mmapRegions = null;
        List<Vorcyc.Quiver.Storage.LargeFieldRegion>? largeFieldRegions = null;

        if (useMmap || useLazyLargeFields)
        {
            // 收集每个 (typeName → mmap field set) 的快照，构造高速谓词。
            var perTypeMmapFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var perTypeLazyLargeFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (type, set) in _sets)
            {
                var isMmapM = set.GetType().GetMethod("IsMmapField", BindingFlags.Instance | BindingFlags.NonPublic);
                var namesP  = set.GetType().GetProperty("VectorFieldNames", BindingFlags.Instance | BindingFlags.NonPublic);
                if (isMmapM is not null && namesP is not null)
                {
                    var names = (IEnumerable<string>)namesP.GetValue(set)!;
                    var bag = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var n in names)
                        if ((bool)isMmapM.Invoke(set, [n])!) bag.Add(n);
                    if (bag.Count > 0)
                    {
                        perTypeMmapFields[EntityTypeName.Resolve(type)] = bag;
                        var alias = EntityTypeName.ResolveLegacyAliasOrNull(type);
                        if (alias is not null) perTypeMmapFields[alias] = bag;
                    }
                }

                var isLazyLargeM = set.GetType().GetMethod("IsLazyLargeField", BindingFlags.Instance | BindingFlags.NonPublic);
                var largeNamesP = set.GetType().GetProperty("LargeFieldNames", BindingFlags.Instance | BindingFlags.NonPublic);
                if (isLazyLargeM is not null && largeNamesP is not null)
                {
                    var names = (IEnumerable<string>)largeNamesP.GetValue(set)!;
                    var bag = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var n in names)
                        if ((bool)isLazyLargeM.Invoke(set, [n])!) bag.Add(n);
                    if (bag.Count > 0)
                    {
                        perTypeLazyLargeFields[EntityTypeName.Resolve(type)] = bag;
                        var alias = EntityTypeName.ResolveLegacyAliasOrNull(type);
                        if (alias is not null) perTypeLazyLargeFields[alias] = bag;
                    }
                }
            }

            if (perTypeMmapFields.Count > 0 || perTypeLazyLargeFields.Count > 0)
            {
                bool MmapPredicate(string tn, string fn)
                    => perTypeMmapFields.TryGetValue(tn, out var s) && s.Contains(fn);
                bool LargeFieldPredicate(string tn, string fn)
                    => perTypeLazyLargeFields.TryGetValue(tn, out var s) && s.Contains(fn);

                var tuple = await _storageProvider.LoadAsync(filePath, _typeMap, MmapPredicate, LargeFieldPredicate, migrationRules);
                loadedSets = tuple.Sets;
                mmapRegions = tuple.VectorRegions;
                largeFieldRegions = tuple.LargeFieldRegions;
            }
            else
            {
                loadedSets = await _storageProvider.LoadAsync(filePath, _typeMap, migrationRules);
            }
        }
        else
        {
            loadedSets = await _storageProvider.LoadAsync(filePath, _typeMap, migrationRules);
        }

        // 预先扫一遍 IndexSnapshot 段，给每个 (typeName → (field → coveredNextId)) 建一张表。
        // 实体加载完成前应用快照恢复索引拓扑，从而对已覆盖的 id 跳过在线 Add(id) 重建。
        var snapshotCoveredByType = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        try
        {
            var rawSnapshots = Vorcyc.Quiver.Storage.BinaryStorageProvider.ReadIndexSnapshots(filePath);
            if (rawSnapshots.Count > 0)
            {
                // 按 typeName 聚合 (fieldName → payload)
                var perType = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
                foreach (var ((tn, fn), payload) in rawSnapshots)
                {
                    if (!perType.TryGetValue(tn, out var bag))
                        perType[tn] = bag = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    bag[fn] = payload;
                }
                foreach (var (tn, bag) in perType)
                {
                    if (!_typeMap.TryGetValue(tn, out var type)) continue;
                    if (!_sets.TryGetValue(type, out var set)) continue;
                    var applyM = set.GetType().GetMethod("ApplyIndexSnapshots", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (applyM is null) continue;
                    var covered = (IReadOnlyDictionary<string, int>?)applyM.Invoke(set, [bag]);
                    if (covered is { Count: > 0 }) snapshotCoveredByType[tn] = covered;
                }
            }
        }
        catch (Exception ex)
        {
            // 快照解析失败不应阻止加载，记录后回退到全量在线重建路径。
            System.Diagnostics.Trace.TraceWarning(
                "[Vorcyc.Quiver] Failed to read HNSW index snapshots from '{0}': {1}. Falling back to online rebuild.",
                filePath, ex.Message);
        }

        foreach (var (typeName, entities) in loadedSets)
        {
            if (!_typeMap.TryGetValue(typeName, out var type) || !_sets.TryGetValue(type, out var set))
                continue;

            // Apply value transform rules (if any)
            if (_migrations.TryGetValue(type, out var rule) && rule.ValueTransforms.Count > 0)
            {
                foreach (var entity in entities)
                {
                    if (entity is null) continue; // tombstoned
                    foreach (var (propName, transform) in rule.ValueTransforms)
                    {
                        var prop = type.GetProperty(propName);
                        if (prop == null) continue;
                        var oldValue = prop.GetValue(entity);
                        var newValue = transform(oldValue);
                        if (!Equals(oldValue, newValue))
                            prop.SetValue(entity, newValue);
                    }
                }
            }

            // Collect this type's mmap regions (if any) and dispatch to LoadEntitiesMmap; otherwise plain LoadEntities.
            List<Vorcyc.Quiver.Storage.MmapVectorRegion>? typeRegions = null;
            if (mmapRegions is not null && mmapRegions.Count > 0)
            {
                typeRegions = mmapRegions.Where(r => r.TypeName == typeName).ToList();
                if (typeRegions.Count == 0) typeRegions = null;
            }

            snapshotCoveredByType.TryGetValue(typeName, out var snapshotCovered);

            if (typeRegions is not null)
                InvokeLoadEntitiesMmap(set, type, entities, typeRegions, filePath, snapshotCovered);
            else
                InvokeLoadEntities(set, type, entities, snapshotCovered);

            if (largeFieldRegions is { Count: > 0 })
            {
                var typeLargeRegions = largeFieldRegions.Where(r => r.TypeName == typeName).ToList();
                if (typeLargeRegions.Count > 0)
                    InvokeBindLargeFieldRegions(set, typeLargeRegions, filePath);
            }
        }
    }

    private static void InvokeBindLargeFieldRegions(object set, List<Vorcyc.Quiver.Storage.LargeFieldRegion> regions, string filePath)
    {
        var bindMethod = set.GetType().GetMethod("BindLargeFieldRegions", BindingFlags.Instance | BindingFlags.NonPublic,
            types: [typeof(IReadOnlyList<Vorcyc.Quiver.Storage.LargeFieldRegion>), typeof(string)])!;
        bindMethod.Invoke(set, [regions, filePath]);
    }

    /// <summary>反射调用 <c>QuiverSet&lt;T&gt;.LoadEntities(IEnumerable&lt;T&gt;, IReadOnlyDictionary&lt;string, int&gt;?)</c>。</summary>
    private static void InvokeLoadEntities(object set, Type entityType, List<object> entities, IReadOnlyDictionary<string, int>? snapshotCoveredNextIdByField)
    {
        var enumerableT = typeof(IEnumerable<>).MakeGenericType(entityType);
        var loadMethod = set.GetType().GetMethod("LoadEntities", BindingFlags.Instance | BindingFlags.NonPublic,
            types: [enumerableT, typeof(IReadOnlyDictionary<string, int>)])!;
        var castMethod = typeof(Enumerable).GetMethod("Cast")!.MakeGenericMethod(entityType);
        var typedEntities = castMethod.Invoke(null, [entities])!;
        loadMethod.Invoke(set, [typedEntities, snapshotCoveredNextIdByField]);
    }

    /// <summary>
    /// 反射调用 <c>QuiverSet&lt;T&gt;.LoadEntitiesMmap(...)</c>，并提供 <c>bindAction</c> 回调：
    /// 该回调收到 <c>(field → region, rowIds)</c> 后打开 <paramref name="filePath"/> 的 <see cref="MemoryMappedFile"/>
    /// 并调用 <c>MmapVectorStore.BindRegion</c>。
    /// </summary>
    private static void InvokeLoadEntitiesMmap(
        object set,
        Type entityType,
        List<object> entities,
        List<Vorcyc.Quiver.Storage.MmapVectorRegion> regions,
        string filePath,
        IReadOnlyDictionary<string, int>? snapshotCoveredNextIdByField)
    {
        // 把 List<object> 转成 IReadOnlyList<T>
        var castMethod = typeof(Enumerable).GetMethod("Cast")!.MakeGenericMethod(entityType);
        var typedEnumerable = castMethod.Invoke(null, [entities])!;
        var toListMethod = typeof(Enumerable).GetMethod("ToList")!.MakeGenericMethod(entityType);
        var typedList = toListMethod.Invoke(null, [typedEnumerable])!;

        // 同一 (type, field) 可能由多次 Append 产生多个 VectorBlob region；按字段聚合成列表传给 LoadEntitiesMmap。
        var regionDict = new Dictionary<string, IReadOnlyList<Vorcyc.Quiver.Storage.MmapVectorRegion>>(StringComparer.Ordinal);
        var grouped = new Dictionary<string, List<Vorcyc.Quiver.Storage.MmapVectorRegion>>(StringComparer.Ordinal);
        foreach (var r in regions)
        {
            if (!grouped.TryGetValue(r.FieldName, out var list))
                grouped[r.FieldName] = list = new List<Vorcyc.Quiver.Storage.MmapVectorRegion>();
            list.Add(r);
        }
        foreach (var (k, v) in grouped) regionDict[k] = v;

        // bindAction: 对每个字段，按 region 列表逐一打开 mmap 并调用 BindRegion。
        Action<IReadOnlyDictionary<string, IReadOnlyList<(Vorcyc.Quiver.Storage.MmapVectorRegion Region, int[] RowIds)>>> bindAction = bindings =>
        {
            if (bindings.Count == 0) return;
            var vectorStoresField = set.GetType()
                .GetField("_vectorStores", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var vectorStores = (System.Collections.IDictionary)vectorStoresField.GetValue(set)!;

            foreach (var (fieldName, items) in bindings)
            {
                var raw = vectorStores[fieldName] as Vorcyc.Quiver.Indexing.IVectorStore;
                var mstore = Vorcyc.Quiver.Indexing.VectorStoreSlot.As<Vorcyc.Quiver.Indexing.MmapVectorStore>(raw);
                if (mstore is null) continue;
                foreach (var (region, rowIds) in items)
                {
                    var mmf = MemoryMappedFile.CreateFromFile(
                        filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
                    mstore.BindRegion(mmf, region.PayloadOffset, region.RowCount, rowIds,
                        region.Encoding, region.StorageDim > 0 ? region.StorageDim : region.Dim, region.Sq8Scales);
                }
            }
        };

        var loadMmapM = set.GetType().GetMethod("LoadEntitiesMmap", BindingFlags.Instance | BindingFlags.NonPublic,
            types: [
                typeof(IReadOnlyList<>).MakeGenericType(entityType),
                typeof(IReadOnlyDictionary<string, IReadOnlyList<Vorcyc.Quiver.Storage.MmapVectorRegion>>),
                typeof(Action<IReadOnlyDictionary<string, IReadOnlyList<(Vorcyc.Quiver.Storage.MmapVectorRegion Region, int[] RowIds)>>>),
                typeof(IReadOnlyDictionary<string, int>)
            ])!;
        loadMmapM.Invoke(set, [typedList, regionDict, bindAction, snapshotCoveredNextIdByField]);
    }

    #endregion

    #region Export / Import

    /// <summary>
    /// Asynchronously exports all entities in the current database to the specified file.
    /// <para>
    /// The export format is human-readable JSON or XML, suitable for data backup, cross-platform exchange, or manual inspection.
    /// The primary storage format (binary QDB) is not affected.
    /// </para>
    /// </summary>
    /// <param name="filePath">The export target file path.</param>
    /// <param name="format">The export format. Default is <see cref="ExportFormat.Json"/>.</param>
    /// <param name="jsonOptions">
    /// Serialization options applied only when using <see cref="ExportFormat.Json"/>.
    /// When <c>null</c>, default options (indented + camelCase) are used.
    /// </param>
    public async Task ExportAsync(
        string filePath,
        ExportFormat format = ExportFormat.Json,
        System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        var provider = Vorcyc.Quiver.Storage.ExportStorageProviderFactory.Create(format, jsonOptions);

        var setsData = new Dictionary<string, (Type Type, List<object> Entities)>();
        foreach (var (type, set) in _sets)
        {
            var getAll = _getAllMethodCache[type];
            var entities = ((IEnumerable<object>)getAll.Invoke(set, null)!).ToList();
            setsData[EntityTypeName.Resolve(type)] = (type, entities);
        }

        await provider.SaveAsync(filePath, setsData);
    }

    /// <summary>
    /// Asynchronously imports data from a JSON or XML file, merging it into the current database.
    /// <para>
    /// Each record is upserted (updated if it exists, inserted if it does not); existing data is not cleared.
    /// Schema migration rules are also applied to imported data.
    /// </para>
    /// </summary>
    /// <param name="filePath">The source file path.</param>
    /// <param name="format">The source file format. Default is <see cref="ExportFormat.Json"/>.</param>
    /// <param name="jsonOptions">
    /// Deserialization options applied only when using <see cref="ExportFormat.Json"/>.
    /// When <c>null</c>, default options (indented + camelCase) are used.
    /// </param>
    public async Task ImportAsync(
        string filePath,
        ExportFormat format = ExportFormat.Json,
        System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        var provider = Vorcyc.Quiver.Storage.ExportStorageProviderFactory.Create(format, jsonOptions);

        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null;
        if (_migrations.Count > 0)
            migrationRules = _migrations.ToDictionary(kv => EntityTypeName.Resolve(kv.Key), kv => kv.Value);

        var loadedSets = await provider.LoadAsync(filePath, _typeMap, migrationRules);

        foreach (var (typeName, entities) in loadedSets)
        {
            if (!_typeMap.TryGetValue(typeName, out var type) || !_sets.TryGetValue(type, out var set))
                continue;

            // Apply value transform rules
            if (_migrations.TryGetValue(type, out var rule) && rule.ValueTransforms.Count > 0)
            {
                foreach (var entity in entities)
                    foreach (var (propName, transform) in rule.ValueTransforms)
                    {
                        var prop = type.GetProperty(propName);
                        if (prop == null) continue;
                        var oldValue = prop.GetValue(entity);
                        var newValue = transform(oldValue);
                        if (!Equals(oldValue, newValue))
                            prop.SetValue(entity, newValue);
                    }
            }

            // Upsert each record
            var upsertMethod = set.GetType().GetMethod("Upsert", BindingFlags.Instance | BindingFlags.Public)!;
            foreach (var entity in entities)
                upsertMethod.Invoke(set, [entity]);
        }
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Synchronously releases all vector collection resources. Data is <b>not</b> saved automatically.
    /// To save, call <see cref="SaveAsync"/> manually before disposing.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var set in _sets.Values)
            if (set is IDisposable d) d.Dispose();
        _sets.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases resources.
    /// <para>
    /// 默认行为：<b>不自动保存</b>，仅释放资源。这避免在 <see cref="AppendAsync"/> 配合阶段性 <c>Clear()</c>
    /// 的批量导入场景下，<see cref="SaveAsync"/> 的全量 rewrite 把磁盘上 Append 累积的数据反向覆盖为
    /// 当前内存快照（甚至空快照）。
    /// </para>
    /// <para>
    /// 若希望旧版"Dispose 即自动保存"语义，将 <see cref="QuiverDbOptions.SaveOnDispose"/> 显式设为 <c>true</c>。
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// await using var db = new MyDb();
    /// // ... operate on data ...
    /// await db.SaveAsync(); // 显式保存
    /// </code>
    /// </example>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 仅在显式启用 SaveOnDispose 时才自动落盘；默认 false，避免覆盖 Append 累积的数据。
        if (_options.SaveOnDispose && !string.IsNullOrEmpty(_options.DatabasePath))
            await SaveAsync();

        // Then release all QuiverSet instances
        foreach (var set in _sets.Values)
            if (set is IDisposable d) d.Dispose();
        _sets.Clear();
        GC.SuppressFinalize(this);
    }

    #endregion
}