namespace Vorcyc.Quiver;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quiver.Storage;
using Quiver.Storage.Wal;

/// <summary>
/// 向量数据库上下文基类。类似 EF Core 的 <c>DbContext</c> 模式，
/// 子类通过声明 <see cref="QuiverSet{TEntity}"/> 类型的公共属性来注册实体集合。
/// <para>
/// <b>自动发现</b>：构造时通过反射扫描子类的所有 <c>QuiverSet&lt;T&gt;</c> 属性，
/// 自动创建实例并注入，无需手动初始化。
/// </para>
/// <para>
/// <b>持久化</b>：支持两种模式：
/// <list type="bullet">
///   <item><b>全量模式</b>（默认）：<see cref="SaveAsync"/> 全量序列化到磁盘。</item>
///   <item><b>WAL 增量模式</b>：<see cref="SaveChangesAsync"/> 仅将变更追加到 WAL 文件，
///   O(Δ) 复杂度；<see cref="CompactAsync"/> 创建全量快照并清空 WAL。</item>
/// </list>
/// </para>
/// <para>
/// <b>生命周期</b>：实现 <see cref="IDisposable"/> 和 <see cref="IAsyncDisposable"/>。
/// 同步 Dispose 仅释放资源，异步 DisposeAsync 会先自动保存再释放。
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 1. 定义上下文（启用 WAL）
/// public class MyDb : QuiverDbContext                    
/// {
///     public QuiverSet&lt;Document&gt; Documents { get; set; } 
///     public MyDb() : base(new QuiverDbOptions           
///     {
///         DatabasePath = "my.db",
///         EnableWal = true,
///         StorageFormat = StorageFormat.Binary
///     }) { }
/// }
/// </code>
/// </example>
public abstract class QuiverDbContext : IDisposable, IAsyncDisposable
{
    // ──────────────────────────────────────────────────────────────
    // 内部状态
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 实体类型 → QuiverSet 实例的映射。
    /// 键为 <c>typeof(TEntity)</c>，值为 <c>QuiverSet&lt;TEntity&gt;</c>（以 object 存储避免泛型擦除）。
    /// </summary>
    private readonly Dictionary<Type, object> _sets = [];

    /// <summary>
    /// 类型全名 → 类型对象的映射。用于从持久化文件中的类型名称字符串反查 CLR 类型。
    /// 键为 <c>Type.FullName</c>（如 <c>"MyApp.Document"</c>）。
    /// </summary>
    private readonly Dictionary<string, Type> _typeMap = [];

    /// <summary>存储提供者实例，根据 <see cref="QuiverDbOptions.StorageFormat"/> 创建。</summary>
    private readonly IStorageProvider _storageProvider;

    /// <summary>数据库配置选项（路径、默认度量、存储格式等）。</summary>
    private readonly QuiverDbOptions _options;

    /// <summary>WAL 实例。仅当 <see cref="QuiverDbOptions.EnableWal"/> 为 <c>true</c> 时非空。</summary>
    private WriteAheadLog? _wal;

    /// <summary>WAL 载荷序列化选项。使用驼峰命名与快照 JSON 格式保持一致。</summary>
    private static readonly JsonSerializerOptions WalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 缓存的反射方法引用，避免每次调用 <see cref="SaveChangesAsync"/> 时重复反射查找。
    /// 键为实体类型，值为 <c>DrainChanges</c> 方法引用。
    /// </summary>
    private readonly Dictionary<Type, MethodInfo> _drainMethodCache = [];

    /// <summary>
    /// 缓存的主键属性类型，用于 WAL 回放时反序列化主键。
    /// 键为实体类型全名，值为主键属性的 <see cref="PropertyInfo"/>。
    /// </summary>
    private readonly Dictionary<string, PropertyInfo> _keyPropCache = [];

    /// <summary>
    /// 各实体类型的 Schema 迁移规则。键为实体 CLR 类型，值为迁移规则。
    /// <para>
    /// 通过子类调用 <see cref="ConfigureMigration{TEntity}"/> 注册。
    /// 加载时自动应用属性重命名和值转换，实现透明的 Schema 迁移。
    /// </para>
    /// </summary>
    private readonly Dictionary<Type, SchemaMigrationRule> _migrations = [];

