using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Numerics.Tensors;
using System.Reflection;
using Vorcyc.Quiver.Indexing;

namespace Vorcyc.Quiver;

/// <summary>
/// 记录单个向量字段的元信息，包含维度、距离度量、索引配置和是否需要预归一化。
/// </summary>
/// <param name="Dimensions">向量维度。</param>
/// <param name="Metric">距离度量类型。</param>
/// <param name="IndexConfig">索引配置，为 <c>null</c> 时使用 Flat 暴力搜索。</param>
/// <param name="PreNormalize">是否在写入和查询时执行 L2 预归一化（Cosine 度量时自动启用）。</param>
internal record QuiverFieldInfo(
    int Dimensions,
    DistanceMetric Metric,
    QuiverIndexAttribute? IndexConfig,
    bool PreNormalize);

/// <summary>
/// 向量集合，提供实体的 CRUD 操作和向量相似度搜索。
/// <para>
/// <b>线程安全</b>：内部使用 <see cref="ReaderWriterLockSlim"/> 实现读写分离锁，
/// 多个搜索操作可并行执行，写操作互斥。
/// </para>
/// <para>
/// <b>异步支持</b>：所有可能耗时的操作（搜索、批量写入）均提供 <c>Async</c> 后缀重载，
/// 通过 <see cref="Task.Run"/> 将 CPU 密集计算卸载到线程池，避免阻塞调用方线程（如 UI 线程）。
/// </para>
/// </summary>
/// <typeparam name="TEntity">
/// 实体类型。须满足以下约束：
/// <list type="bullet">
///   <item>标记一个 <see cref="QuiverKeyAttribute"/> 属性作为主键</item>
///   <item>标记至少一个 <see cref="QuiverVectorAttribute"/> 属性作为向量字段（类型须为 <c>float[]</c>）</item>
/// </list>
/// </typeparam>
/// <example>
/// <code>
/// using var set = new QuiverSet&lt;Document&gt;();
/// set.Add(new Document { Id = "doc1", Embedding = new float[128] });
/// var results = set.Search(e => e.Embedding, queryVector, topK: 5);
/// </code>
/// </example>
public class QuiverSet<TEntity> : IDisposable where TEntity : class, new()
{
    // ──────────────────────────────────────────────────────────────
    // 存储层：内部 ID → 实体 的双向映射，内部 ID 由 _nextId 自增分配
    // ──────────────────────────────────────────────────────────────

    /// <summary>内部 ID → 实体 的映射，用于搜索结果的 ID 反查。</summary>
    private readonly Dictionary<int, TEntity> _entities = [];

    /// <summary>用户主键 → 内部 ID 的映射，支持 O(1) 的主键去重和查找。</summary>
    private readonly Dictionary<object, int> _keyToId = [];

    /// <summary>内部 ID 自增计数器。仅在写锁内递增，无需原子操作。</summary>
    private int _nextId;

    // ──────────────────────────────────────────────────────────────
    // 元数据层：构造后冻结的只读配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>编译后的主键属性访问器，替代反射调用 PropertyInfo.GetValue。</summary>
    private readonly Func<TEntity, object?> _getKey;

    /// <summary>向量字段名称 → 字段元信息（维度、度量、索引配置）。构造后冻结。</summary>
    private readonly FrozenDictionary<string, QuiverFieldInfo> _vectorFields;

    /// <summary>向量字段名称 → 编译后的向量属性访问器。构造后冻结。</summary>
    private readonly FrozenDictionary<string, Func<TEntity, float[]>> _vectorGetters;

    /// <summary>向量字段名称 → 对应的向量索引实例。构造后冻结。</summary>
    private readonly FrozenDictionary<string, IVectorIndex> _indices;

    /// <summary>
    /// 当实体仅有一个向量字段时缓存的默认字段信息，避免每次搜索调用 _vectorFields.First()。
    /// 多字段实体时为 <c>null</c>。
    /// </summary>
    private readonly (string Name, QuiverFieldInfo Field)? _defaultField;

    /// <summary>VectorFields 公共属性的惰性缓存，首次访问时构建。</summary>
    private ReadOnlyDictionary<string, int>? _vectorFieldsCache;

