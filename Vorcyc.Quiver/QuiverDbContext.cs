namespace Vorcyc.Quiver;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quiver.Storage;
using Quiver.Storage.Wal;

/// <summary>
/// Vector database context base class. Similar to EF Core's <c>DbContext</c> pattern,
/// subclasses register entity collections by declaring public properties of type <see cref="QuiverSet{TEntity}"/>.
/// <para>
/// <b>Auto-discovery</b>: During construction, all <c>QuiverSet&lt;T&gt;</c> properties on the subclass are scanned
/// via reflection; instances are automatically created and injected without manual initialization.
/// </para>
/// <para>
/// <b>Persistence</b>: Two modes are supported:
/// <list type="bullet">
///   <item><b>Full mode</b> (default): <see cref="SaveAsync"/> fully serializes all data to disk.</item>
///   <item><b>WAL incremental mode</b>: <see cref="SaveChangesAsync"/> appends only changes to the WAL file
///   at O(Δ) complexity; <see cref="CompactAsync"/> creates a full snapshot and clears the WAL.</item>
/// </list>
/// </para>
/// <para>
/// <b>Lifecycle</b>: Implements <see cref="IDisposable"/> and <see cref="IAsyncDisposable"/>.
/// Synchronous Dispose releases resources only; asynchronous DisposeAsync saves first, then releases.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 1. Define the context (with WAL enabled)
/// public class MyDb : QuiverDbContext                    
/// {
///     public QuiverSet&lt;Document&gt; Documents { get; set; } 
///     public MyDb() : base(new QuiverDbOptions           
///     {
///         DatabasePath = "my.db",
///         EnableWal = true
///     }) { }
/// }
/// </code>
/// </example>
public abstract class QuiverDbContext : IDisposable, IAsyncDisposable
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

    /// <summary>WAL instance. Non-null only when <see cref="QuiverDbOptions.EnableWal"/> is <c>true</c>.</summary>
    private WriteAheadLog? _wal;

    /// <summary>JSON serialization options for WAL payloads. Uses camelCase naming to match snapshot JSON format.</summary>
    private static readonly JsonSerializerOptions WalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Cached reflection method references to avoid repeated lookup on each <see cref="SaveChangesAsync"/> call.
    /// Key is entity type; value is a reference to the <c>DrainChanges</c> method.
    /// </summary>
    private readonly Dictionary<Type, MethodInfo> _drainMethodCache = [];

    /// <summary>
    /// Cached reflection method references for <c>GetAll</c>.
    /// Used by <see cref="SaveAsync"/> and <see cref="ExportAsync"/> to avoid repeated reflection lookups.
    /// </summary>
    private readonly Dictionary<Type, MethodInfo> _getAllMethodCache = [];

    /// <summary>
    /// Cached primary key property types, used to deserialize primary keys during WAL replay.
    /// Key is entity type full name; value is the <see cref="PropertyInfo"/> of the primary key property.
    /// </summary>
    private readonly Dictionary<string, PropertyInfo> _keyPropCache = [];

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

        // Initialize WAL (if enabled and a database path is configured)
        if (options.EnableWal && !string.IsNullOrEmpty(options.DatabasePath))
            _wal = new WriteAheadLog(options.DatabasePath + ".wal");
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
                 _options.EntityCache, _options.MaxCachedPages, _options.PageSize],
                null);

            // Register in internal dictionaries
            _sets[entityType] = setInstance!;
            _typeMap[entityType.FullName!] = entityType;

            // Inject into the subclass property
            prop.SetValue(this, setInstance);

            // Cache reflection method references
            _drainMethodCache[entityType] = setInstance!.GetType()
                .GetMethod("DrainChanges", BindingFlags.Instance | BindingFlags.NonPublic)!;

            _getAllMethodCache[entityType] = setInstance!.GetType()
                .GetMethod("GetAll", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Cache primary key property info (used to deserialize keys during WAL replay)
            var keyProp = entityType.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<QuiverKeyAttribute>() != null);
            if (keyProp != null)
                _keyPropCache[entityType.FullName!] = keyProp;
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
    /// <para>
    /// When WAL is enabled, this method also performs compaction: it creates a full snapshot and clears the WAL.
    /// Equivalent to <see cref="CompactAsync"/>.
    /// </para>
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

        foreach (var (type, set) in _sets)
        {
            var getAll = _getAllMethodCache[type];
            var entities = ((IEnumerable<object>)getAll.Invoke(set, null)!).ToList();
            setsData[type.FullName!] = (type, entities);
        }

        // Atomic write: write to a temp file first, then replace the original on success
        var tempPath = filePath + ".tmp";
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await _storageProvider.SaveAsync(tempPath, setsData);
        File.Move(tempPath, filePath, overwrite: true);

        // After successful snapshot, clear the WAL and pending change logs
        if (_wal != null)
        {
            _wal.Truncate();

            // Clear the pending change log in each QuiverSet (already covered by the snapshot)
            foreach (var (type, set) in _sets)
            {
                if (_drainMethodCache.TryGetValue(type, out var drainMethod))
                    drainMethod.Invoke(set, null);
            }
        }
    }

    /// <summary>
    /// Appends only unpersisted changes incrementally to the WAL file. O(Δ) complexity.
    /// <para>
    /// This is the primary save method in WAL mode and is orders of magnitude faster than <see cref="SaveAsync"/>.
    /// When WAL is not enabled, this method behaves identically to <see cref="SaveAsync"/> (full save).
    /// </para>
    /// <para>
    /// When the WAL record count exceeds <see cref="QuiverDbOptions.WalCompactionThreshold"/>,
    /// <see cref="CompactAsync"/> is triggered automatically to create a full snapshot and clear the WAL.
    /// </para>
    /// </summary>
    public async Task SaveChangesAsync()
    {
        // Fall back to full save when WAL is not enabled
        if (_wal == null)
        {
            await SaveAsync();
            return;
        }

        // Collect changes from all QuiverSets
        var walEntries = new List<WalEntry>();
        foreach (var (type, set) in _sets)
        {
            if (!_drainMethodCache.TryGetValue(type, out var drainMethod))
                continue;

            var changes = (List<(byte Op, object? Key, object? Entity)>)drainMethod.Invoke(set, null)!;
            if (changes.Count == 0)
                continue;

            var typeName = type.FullName!;

            foreach (var (op, key, entity) in changes)
            {
                var payloadJson = op switch
                {
                    1 => JsonSerializer.Serialize(entity, type, WalJsonOptions),    // Add
                    2 => JsonSerializer.Serialize(key, WalJsonOptions),              // Remove
                    _ => string.Empty                                                // Clear
                };
                walEntries.Add(new WalEntry((WalOperation)op, typeName, payloadJson));
            }
        }

        if (walEntries.Count > 0)
        {
            // Batch-append to WAL (single fsync)
            await Task.Run(() => _wal.Append(walEntries, _options.WalFlushToDisk));
        }

        // Auto-compact: create a full snapshot when WAL record count exceeds threshold
        if (_wal.RecordCount >= _options.WalCompactionThreshold)
            await CompactAsync();
    }

    /// <summary>
    /// Performs compaction: creates a full snapshot and clears the WAL. Equivalent to <see cref="SaveAsync"/>.
    /// <para>
    /// Suitable for the following scenarios:
    /// <list type="bullet">
    ///   <item>Periodic compaction via a scheduled task</item>
    ///   <item>Manual trigger when the WAL file becomes too large</item>
    ///   <item>Ensuring the snapshot is up-to-date before application shutdown</item>
    /// </list>
    /// </para>
    /// </summary>
    public Task CompactAsync() => SaveAsync();

    /// <summary>
    /// Asynchronously loads data from disk into all vector collections.
    /// <para>
    /// When WAL is enabled, loading is a two-phase process:
    /// <list type="number">
    ///   <item>Read the full snapshot and restore entities and indices</item>
    ///   <item>Read the WAL file and replay incremental changes in order</item>
    /// </list>
    /// </para>
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

        // ── Phase 1: Load full snapshot ──
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            // Build migration rule dictionary (type full name → rule), only for registered types
            IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null;
            if (_migrations.Count > 0)
            {
                migrationRules = _migrations.ToDictionary(
                    kv => kv.Key.FullName!,
                    kv => kv.Value);
            }

            var loadedSets = await _storageProvider.LoadAsync(filePath, _typeMap, migrationRules);

            foreach (var (typeName, entities) in loadedSets)
            {
                if (!_typeMap.TryGetValue(typeName, out var type) || !_sets.TryGetValue(type, out var set))
                    continue;

                // Apply value transform rules (if any)
                if (_migrations.TryGetValue(type, out var rule) && rule.ValueTransforms.Count > 0)
                {
                    foreach (var entity in entities)
                    {
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

                var loadMethod = set.GetType().GetMethod("LoadEntities", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var castMethod = typeof(Enumerable).GetMethod("Cast")!.MakeGenericMethod(type);
                var typedEntities = castMethod.Invoke(null, [entities])!;
                loadMethod.Invoke(set, [typedEntities]);
            }
        }

        // ── Phase 2: Replay WAL incremental changes ──
        if (_wal != null)
            ReplayWal(_wal.FilePath);
    }

    /// <summary>
    /// Replays all valid records from the WAL file, applying incremental changes to the corresponding vector collections.
    /// <para>
    /// During replay, <c>ReplayAdd/ReplayRemove/ReplayClear</c> methods are used
    /// without triggering change log recording, to avoid circular writes.
    /// </para>
    /// <para>
    /// When Schema migration rules are present, property renames (correcting JSON property names)
    /// and value transforms (applied to deserialized entities via <see cref="SchemaMigrationRule.ValueTransforms"/>) are automatically applied.
    /// </para>
    /// </summary>
    private void ReplayWal(string walFilePath)
    {
        var entries = WriteAheadLog.ReadAll(walFilePath);
        if (entries.Count == 0) return;

        foreach (var entry in entries)
        {
            // Find the target type and QuiverSet instance
            if (!_typeMap.TryGetValue(entry.TypeName, out var type) ||
                !_sets.TryGetValue(type, out var set))
                continue;

            // Retrieve migration rules (may be null)
            _migrations.TryGetValue(type, out var rule);

            switch (entry.Operation)
            {
                case WalOperation.Add:
                    {
                        var json = entry.PayloadJson;

                            // When rename rules are present, fix old property names in the WAL JSON
                            if (rule != null && rule.PropertyRenames.Count > 0)                            // When rename rules are present, fix old property names in the WAL JSON
                        {
                            var node = JsonNode.Parse(json)!.AsObject();
                            var namingPolicy = WalJsonOptions.PropertyNamingPolicy;
                            foreach (var (oldName, newName) in rule.PropertyRenames)
                            {
                                var oldJsonName = namingPolicy?.ConvertName(oldName) ?? oldName;
                                var newJsonName = namingPolicy?.ConvertName(newName) ?? newName;
                                if (node.ContainsKey(oldJsonName))
                                {
                                    var val = node[oldJsonName];
                                    node.Remove(oldJsonName);
                                    node[newJsonName] = val?.DeepClone();
                                }
                            }
                            json = node.ToJsonString(WalJsonOptions);
                        }

                        // Deserialize the entity
                        var entity = JsonSerializer.Deserialize(json, type, WalJsonOptions);
                        if (entity == null) continue;

                        // Apply value transform rules
                        if (rule != null && rule.ValueTransforms.Count > 0)
                        {
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

                        var replayAdd = set.GetType().GetMethod("ReplayAdd", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        replayAdd.Invoke(set, [entity]);
                        break;
                    }
                case WalOperation.Remove:
                    {
                        // Deserialize the primary key and call ReplayRemove
                        if (!_keyPropCache.TryGetValue(entry.TypeName, out var keyProp)) continue;
                        var key = JsonSerializer.Deserialize(entry.PayloadJson, keyProp.PropertyType, WalJsonOptions);
                        if (key == null) continue;
                        var replayRemove = set.GetType().GetMethod("ReplayRemove", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        replayRemove.Invoke(set, [key]);
                        break;
                    }
                case WalOperation.Clear:
                    {
                        var replayClear = set.GetType().GetMethod("ReplayClear", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        replayClear.Invoke(set, null);
                        break;
                    }
            }
        }
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
            setsData[type.FullName!] = (type, entities);
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
            migrationRules = _migrations.ToDictionary(kv => kv.Key.FullName!, kv => kv.Value);

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

    #region Memory compaction

    /// <summary>
    /// Flushes all dirty pages in every <see cref="QuiverSet{TEntity}"/> to disk and evicts all loaded pages
    /// from memory across the entire context, minimizing the overall memory footprint.
    /// Pages are reloaded from disk transparently on next access.
    /// <para>
    /// No-op for collections operating in <see cref="EntityCacheMode.FullMemory"/> mode.
    /// Vector index structures are not affected and always remain in memory.
    /// </para>
    /// </summary>
    public async Task CompactAllMemoryAsync()
    {
        var tasks = new List<Task>(_sets.Count);
        foreach (var set in _sets.Values)
        {
            var method = set.GetType().GetMethod("CompactMemoryAsync", BindingFlags.Instance | BindingFlags.Public);
            if (method?.Invoke(set, null) is Task t)
                tasks.Add(t);
        }
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Synchronously releases all vector collection resources. Data is <b>not</b> saved automatically.
    /// To save, call <see cref="SaveChangesAsync"/> manually before disposing, or use <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _wal?.Dispose();

        foreach (var set in _sets.Values)
            if (set is IDisposable d) d.Dispose();
        _sets.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases resources. <b>Data is saved automatically</b> before releasing all vector collections.
    /// Recommended for use with <c>await using</c> to ensure no data is lost.
    /// <para>
    /// When WAL is enabled, calls <see cref="SaveChangesAsync"/> (incremental save);
    /// otherwise calls <see cref="SaveAsync"/> (full save).
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// await using var db = new MyDb();
    /// // ... operate on data ...
    /// // DisposeAsync is called automatically at end of scope — saves then releases
    /// </code>
    /// </example>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Save data to disk first (memory-only mode has null DatabasePath — skip saving)
        if (!string.IsNullOrEmpty(_options.DatabasePath))
        {
            if (_wal != null)
                await SaveChangesAsync();
            else
                await SaveAsync();
        }

        _wal?.Dispose();

        // Then release all QuiverSet instances
        foreach (var set in _sets.Values)
            if (set is IDisposable d) d.Dispose();
        _sets.Clear();
        GC.SuppressFinalize(this);
    }

    #endregion
}