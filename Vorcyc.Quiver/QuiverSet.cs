using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Numerics.Tensors;
using System.Reflection;
using Vorcyc.Quiver.Indexing;

namespace Vorcyc.Quiver;

/// <summary>
/// 记录单个向量字段的元信息，包含维度、距离度量、索引配置、预归一化标志和可空标志。
/// <para>
/// 由 <see cref="QuiverSet{TEntity}"/> 构造时通过反射扫描 <see cref="QuiverVectorAttribute"/> 自动创建，
/// 构造后冻结为 <see cref="System.Collections.Frozen.FrozenDictionary{TKey, TValue}"/> 的值，生命周期内不可变。
/// </para>
/// </summary>
/// <param name="Dimensions">
/// 向量维度（固定值）。写入时实际数组长度必须等于此值，否则抛出 <see cref="ArgumentException"/>。
/// 来源于 <see cref="QuiverVectorAttribute.Dimensions"/>。
/// </param>
/// <param name="Metric">
/// 距离度量类型，决定相似度计算方式。
/// 来源于 <see cref="QuiverVectorAttribute.Metric"/>。
/// </param>
/// <param name="IndexConfig">
/// 索引配置。为 <c>null</c> 时使用 <see cref="Indexing.FlatIndex"/> 暴力搜索。
/// 来源于属性上的 <see cref="QuiverIndexAttribute"/>（可选标记）。
/// </param>
/// <param name="PreNormalize">
/// 是否在写入和查询时执行 L2 预归一化。
/// 当 <paramref name="Metric"/> 为 <see cref="DistanceMetric.Cosine"/> 时自动启用，
/// 使搜索时可用 Dot 替代 CosineSimilarity 以提升性能。
/// </param>
/// <param name="Optional">
/// 是否允许向量值为 <c>null</c>。来源于 <see cref="QuiverVectorAttribute.Optional"/>。
/// <para>
/// 为 <c>true</c> 时，向量为 <c>null</c> 的实体仍可写入但不加入该字段的索引，
/// 搜索该字段时不会返回这些实体。为 <c>false</c>（默认）时，向量为 <c>null</c> 将抛出 <see cref="ArgumentNullException"/>。
/// </para>
/// </param>
internal record QuiverFieldInfo(
    int Dimensions,
    DistanceMetric Metric,
    QuiverIndexAttribute? IndexConfig,
    bool PreNormalize,
    bool Optional);