    // ──────────────────────────────────────────────────────────────
    // 并发控制
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 读写分离锁。读操作（Search/Find）获取共享读锁，写操作（Add/Remove/Clear）获取独占写锁。
    /// </summary>
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// 释放标志：0 = 未释放，1 = 已释放。
    /// 配合 <see cref="Interlocked.Exchange"/> 保证并发 Dispose 安全。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化向量集合。通过反射扫描 <typeparamref name="TEntity"/> 的属性，
    /// 自动发现主键和向量字段，编译属性访问器，并为每个向量字段创建对应的索引实例。
    /// </summary>
    /// <param name="defaultMetric">
    /// 默认距离度量。当前未使用（度量从 <see cref="QuiverVectorAttribute.Metric"/> 读取），
    /// 保留用于未来扩展。
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// 实体类型缺少 <see cref="QuiverKeyAttribute"/> 主键，或没有任何 <see cref="QuiverVectorAttribute"/> 向量字段。
    /// </exception>
    internal QuiverSet(DistanceMetric defaultMetric = DistanceMetric.Cosine)
    {
        var type = typeof(TEntity);

        // ── 发现主键属性 ──
        var keyProp = type.GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<QuiverKeyAttribute>() != null)
            ?? throw new InvalidOperationException($"Entity {type.Name} must have a [QuiverKey] property.");

        // 编译表达式树访问器：e => (object?)e.KeyProp，替代 PropertyInfo.GetValue 反射调用
        _getKey = CompileGetter<object?>(keyProp);

        // ── 发现并注册所有向量字段 ──
        var vectorProps = type.GetProperties()
            .Where(p => p.GetCustomAttribute<QuiverVectorAttribute>() != null);

        // 使用临时可变字典收集，构造完成后冻结为 FrozenDictionary
        var vectorFields = new Dictionary<string, QuiverFieldInfo>();
        var vectorGetters = new Dictionary<string, Func<TEntity, float[]>>();
        var indices = new Dictionary<string, IVectorIndex>();

        foreach (var prop in vectorProps)
        {
            // 向量属性必须是 float[] 类型
            if (prop.PropertyType != typeof(float[]))
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name} must be of type float[].");

            var vectorAttr = prop.GetCustomAttribute<QuiverVectorAttribute>()!;
            var indexAttr = prop.GetCustomAttribute<QuiverIndexAttribute>();

            // 直接使用 Attribute 上的 Metric，不做 default 判断
            // 因为 DistanceMetric.Cosine == 0 == default，无法区分"显式 Cosine"和"未指定"
            var metric = vectorAttr.Metric;

            // Cosine 度量启用预归一化优化：写入时归一化向量，搜索时用 Dot 替代 CosineSimilarity
            var preNormalize = metric == DistanceMetric.Cosine;

            vectorFields[prop.Name] = new QuiverFieldInfo(
                vectorAttr.Dimensions, metric, indexAttr, preNormalize);

            // 编译表达式树访问器：e => e.VectorProp
            vectorGetters[prop.Name] = CompileGetter<float[]>(prop);

            // 预归一化模式下使用 Dot（已归一化向量的 Dot = Cosine），否则按度量选择相似度函数
            SimilarityFunc simFunc = preNormalize
                ? TensorPrimitives.Dot
                : CreateSimilarityFunc(metric);