    /// <summary>是否已释放。防止重复释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 使用默认选项创建上下文。默认存储格式为 JSON，默认度量为 Cosine。
    /// </summary>
    protected QuiverDbContext() : this(new QuiverDbOptions()) { }

    /// <summary>
    /// 使用指定选项创建上下文。
    /// </summary>
    /// <param name="options">
    /// 数据库配置选项。包含存储路径、默认距离度量、存储格式、JSON 序列化选项等。
    /// </param>
    protected QuiverDbContext(QuiverDbOptions options)
    {
        _options = options;
        _storageProvider = StorageProviderFactory.Create(options);

        // 反射扫描子类属性，自动创建并注入所有 QuiverSet<T> 实例
        InitializeSets();

        // 初始化 WAL（如果启用且配置了数据库路径）
        if (options.EnableWal && !string.IsNullOrEmpty(options.DatabasePath))
            _wal = new WriteAheadLog(options.DatabasePath + ".wal");
    }

    #region 初始化

    /// <summary>
    /// 通过反射扫描子类的所有 <c>QuiverSet&lt;T&gt;</c> 公共属性，
    /// 为每个属性创建对应的 <see cref="QuiverSet{TEntity}"/> 实例并注入。
    /// <para>
    /// 处理流程：
    /// <list type="number">
    ///   <item>筛选子类中所有类型为 <c>QuiverSet&lt;T&gt;</c> 的属性</item>
    ///   <item>通过 <c>Activator.CreateInstance</c> 调用 <c>internal</c> 构造函数创建实例</item>
    ///   <item>注册到 <see cref="_sets"/>（按类型查找）和 <see cref="_typeMap"/>（按名称查找）</item>
    ///   <item>通过 <c>PropertyInfo.SetValue</c> 将实例注入子类属性</item>
    /// </list>
    /// </para>
    /// </summary>
    private void InitializeSets()
    {
        // 扫描子类中所有 QuiverSet<T> 类型的属性
        var setProperties = GetType()
            .GetProperties()
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(QuiverSet<>));