/// <summary>
/// 向量集合，提供实体的 CRUD 操作和向量相似度搜索。
/// <para>
/// <b>线程安全</b>：内部使用 <see cref="ReaderWriterLockSlim"/> 实现读写分离锁，
/// 多个搜索操作可并行执行，写操作互斥。
/// </para>
/// <para>
/// <b>异步支持</b>：所有可能耗时的操作（搜索、批量写入）均提供 <c>Async</c> 后缀重载，
/// 通过 <see cref="Task.Run(Action)"/> 将 CPU 密集计算卸载到线程池，避免阻塞调用方线程（如 UI 线程）。
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
public partial class QuiverSet<TEntity> : IDisposable, IEnumerable<TEntity> where TEntity : class, new()
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
    private readonly FrozenDictionary<string, Func<TEntity, float[]?>> _vectorGetters;

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

        _getKey = CompileGetter<object?>(keyProp);

        // ── 发现并注册所有向量字段 ──
        var vectorProps = type.GetProperties()
            .Where(p => p.GetCustomAttribute<QuiverVectorAttribute>() != null);

        var vectorFields = new Dictionary<string, QuiverFieldInfo>();
        var vectorGetters = new Dictionary<string, Func<TEntity, float[]?>>();
        var indices = new Dictionary<string, IVectorIndex>();

        foreach (var prop in vectorProps)
        {
            if (prop.PropertyType != typeof(float[]))
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name} must be of type float[].");

            var vectorAttr = prop.GetCustomAttribute<QuiverVectorAttribute>()!;
            var indexAttr = prop.GetCustomAttribute<QuiverIndexAttribute>();
            var metric = vectorAttr.Metric;
            var preNormalize = metric == DistanceMetric.Cosine;

            vectorFields[prop.Name] = new QuiverFieldInfo(
                vectorAttr.Dimensions, metric, indexAttr, preNormalize, vectorAttr.Optional);

            vectorGetters[prop.Name] = CompileGetter<float[]?>(prop);

            SimilarityFunc simFunc = preNormalize
                ? TensorPrimitives.Dot
                : CreateSimilarityFunc(metric);

            indices[prop.Name] = CreateIndex(indexAttr, simFunc);
        }

        if (vectorFields.Count == 0)
            throw new InvalidOperationException($"Entity {type.Name} must have at least one [QuiverVector] property.");

        _vectorFields = vectorFields.ToFrozenDictionary();
        _vectorGetters = vectorGetters.ToFrozenDictionary();
        _indices = indices.ToFrozenDictionary();

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
    public IReadOnlyDictionary<string, int> VectorFields
        => _vectorFieldsCache ??= new ReadOnlyDictionary<string, int>(
            _vectorFields.ToDictionary(kv => kv.Key, kv => kv.Value.Dimensions));

    #region 枚举

    /// <summary>
    /// 返回所有实体的枚举器。支持 <c>foreach</c> 循环和 LINQ 查询。
    /// <para>
    /// <b>线程安全</b>：枚举前在读锁内拍摄实体快照（浅拷贝），
    /// 释放锁后再逐一 yield，避免持锁期间执行用户代码导致死锁。
    /// 枚举期间的写操作不影响已拍摄的快照。
    /// </para>
    /// </summary>
    /// <returns>实体快照的枚举器。</returns>
    /// <example>
    /// <code>
    /// // foreach 循环
    /// foreach (var doc in db.Documents)
    ///     Console.WriteLine(doc.Title);
    ///
    /// // LINQ 查询
    /// var tutorials = db.Documents
    ///     .Where(e => e.Category == "教程")
    ///     .OrderBy(e => e.Title)
    ///     .ToList();
    /// </code>
    /// </example>
    public IEnumerator<TEntity> GetEnumerator()
    {
        ThrowIfDisposed();

        TEntity[] snapshot;
        _lock.EnterReadLock();
        try { snapshot = [.. _entities.Values]; }
        finally { _lock.ExitReadLock(); }

        foreach (var entity in snapshot)
            yield return entity;
    }

    /// <summary>非泛型枚举器实现，转发到泛型版本。</summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    #region 内部工具方法

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
    private static Func<TEntity, TResult> CompileGetter<TResult>(PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(TEntity), "e");
        Expression body = Expression.Property(param, prop);

        if (prop.PropertyType != typeof(TResult))
            body = Expression.Convert(body, typeof(TResult));

        return Expression.Lambda<Func<TEntity, TResult>>(body, param).Compile();
    }

    /// <summary>
    /// L2 归一化并返回新数组。内部使用 <see cref="TensorPrimitives"/> 的 SIMD 加速实现。
    /// </summary>
    private static float[] NormalizeToArray(float[] source)
    {
        var result = new float[source.Length];
        NormalizeVector(source, result);
        return result;
    }

    /// <summary>
    /// L2 归一化：<c>destination[i] = source[i] / ‖source‖₂</c>。
    /// 使用 <see cref="TensorPrimitives.Norm"/> 计算 L2 范数（SIMD 加速），
    /// 再用 <see cref="TensorPrimitives.Divide{T}(ReadOnlySpan{T}, T, Span{T})"/> 向量化除法。
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
    /// </summary>
    private static SimilarityFunc CreateSimilarityFunc(DistanceMetric metric) => metric switch
    {
        DistanceMetric.DotProduct => TensorPrimitives.Dot,
        DistanceMetric.Euclidean => (a, b) => 1f / (1f + TensorPrimitives.Distance(a, b)),
        _ => TensorPrimitives.CosineSimilarity
    };

    /// <summary>
    /// 根据索引配置创建对应的向量索引实例。
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

    #region Dispose

    /// <summary>
    /// 释放所有资源：索引实例和读写锁。
    /// 使用 <see cref="Interlocked.Exchange"/> 保证并发调用时仅执行一次释放逻辑。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

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