            // 根据 QuiverIndexAttribute 配置创建对应的索引实现
            indices[prop.Name] = CreateIndex(indexAttr, simFunc);
        }

        if (vectorFields.Count == 0)
            throw new InvalidOperationException($"Entity {type.Name} must have at least one [QuiverVector] property.");

        // 构建后冻结：FrozenDictionary 针对实际 key 集合优化哈希策略，
        // 对小 key 集合（通常 1~3 个向量字段）查找更快且零堆分配
        _vectorFields = vectorFields.ToFrozenDictionary();
        _vectorGetters = vectorGetters.ToFrozenDictionary();
        _indices = indices.ToFrozenDictionary();

        // 单字段时缓存默认字段，避免每次默认搜索调用 _vectorFields.First()
        if (_vectorFields.Count == 1)
        {
            var first = _vectorFields.First();
            _defaultField = (first.Key, first.Value);
        }
    }

    /// <summary>当前存储的实体数量。线程安全（读锁）。</summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            _lock.EnterReadLock();
            try { return _entities.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// 所有向量字段名称与维度的只读映射。
    /// 首次访问时惰性构建，后续调用返回缓存实例。
    /// </summary>
    /// <example>
    /// <code>
    /// // 输出：Embedding: 128
    /// foreach (var (name, dim) in set.VectorFields)
    ///     Console.WriteLine($"{name}: {dim}");
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, int> VectorFields
        => _vectorFieldsCache ??= new ReadOnlyDictionary<string, int>(
            _vectorFields.ToDictionary(kv => kv.Key, kv => kv.Value.Dimensions));

    #region CRUD 操作

    /// <summary>
    /// 添加单个实体。主键重复时抛出异常。
    /// </summary>
    /// <param name="entity">要添加的实体，主键属性值不能为 <c>null</c>。</param>
    /// <exception cref="InvalidOperationException">主键为 <c>null</c> 或已存在相同主键的实体。</exception>
    /// <exception cref="ArgumentException">实体的向量字段维度与定义不一致。</exception>
    public void Add(TEntity entity)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { AddCore(entity); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 批量添加实体。采用两阶段提交：先校验全部实体，再统一写入。
    /// <para>
    /// <b>原子语义</b>：任一实体校验失败时全部回滚，不会写入任何数据。
    /// </para>
    /// </summary>
    /// <param name="entities">要添加的实体集合。</param>
    /// <exception cref="InvalidOperationException">主键为 <c>null</c>，或批次内/已有数据存在重复主键。</exception>
    /// <exception cref="ArgumentException">某个实体的向量字段维度不匹配。</exception>
    public void AddRange(IEnumerable<TEntity> entities)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            var entityList = entities as IList<TEntity> ?? [.. entities];
            if (entityList.Count == 0) return;

            // ── 阶段 1：全部预校验（不修改任何状态，异常安全）──
            var batch = new (object Key, (string Name, float[] Vector)[] Vectors)[entityList.Count];
            var keysInBatch = new HashSet<object>(entityList.Count);

            for (var idx = 0; idx < entityList.Count; idx++)
            {
                var key = _getKey(entityList[idx])
                    ?? throw new InvalidOperationException("Key property value cannot be null.");

                // 检查与已有数据和批次内是否重复
                if (_keyToId.ContainsKey(key) || !keysInBatch.Add(key))
                    throw new InvalidOperationException(
                        $"Duplicate key '{key}'. An entity with the same [QuiverKey] already exists.");

                // 收集并校验向量数据（含维度检查、归一化/防御性复制）
                batch[idx] = (key, PrepareVectors(entityList[idx]));
            }

            // ── 阶段 2：全部提交（此后不会再抛异常）──
            for (var idx = 0; idx < entityList.Count; idx++)
            {
                var id = _nextId++;
                _entities[id] = entityList[idx];
                _keyToId[batch[idx].Key] = id;

                foreach (var (name, vector) in batch[idx].Vectors)
                    _indices[name].Add(id, vector);

                // 记录变更
                _changeLog.Add((1, batch[idx].Key, entityList[idx]));
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// <see cref="AddRange"/> 的异步版本。将 CPU 密集的校验和索引构建卸载到线程池，避免阻塞 UI 线程。
    /// </summary>
    /// <param name="entities">要添加的实体集合。</param>
    /// <param name="cancellationToken">取消令牌。取消时操作可能已部分完成，数据状态由内部事务保证一致。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => AddRange(entities), cancellationToken);
    }

    /// <summary>
    /// 插入或更新实体（Upsert 语义）。若主键已存在则先删除旧实体再新增，否则直接新增。
    /// 在单次写锁内完成，比外部 <c>Remove + Add</c> 更高效。
    /// </summary>
    /// <param name="entity">要插入或更新的实体。</param>
    /// <exception cref="InvalidOperationException">主键为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException">向量字段维度不匹配。</exception>
    public void Upsert(TEntity entity)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            var key = _getKey(entity)
                ?? throw new InvalidOperationException("Key property value cannot be null.");

            // 若主键已存在则先清除旧数据（RemoveCore 不存在时静默返回 false）
            RemoveCore(key);
            AddCore(entity);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 按实体删除。提取实体的主键属性值进行匹配，而非引用比较。
    /// </summary>
    /// <param name="entity">要删除的实体。</param>
    /// <returns>成功删除返回 <c>true</c>；主键为 <c>null</c> 或未找到返回 <c>false</c>。</returns>
    public bool Remove(TEntity entity)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            var key = _getKey(entity);
            return key != null && RemoveCore(key);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 按主键值直接删除，无需持有实体引用。
    /// </summary>
    /// <param name="key">要删除的实体主键值。</param>
    /// <returns>成功删除返回 <c>true</c>；未找到返回 <c>false</c>。</returns>
    public bool RemoveByKey(object key)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { return RemoveCore(key); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 按主键查找实体。通过双层字典（主键 → 内部 ID → 实体）实现 O(1) 复杂度。
    /// </summary>
    /// <param name="key">要查找的主键值。</param>
    /// <returns>找到的实体；未命中返回 <c>null</c>。</returns>
    public TEntity? Find(object key)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return _keyToId.TryGetValue(key, out var id)
                && _entities.TryGetValue(id, out var entity)
                ? entity
                : null;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 清空所有实体、主键映射和索引数据。内部 ID 计数器重置为 0。
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            _entities.Clear();
            _keyToId.Clear();
            _nextId = 0;
            foreach (var index in _indices.Values)
                index.Clear();

            _changeLog.Add((3, null, null)); // Op=3: Clear
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 添加实体的核心逻辑。采用两阶段提交保证异常安全：
    /// 阶段 1 校验并准备向量数据（不修改任何状态），阶段 2 原子写入。
    /// 调用方须持有写锁。
    /// </summary>
    /// <param name="logChanges">是否记录到变更日志。加载和 WAL 回放时传 <c>false</c>。</param>
    private void AddCore(TEntity entity, bool logChanges = true)
    {
        var key = _getKey(entity)
            ?? throw new InvalidOperationException("Key property value cannot be null.");

        if (_keyToId.ContainsKey(key))
            throw new InvalidOperationException(
                $"Duplicate key '{key}'. An entity with the same [QuiverKey] already exists.");

        // 阶段 1：收集 + 校验（不修改任何状态，异常安全）
        var prepared = PrepareVectors(entity);

        // 阶段 2：原子提交（此后不会再抛异常）
        var id = _nextId++;
        _entities[id] = entity;
        _keyToId[key] = id;

        foreach (var (name, indexVector) in prepared)
            _indices[name].Add(id, indexVector);

        // 记录变更（加载/回放时跳过）
        if (logChanges)
            _changeLog.Add((1, key, entity)); // Op=1: Add
    }

    /// <summary>
    /// 收集并校验实体的所有向量字段，返回准备好的索引数据。
    /// 此方法不修改任何状态，校验失败时可安全抛出异常。
    /// <list type="bullet">
    ///   <item>PreNormalize 字段：执行 L2 归一化，返回新数组</item>
    ///   <item>其他字段：防御性复制（Clone），防止外部修改数组导致索引损坏</item>
    /// </list>
    /// </summary>
    private (string Name, float[] IndexVector)[] PrepareVectors(TEntity entity)
    {
        var prepared = new (string Name, float[] IndexVector)[_vectorFields.Count];
        var i = 0;
        foreach (var (name, field) in _vectorFields)
        {
            var vector = _vectorGetters[name](entity);

            if (vector.Length != field.Dimensions)
                throw new ArgumentException(
                    $"Vector dimension mismatch on '{name}'. Expected {field.Dimensions}, got {vector.Length}");

            prepared[i++] = field.PreNormalize
                ? (name, NormalizeToArray(vector))
                : (name, (float[])vector.Clone());
        }
        return prepared;
    }

    /// <summary>
    /// 删除实体的核心逻辑。从实体存储、主键映射和所有索引中移除。
    /// 调用方须持有写锁。
    /// </summary>
    /// <param name="logChanges">是否记录到变更日志。WAL 回放时传 <c>false</c>。</param>
    /// <returns>成功删除返回 <c>true</c>；主键不存在返回 <c>false</c>。</returns>
    private bool RemoveCore(object key, bool logChanges = true)
    {
        if (!_keyToId.TryGetValue(key, out var id))
            return false;

        _entities.Remove(id);
        _keyToId.Remove(key);
        foreach (var index in _indices.Values)
            index.Remove(id);

        // 记录变更
        if (logChanges)
            _changeLog.Add((2, key, null)); // Op=2: Remove

        return true;
    }


    #endregion

    #region 向量检索（同步）

    /// <summary>
    /// 在指定向量字段上搜索 Top-K 最相似的实体。
    /// </summary>
    /// <param name="vectorSelector">
    /// 向量字段选择器表达式。须为简单属性访问，例如 <c>e =&gt; e.Embedding</c>。
    /// </param>
    /// <param name="queryVector">查询向量，维度须与字段定义一致。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的搜索结果列表。</returns>
    /// <exception cref="ArgumentException">选择器不是属性访问表达式，或属性未标记 [QuiverVector]。</exception>
    /// <exception cref="ArgumentOutOfRangeException">查询向量维度与字段定义不一致。</exception>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ArgumentOutOfRangeException.ThrowIfNotEqual(queryVector.Length, field.Dimensions);
            return MapResults(SearchIndex(name, field, queryVector, topK));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 在指定向量字段上搜索 Top-K 最相似的实体，并按表达式过滤。
    /// <para>
    /// <b>注意</b>：每次调用会编译表达式树（开销约 ~50μs）。
    /// 高频调用场景请使用 <see cref="Func{T, TResult}"/> 重载，外部缓存编译后的委托。
    /// </para>
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <param name="filter">实体过滤表达式。</param>
    /// <returns>满足过滤条件的搜索结果，按相似度降序排列。</returns>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        Expression<Func<TEntity, bool>> filter)
    {
        return Search(vectorSelector, queryVector, topK, filter.Compile());
    }

    /// <summary>
    /// 在指定向量字段上搜索 Top-K 最相似的实体，并按委托过滤。
    /// <para>
    /// 内部采用过采样策略：先从索引中检索 <c>topK × overFetchMultiplier</c> 个候选，
    /// 再逐一过滤直到收集够 topK 个结果。高过滤率场景可增大 <paramref name="overFetchMultiplier"/>。
    /// </para>
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <param name="filter">实体过滤谓词。返回 <c>true</c> 的实体保留。</param>
    /// <param name="overFetchMultiplier">
    /// 候选集倍率。默认值 4 表示从索引中检索 4 倍于 topK 的候选。
    /// 过滤率高于 75% 时建议增大此值。
    /// </param>
    /// <returns>满足过滤条件的搜索结果，按相似度降序排列。</returns>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        Func<TEntity, bool> filter,
        int overFetchMultiplier = 4)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ArgumentOutOfRangeException.ThrowIfNotEqual(queryVector.Length, field.Dimensions);

            // 过采样：多检索一些候选，再过滤，提高命中率
            var overFetch = Math.Min(topK * overFetchMultiplier, _entities.Count);

            var results = new List<QuiverSearchResult<TEntity>>(topK);
            foreach (var (id, similarity) in SearchIndex(name, field, queryVector, overFetch))
            {
                if (!_entities.TryGetValue(id, out var entity))
                    continue;
                if (!filter(entity))
                    continue;

                results.Add(new QuiverSearchResult<TEntity>(entity, similarity));
                if (results.Count >= topK)
                    break; // 已收集够 topK 个，提前终止
            }
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 搜索所有相似度不低于阈值的实体。结果数量不固定，取决于数据分布。
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="threshold">相似度下限（含），低于此值的结果被过滤。</param>
    /// <returns>满足阈值条件的搜索结果列表。</returns>
    public List<QuiverSearchResult<TEntity>> SearchByThreshold(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        float threshold)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ArgumentOutOfRangeException.ThrowIfNotEqual(queryVector.Length, field.Dimensions);
            return MapResults(SearchIndexByThreshold(name, field, queryVector, threshold));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 搜索最相似的单个实体。等价于 <c>Search(selector, query, topK: 1)</c> 但避免中间 List 分配。
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <returns>最相似的实体及其相似度分数；集合为空时返回 <c>null</c>。</returns>
    public QuiverSearchResult<TEntity>? SearchTop1(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ArgumentOutOfRangeException.ThrowIfNotEqual(queryVector.Length, field.Dimensions);
            return MapTop1(SearchIndex(name, field, queryVector, 1));
        }
        finally { _lock.ExitReadLock(); }
    }

    #endregion

    #region 向量检索（异步）

    // ──────────────────────────────────────────────────────────────
    // 异步重载通过 Task.Run 将 CPU 密集的搜索计算卸载到线程池，
    // 释放调用方线程（如 UI 线程、ASP.NET 请求线程），避免界面冻结。
    // 内部复用同步方法，不重复实现搜索逻辑。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Search(Expression{Func{TEntity, float[]}}, float[], int)"/> 的异步版本。
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按相似度降序排列的搜索结果。</returns>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(vectorSelector, queryVector, topK), cancellationToken);
    }

    /// <summary>
    /// <see cref="Search(Expression{Func{TEntity, float[]}}, float[], int, Func{TEntity, bool}, int)"/> 的异步版本。
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <param name="filter">实体过滤谓词。</param>
    /// <param name="overFetchMultiplier">候选集倍率，默认 4。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>满足过滤条件的搜索结果。</returns>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        Func<TEntity, bool> filter,
        int overFetchMultiplier = 4,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(
            () => Search(vectorSelector, queryVector, topK, filter, overFetchMultiplier),
            cancellationToken);
    }

    /// <summary>
    /// <see cref="SearchByThreshold"/> 的异步版本。
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="threshold">相似度下限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>满足阈值条件的搜索结果。</returns>
    public Task<List<QuiverSearchResult<TEntity>>> SearchByThresholdAsync(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        float threshold,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(
            () => SearchByThreshold(vectorSelector, queryVector, threshold),
            cancellationToken);
    }

    /// <summary>
    /// <see cref="SearchTop1(Expression{Func{TEntity, float[]}}, float[])"/> 的异步版本。
    /// </summary>
    /// <param name="vectorSelector">向量字段选择器。</param>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最相似的实体及其相似度分数。</returns>
    public Task<QuiverSearchResult<TEntity>?> SearchTop1Async(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => SearchTop1(vectorSelector, queryVector), cancellationToken);
    }

    #endregion

    #region 默认字段便捷方法（同步 + 异步）

    // ──────────────────────────────────────────────────────────────
    // 当实体仅有一个 [QuiverVector] 字段时，可省略 vectorSelector 参数，
    // 自动使用缓存的 _defaultField。多字段实体调用时抛出明确的异常提示。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在默认（唯一）向量字段上搜索 Top-K。仅当实体只有一个 [QuiverVector] 字段时可用。
    /// </summary>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <returns>按相似度降序排列的搜索结果。</returns>
    /// <exception cref="InvalidOperationException">实体有多个向量字段，须使用带 vectorSelector 的重载。</exception>
    public List<QuiverSearchResult<TEntity>> Search(float[] queryVector, int topK)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = GetDefaultField();
            ArgumentOutOfRangeException.ThrowIfNotEqual(queryVector.Length, field.Dimensions);
            return MapResults(SearchIndex(name, field, queryVector, topK));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 在默认向量字段上搜索最相似的单个实体。
    /// </summary>
    /// <param name="queryVector">查询向量。</param>
    /// <returns>最相似的实体及其相似度分数；集合为空时返回 <c>null</c>。</returns>
    public QuiverSearchResult<TEntity>? SearchTop1(float[] queryVector)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = GetDefaultField();
            ArgumentOutOfRangeException.ThrowIfNotEqual(queryVector.Length, field.Dimensions);
            return MapTop1(SearchIndex(name, field, queryVector, 1));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary><see cref="Search(float[], int)"/> 的异步版本。</summary>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="topK">返回结果数量上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按相似度降序排列的搜索结果。</returns>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        float[] queryVector, int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(queryVector, topK), cancellationToken);
    }

    /// <summary><see cref="SearchTop1(float[])"/> 的异步版本。</summary>
    /// <param name="queryVector">查询向量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最相似的实体及其相似度分数。</returns>
    public Task<QuiverSearchResult<TEntity>?> SearchTop1Async(
        float[] queryVector,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => SearchTop1(queryVector), cancellationToken);
    }

    #endregion

    #region 核心搜索 — 预归一化

    /// <summary>
    /// 按需归一化查询向量。Cosine 度量（PreNormalize=true）时返回归一化副本，
    /// 其他度量直接返回原数组（零拷贝）。
    /// </summary>
    private float[] NormalizeIfNeeded(QuiverFieldInfo field, float[] queryVector)
        => field.PreNormalize ? NormalizeToArray(queryVector) : queryVector;

    /// <summary>调用索引的 Top-K 搜索，自动处理查询向量归一化。</summary>
    private List<(int Id, float Similarity)> SearchIndex(
        string name, QuiverFieldInfo field, float[] queryVector, int topK)
        => _indices[name].Search(NormalizeIfNeeded(field, queryVector), topK);

    /// <summary>调用索引的阈值搜索，自动处理查询向量归一化。</summary>
    private List<(int Id, float Similarity)> SearchIndexByThreshold(
        string name, QuiverFieldInfo field, float[] queryVector, float threshold)
        => _indices[name].SearchByThreshold(NormalizeIfNeeded(field, queryVector), threshold);

    /// <summary>
    /// 将索引返回的 (内部ID, 相似度) 列表映射为用户侧的搜索结果列表。
    /// 跳过已被删除但索引中可能残留的无效 ID。
    /// </summary>
    private List<QuiverSearchResult<TEntity>> MapResults(List<(int Id, float Similarity)> indexResults)
    {
        var results = new List<QuiverSearchResult<TEntity>>(indexResults.Count);
        foreach (var (id, similarity) in indexResults)
        {
            if (_entities.TryGetValue(id, out var entity))
                results.Add(new QuiverSearchResult<TEntity>(entity, similarity));
        }
        return results;
    }

    /// <summary>
    /// 仅映射第一个有效结果，避免 SearchTop1 路径上的中间 List 分配。
    /// </summary>
    private QuiverSearchResult<TEntity>? MapTop1(List<(int Id, float Similarity)> indexResults)
    {
        foreach (var (id, similarity) in indexResults)
        {
            if (_entities.TryGetValue(id, out var entity))
                return new QuiverSearchResult<TEntity>(entity, similarity);
        }
        return null;
    }

    #endregion

    #region 内部方法

    /// <summary>
    /// 获取默认（唯一）向量字段。多字段实体时抛出异常引导用户使用 vectorSelector 重载。
    /// </summary>
    private (string Name, QuiverFieldInfo Field) GetDefaultField()
    {
        return _defaultField
            ?? throw new InvalidOperationException(
                $"Entity has {_vectorFields.Count} vector fields. " +
                $"Use the overload with a vectorSelector expression.");
    }

    /// <summary>
    /// 从表达式树中解析向量字段名称，查找对应的字段元信息。
    /// 仅支持简单属性访问表达式（如 <c>e =&gt; e.Embedding</c>），不支持方法调用或复杂表达式。
    /// </summary>
    private (string Name, QuiverFieldInfo Field) ResolveField(
        Expression<Func<TEntity, float[]>> vectorSelector)
    {
        var memberExpr = vectorSelector.Body as MemberExpression
            ?? throw new ArgumentException("vectorSelector must be a simple property access expression.");

        var propName = memberExpr.Member.Name;
        return _vectorFields.TryGetValue(propName, out var field)
            ? (propName, field)
            : throw new ArgumentException($"Property '{propName}' is not marked with [QuiverVector] attribute.");
    }

    /// <summary>
    /// 检查是否已释放。使用 <see cref="Volatile.Read"/> 保证跨线程可见性，
    /// 配合 <see cref="Interlocked.Exchange"/> 在 Dispose 中设置标志。
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>
    /// 通过表达式树编译属性访问器委托，替代运行时反射调用 <see cref="PropertyInfo.GetValue"/>。
    /// 编译后的委托性能与直接属性访问相当（纳秒级），而反射约慢 100 倍。
    /// </summary>
    /// <typeparam name="TResult">属性值类型。值类型属性会自动插入装箱转换节点。</typeparam>
    /// <param name="prop">要编译的属性信息。</param>
    /// <returns>编译后的属性访问器委托。</returns>
    private static Func<TEntity, TResult> CompileGetter<TResult>(PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(TEntity), "e");
        Expression body = Expression.Property(param, prop);

        // 属性类型与目标类型不同时插入转换（如 int → object 的装箱）
        if (prop.PropertyType != typeof(TResult))
            body = Expression.Convert(body, typeof(TResult));

        return Expression.Lambda<Func<TEntity, TResult>>(body, param).Compile();
    }

    /// <summary>
    /// L2 归一化并返回新数组。内部使用 <see cref="TensorPrimitives"/> 的 SIMD 加速实现。
    /// </summary>
    /// <param name="source">源向量。不会被修改。</param>
    /// <returns>归一化后的新数组。零向量返回全零数组。</returns>
    private static float[] NormalizeToArray(float[] source)
    {
        var result = new float[source.Length];
        NormalizeVector(source, result);
        return result;
    }

    /// <summary>
    /// L2 归一化：<c>destination[i] = source[i] / ‖source‖₂</c>。
    /// 使用 <see cref="TensorPrimitives.Norm"/> 计算 L2 范数（SIMD 加速），
    /// 再用 <see cref="TensorPrimitives.Divide"/> 向量化除法。
    /// 零向量（范数为 0）时清零目标数组，避免 NaN。
    /// </summary>
    private static void NormalizeVector(ReadOnlySpan<float> source, Span<float> destination)
    {
        var norm = TensorPrimitives.Norm(source);
        if (norm > 0f)
            TensorPrimitives.Divide(source, norm, destination);
        else
            destination.Clear();
    }

    /// <summary>
    /// 根据距离度量类型创建相似度计算委托。
    /// <list type="bullet">
    ///   <item><see cref="DistanceMetric.DotProduct"/>：直接点积</item>
    ///   <item><see cref="DistanceMetric.Euclidean"/>：<c>1 / (1 + 欧几里得距离)</c>，映射到 (0, 1] 区间</item>
    ///   <item>默认（含 Cosine）：余弦相似度（此分支仅在非预归一化模式下命中）</item>
    /// </list>
    /// </summary>
    private static SimilarityFunc CreateSimilarityFunc(DistanceMetric metric) => metric switch
    {
        DistanceMetric.DotProduct => TensorPrimitives.Dot,
        DistanceMetric.Euclidean => (a, b) => 1f / (1f + TensorPrimitives.Distance(a, b)),
        _ => TensorPrimitives.CosineSimilarity
    };

    /// <summary>
    /// 根据索引配置创建对应的向量索引实例。
    /// <list type="bullet">
    ///   <item><see cref="VectorIndexType.Flat"/>（或无配置）：暴力搜索，精确结果，O(n)</item>
    ///   <item><see cref="VectorIndexType.HNSW"/>：分层可导航小世界图，近似搜索，O(log n)</item>
    ///   <item><see cref="VectorIndexType.IVF"/>：倒排文件索引，近似搜索，适合大数据量</item>
    ///   <item><see cref="VectorIndexType.KDTree"/>：KD 树，适合低维（&lt;20）精确搜索</item>
    /// </list>
    /// </summary>
    private static IVectorIndex CreateIndex(QuiverIndexAttribute? config, SimilarityFunc simFunc)
    {
        if (config == null || config.IndexType == VectorIndexType.Flat)
            return new FlatIndex(simFunc);

        return config.IndexType switch
        {
            VectorIndexType.HNSW => new HnswIndex(simFunc, config.M, config.EfConstruction, config.EfSearch),
            VectorIndexType.IVF => new IvfIndex(simFunc, config.NumClusters, config.NumProbes),
            VectorIndexType.KDTree => new KDTreeIndex(simFunc),
            _ => new FlatIndex(simFunc)
        };
    }

    #endregion

    #region 持久化支持

    // ── 变更追踪（WAL 增量持久化支持）──

    /// <summary>
    /// 变更日志缓冲区。记录自上次 <see cref="DrainChanges"/> 以来的所有写操作。
    /// <para>
    /// 元组含义：
    /// <list type="bullet">
    ///   <item><c>Op</c>：操作类型（1=Add, 2=Remove, 3=Clear）</item>
    ///   <item><c>Key</c>：实体主键（Clear 时为 <c>null</c>）</item>
    ///   <item><c>Entity</c>：实体实例（仅 Add 时非空）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 仅在写锁内访问，无需额外同步。加载（<see cref="LoadEntities"/>）和回放
    /// （<see cref="ReplayAdd"/>/<see cref="ReplayRemove"/>/<see cref="ReplayClear"/>）
    /// 期间不记录变更，避免循环写入。
    /// </para>
    /// </summary>
    private readonly List<(byte Op, object? Key, object? Entity)> _changeLog = [];

    /// <summary>是否有未持久化的变更。读锁保护。</summary>
    internal bool HasPendingChanges
    {
        get
        {
            ThrowIfDisposed();
            _lock.EnterReadLock();
            try { return _changeLog.Count > 0; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// 获取并清空变更日志（快照 + 清除语义）。
    /// <para>
    /// 由 <see cref="QuiverDbContext.SaveChangesAsync"/> 调用，将变更转为 WAL 记录后持久化。
    /// </para>
    /// </summary>
    /// <returns>自上次调用以来的所有变更记录。无变更时返回空列表。</returns>
    internal List<(byte Op, object? Key, object? Entity)> DrainChanges()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            if (_changeLog.Count == 0)
                return [];
            var snapshot = new List<(byte, object?, object?)>(_changeLog);
            _changeLog.Clear();
            return snapshot;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── WAL 回放方法 ──
    // 回放期间不记录变更（logChanges: false），避免循环写入。

    /// <summary>
    /// 回放 WAL 的 Add 操作。不触发变更日志记录。
    /// <para>主键冲突时静默跳过（WAL 可能包含与快照重复的记录）。</para>
    /// </summary>
    internal void ReplayAdd(TEntity entity)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            var key = _getKey(entity);
            // 主键已存在时跳过（快照已包含此实体）
            if (key != null && _keyToId.ContainsKey(key))
                return;
            AddCore(entity, logChanges: false);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>回放 WAL 的 Remove 操作。不触发变更日志记录。</summary>
    internal void ReplayRemove(object key)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { RemoveCore(key, logChanges: false); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>回放 WAL 的 Clear 操作。不触发变更日志记录。</summary>
    internal void ReplayClear()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            _entities.Clear();
            _keyToId.Clear();
            _nextId = 0;
            foreach (var index in _indices.Values)
                index.Clear();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 获取所有实体的快照副本，供 <see cref="QuiverDbContext.SaveAsync"/> 持久化使用。
    /// 返回的是值的浅拷贝列表，读锁释放后外部修改不影响内部数据。
    /// </summary>
    internal IEnumerable<TEntity> GetAll()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try { return [.. _entities.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 从持久化数据恢复实体。逐个调用 <see cref="AddCore"/> 重建索引。
    /// 供 <see cref="QuiverDbContext.LoadAsync"/> 使用。不记录变更日志。
    /// </summary>
    /// <param name="entities">从存储加载的实体序列。</param>
    internal void LoadEntities(IEnumerable<TEntity> entities)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            foreach (var entity in entities)
                AddCore(entity, logChanges: false);
        }
        finally { _lock.ExitWriteLock(); }
    }

    #endregion

    #region Dispose

    /// <summary>
    /// 释放所有资源：索引实例和读写锁。
    /// 使用 <see cref="Interlocked.Exchange"/> 保证并发调用时仅执行一次释放逻辑。
    /// </summary>
    public void Dispose()
    {
        // CAS 操作：仅第一个调用方执行释放，后续调用立即返回
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 释放所有实现了 IDisposable 的索引实例
        foreach (var index in _indices.Values)
        {
            if (index is IDisposable disposable)
                disposable.Dispose();
        }

        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>
/// 向量搜索结果，封装匹配的实体及其相似度分数。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
/// <param name="Entity">匹配的实体实例。</param>
/// <param name="Similarity">
/// 相似度分数。值越大越相似。
/// 具体范围取决于距离度量：Cosine/DotProduct 为 [-1, 1]，Euclidean 为 (0, 1]。
/// </param>
public record QuiverSearchResult<TEntity>(TEntity Entity, float Similarity);