        foreach (var prop in setProperties)
        {
            // 提取泛型参数 T（如 QuiverSet<Document> → typeof(Document)）
            var entityType = prop.PropertyType.GetGenericArguments()[0];

            // 调用 QuiverSet<T> 的 internal 构造函数，传入默认度量参数
            var setInstance = Activator.CreateInstance(
                typeof(QuiverSet<>).MakeGenericType(entityType),
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [_options.DefaultMetric],
                null);

            // 注册到内部字典
            _sets[entityType] = setInstance!;
            _typeMap[entityType.FullName!] = entityType;

            // 注入到子类的属性上
            prop.SetValue(this, setInstance);

            // 缓存反射方法引用
            _drainMethodCache[entityType] = setInstance!.GetType()
                .GetMethod("DrainChanges", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // 缓存主键属性信息（WAL 回放反序列化主键时使用）
            var keyProp = entityType.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<QuiverKeyAttribute>() != null);
            if (keyProp != null)
                _keyPropCache[entityType.FullName!] = keyProp;
        }
    }

    #endregion

    #region Schema 迁移配置

    /// <summary>
    /// 为指定实体类型注册 Schema 迁移规则。
    /// <para>
    /// 在子类构造函数中调用此方法，声明属性重命名和值转换规则。
    /// 加载时 (<see cref="LoadAsync"/>) 会自动应用这些规则，实现透明的 Schema 迁移。
    /// </para>
    /// <para>
    /// <b>简单场景（加/减字段）无需配置</b>——新增字段自动取默认值，删除字段自动跳过。
    /// 仅在需要重命名字段或转换值类型时才需要调用此方法。
    /// </para>
    /// </summary>
    /// <typeparam name="TEntity">要配置迁移的实体类型。</typeparam>
    /// <param name="configure">迁移构建器的配置委托。</param>
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

    #region 获取 Set

    /// <summary>
    /// 按实体类型获取对应的向量集合。等价于直接访问子类的属性，但支持动态类型查找。
    /// </summary>
    /// <typeparam name="TEntity">实体类型。须在子类中声明对应的 <c>QuiverSet&lt;TEntity&gt;</c> 属性。</typeparam>
    /// <returns>对应的 <see cref="QuiverSet{TEntity}"/> 实例。</returns>
    /// <exception cref="InvalidOperationException">上下文中未注册指定类型的 QuiverSet。</exception>
    /// <example>
    /// <code>
    /// // 以下两种方式等价：
    /// var set1 = db.Documents;             // 直接属性访问
    /// var set2 = db.Set&lt;Document&gt;();  // 泛型方法访问
    /// </code>
    /// </example>
    public QuiverSet<TEntity> Set<TEntity>() where TEntity : class, new()
    {
        if (_sets.TryGetValue(typeof(TEntity), out var set))
            return (QuiverSet<TEntity>)set;

        throw new InvalidOperationException($"No QuiverSet<{typeof(TEntity).Name}> found in context.");
    }

    #endregion

    #region 持久化

    /// <summary>
    /// 将所有向量集合的数据异步保存到磁盘（全量快照）。
    /// <para>
    /// WAL 启用时，此方法同时执行压缩：创建全量快照后清空 WAL。
    /// 等价于 <see cref="CompactAsync"/>。
    /// </para>
    /// </summary>
    /// <param name="path">
    /// 保存路径。为 <c>null</c> 时使用 <see cref="QuiverDbOptions.DatabasePath"/>。
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> 和 <c>DatabasePath</c> 均为空。</exception>
    public async Task SaveAsync(string? path = null)
    {
        var filePath = path ?? _options.DatabasePath;
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        // 收集所有 QuiverSet 中的实体数据
        var setsData = new Dictionary<string, (Type Type, List<object> Entities)>();

        foreach (var (type, set) in _sets)
        {
            // 反射调用 internal 方法 GetAll() 获取实体快照
            var getAll = set.GetType().GetMethod("GetAll", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var entities = ((IEnumerable<object>)getAll.Invoke(set, null)!).ToList();
            setsData[type.FullName!] = (type, entities);
        }

        // 原子写入：先写临时文件，成功后替换原文件
        var tempPath = filePath + ".tmp";
        await _storageProvider.SaveAsync(tempPath, setsData);
        File.Move(tempPath, filePath, overwrite: true);

        // 快照成功后清空 WAL 和变更日志
        if (_wal != null)
        {
            _wal.Truncate();

            // 清空各 QuiverSet 中未持久化的变更日志（已被快照覆盖）
            foreach (var (type, set) in _sets)
            {
                if (_drainMethodCache.TryGetValue(type, out var drainMethod))
                    drainMethod.Invoke(set, null);
            }
        }
    }

    /// <summary>
    /// 仅将未持久化的变更增量追加到 WAL 文件。复杂度 O(Δ)。
    /// <para>
    /// 此方法是 WAL 模式下的主要保存方法，比 <see cref="SaveAsync"/> 快数个数量级。
    /// WAL 未启用时，行为等同于 <see cref="SaveAsync"/>（全量保存）。
    /// </para>
    /// <para>
    /// 当 WAL 记录数超过 <see cref="QuiverDbOptions.WalCompactionThreshold"/> 时，
    /// 自动触发 <see cref="CompactAsync"/> 创建全量快照并清空 WAL。
    /// </para>
    /// </summary>
    public async Task SaveChangesAsync()
    {
        // WAL 未启用时回退到全量保存
        if (_wal == null)
        {
            await SaveAsync();
            return;
        }

        // 收集所有 QuiverSet 的变更
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
            // 批量追加到 WAL（单次 fsync）
            await Task.Run(() => _wal.Append(walEntries, _options.WalFlushToDisk));
        }

        // 自动压缩：WAL 记录数超过阈值时创建全量快照
        if (_wal.RecordCount >= _options.WalCompactionThreshold)
            await CompactAsync();
    }

    /// <summary>
    /// 执行压缩：创建全量快照并清空 WAL。等价于 <see cref="SaveAsync"/>。
    /// <para>
    /// 适用于以下场景：
    /// <list type="bullet">
    ///   <item>定时任务周期性压缩</item>
    ///   <item>WAL 文件过大时手动触发</item>
    ///   <item>应用关闭前确保快照最新</item>
    /// </list>
    /// </para>
    /// </summary>
    public Task CompactAsync() => SaveAsync();

    /// <summary>
    /// 从磁盘异步加载数据到所有向量集合中。
    /// <para>
    /// WAL 启用时，加载流程为两阶段：
    /// <list type="number">
    ///   <item>读取全量快照并恢复实体和索引</item>
    ///   <item>读取 WAL 文件并按顺序回放增量变更</item>
    /// </list>
    /// </para>
    /// <para>
    /// 文件不存在时静默返回（不抛异常），适合首次启动场景。
    /// </para>
    /// </summary>
    /// <param name="path">
    /// 加载路径。为 <c>null</c> 时使用 <see cref="QuiverDbOptions.DatabasePath"/>。
    /// </param>
    public async Task LoadAsync(string? path = null)
    {
        var filePath = path ?? _options.DatabasePath;

        // ── 阶段 1：加载全量快照 ──
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            // 构建迁移规则字典（类型全名 → 迁移规则），仅包含已注册迁移的类型
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

                // 应用值转换规则（如果存在）
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

        // ── 阶段 2：回放 WAL 增量变更 ──
        if (_wal != null)
            ReplayWal(_wal.FilePath);
    }

    /// <summary>
    /// 回放 WAL 文件中的所有有效记录，将增量变更应用到对应的向量集合。
    /// <para>
    /// 回放期间使用 <c>ReplayAdd/ReplayRemove/ReplayClear</c> 方法，
    /// 不触发变更日志记录，避免循环写入。
    /// </para>
    /// <para>
    /// 若存在 Schema 迁移规则，回放时会自动应用属性重命名（修正 JSON 属性名）
    /// 和值转换（对反序列化后的实体执行 <see cref="SchemaMigrationRule.ValueTransforms"/>）。
    /// </para>
    /// </summary>
    private void ReplayWal(string walFilePath)
    {
        var entries = WriteAheadLog.ReadAll(walFilePath);
        if (entries.Count == 0) return;

        foreach (var entry in entries)
        {
            // 查找目标类型和 QuiverSet 实例
            if (!_typeMap.TryGetValue(entry.TypeName, out var type) ||
                !_sets.TryGetValue(type, out var set))
                continue;

            // 获取迁移规则（可能为 null）
            _migrations.TryGetValue(type, out var rule);

            switch (entry.Operation)
            {
                case WalOperation.Add:
                    {
                        var json = entry.PayloadJson;

                        // 存在重命名规则时，修正 WAL JSON 中的旧属性名
                        if (rule != null && rule.PropertyRenames.Count > 0)
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

                        // 反序列化实体
                        var entity = JsonSerializer.Deserialize(json, type, WalJsonOptions);
                        if (entity == null) continue;

                        // 应用值转换规则
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
                        // 反序列化主键并调用 ReplayRemove
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

    #region Dispose

    /// <summary>
    /// 同步释放所有向量集合资源。<b>不会</b>自动保存数据。
    /// 如需保存请在释放前手动调用 <see cref="SaveChangesAsync"/>，或使用 <see cref="DisposeAsync"/>。
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _wal?.Dispose();

            foreach (var set in _sets.Values)
                if (set is IDisposable d) d.Dispose();
            _sets.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 异步释放资源。<b>会先自动保存数据</b>再释放所有向量集合。
    /// 推荐在 <c>await using</c> 语句中使用，确保数据不丢失。
    /// <para>
    /// WAL 启用时调用 <see cref="SaveChangesAsync"/>（增量保存），
    /// 未启用时调用 <see cref="SaveAsync"/>（全量保存）。
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// await using var db = new MyDb();
    /// // ... 操作数据 ...
    /// // 作用域结束时自动调用 DisposeAsync → 先保存再释放
    /// </code>
    /// </example>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            // 先保存数据到磁盘
            if (_wal != null)
                await SaveChangesAsync();
            else
                await SaveAsync();

            _wal?.Dispose();

            // 再释放所有 QuiverSet 实例
            foreach (var set in _sets.Values)
                if (set is IDisposable d) d.Dispose();
            _sets.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    #endregion
}