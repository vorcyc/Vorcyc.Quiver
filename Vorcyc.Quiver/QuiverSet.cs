using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Numerics.Tensors;
using System.Reflection;
using Vorcyc.Quiver.Indexing;
using Vorcyc.Quiver.Paging;
using Vorcyc.Quiver.Similarity;

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
/// 索引配置。为 <c>null</c> 时使用 <see cref="Indexing.FlatIndex{TSim}"/> 暴力搜索。
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
/// The entity type. Must satisfy the following constraints:
/// <list type="bullet">
///   <item>Has exactly one property marked with <see cref="QuiverKeyAttribute"/> as the primary key.</item>
///   <item>Has at least one <c>float[]</c> property marked with <see cref="QuiverVectorAttribute"/> as a vector field.</item>
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
    // Storage layer: bidirectional mapping of internal ID → entity; internal IDs are auto-incremented by _nextId
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Entity cache. In <see cref="EntityCacheMode.FullMemory"/> mode this is a pass-through dictionary;
    /// in <see cref="EntityCacheMode.LazyPaging"/> mode it is an LRU paged cache.
    /// The external interface is identical to <c>Dictionary&lt;int, TEntity&gt;</c> in both modes.
    /// </summary>
    private readonly EntityPageCache<TEntity> _entities;

    /// <summary>User primary key → internal ID mapping; supports O(1) deduplication and lookup.</summary>
    private readonly Dictionary<object, int> _keyToId = [];

    /// <summary>Auto-incrementing internal ID counter. Incremented only under the write lock; no atomic operation needed.</summary>
    private int _nextId;

    // ──────────────────────────────────────────────────────────────
    // Metadata layer: read-only configuration frozen after construction
    // ──────────────────────────────────────────────────────────────

    /// <summary>Compiled primary key property accessor, replacing runtime reflection via PropertyInfo.GetValue.</summary>
    private readonly Func<TEntity, object?> _getKey;

    /// <summary>Vector field name → field metadata (dimensions, metric, index config). Frozen after construction.</summary>
    private readonly FrozenDictionary<string, QuiverFieldInfo> _vectorFields;

    /// <summary>Vector field name → compiled vector property accessor. Frozen after construction.</summary>
    private readonly FrozenDictionary<string, Func<TEntity, float[]?>> _vectorGetters;

    /// <summary>Vector field name → corresponding vector index instance. Frozen after construction.</summary>
    private readonly FrozenDictionary<string, IVectorIndex> _indices;

    /// <summary>Vector field name → vector data store instance. Frozen after construction.</summary>
    private readonly FrozenDictionary<string, IVectorStore> _vectorStores;

    /// <summary>
    /// Cached default field info when the entity has exactly one vector field, avoiding a call to _vectorFields.First() on every search.
    /// <c>null</c> when the entity has multiple vector fields.
    /// </summary>
    private readonly (string Name, QuiverFieldInfo Field)? _defaultField;

    /// <summary>Lazy-initialized cache for the public VectorFields property; built on first access.</summary>
    private ReadOnlyDictionary<string, int>? _vectorFieldsCache;

    // ──────────────────────────────────────────────────────────────
    // Concurrency control
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reader-writer lock. Read operations (Search/Find) acquire a shared read lock; write operations (Add/Remove/Clear) acquire an exclusive write lock.
    /// </summary>
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Dispose flag: 0 = not disposed, 1 = disposed.
    /// Used with <see cref="Interlocked.Exchange"/> to ensure concurrent Dispose safety.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Initializes the vector collection. Scans properties of <typeparamref name="TEntity"/> via reflection
    /// to auto-discover the primary key and vector fields, compiles property accessors, and creates
    /// the corresponding index and vector store instances for each vector field.
    /// </summary>
    /// <param name="databasePath">
    /// Database path. Used to derive the page file directory in <see cref="EntityCacheMode.LazyPaging"/> mode.
    /// </param>
    /// <param name="entityCache">In-memory cache strategy for entity objects.</param>
    /// <param name="maxCachedPages">Maximum number of pages kept in memory in lazy-loading mode.</param>
    /// <param name="pageSize">Number of entities per page in lazy-loading mode.</param>
    /// <exception cref="InvalidOperationException">
    /// The entity type is missing a <see cref="QuiverKeyAttribute"/> primary key, or has no <see cref="QuiverVectorAttribute"/> vector fields.
    /// </exception>
    internal QuiverSet(
        string? databasePath = null,
        EntityCacheMode entityCache = EntityCacheMode.FullMemory,
        int maxCachedPages = 16,
        int pageSize = 512)
    {
        var type = typeof(TEntity);

        // ── Discover primary key property ──
        var keyProp = type.GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<QuiverKeyAttribute>() != null)
            ?? throw new InvalidOperationException($"Entity {type.Name} must have a [QuiverKey] property.");

        _getKey = CompileGetter<object?>(keyProp);

        // ── Discover and register all vector fields ──
        var vectorProps = type.GetProperties()
            .Where(p => p.GetCustomAttribute<QuiverVectorAttribute>() != null);

        var vectorFields = new Dictionary<string, QuiverFieldInfo>();
        var vectorGetters = new Dictionary<string, Func<TEntity, float[]?>>();
        var vectorStores = new Dictionary<string, IVectorStore>();
        var indices = new Dictionary<string, IVectorIndex>();

        foreach (var prop in vectorProps)
        {
            if (prop.PropertyType != typeof(float[]))
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name} must be of type float[].");

            var vectorAttr = prop.GetCustomAttribute<QuiverVectorAttribute>()!;
            var indexAttr = prop.GetCustomAttribute<QuiverIndexAttribute>();
            var metric = vectorAttr.Metric;
            var preNormalize = vectorAttr.CustomSimilarity is null && metric == DistanceMetric.Cosine;

            vectorFields[prop.Name] = new QuiverFieldInfo(
                vectorAttr.Dimensions, metric, indexAttr, preNormalize, vectorAttr.Optional);

            vectorGetters[prop.Name] = CompileGetter<float[]?>(prop);

            // ── Create the corresponding IVectorStore for each vector field ──
            var store = new HeapVectorStore();
            vectorStores[prop.Name] = store;

            // ── Create the corresponding vector index instance (generic helper eliminates the metric×index Cartesian product) ──
            if (vectorAttr.CustomSimilarity is { } customSimType)
            {
                // Custom metric: convert runtime Type → generic index instance (reflection executed only once at construction)
                indices[prop.Name] = CreateIndexReflection(indexAttr, customSimType, store);
            }
            else
            {
                indices[prop.Name] = (preNormalize, metric) switch
                {
                    (true, _) => CreateIndex<DotProductSimilarity>(indexAttr, store),
                    (_, DistanceMetric.DotProduct) => CreateIndex<DotProductSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Euclidean) => CreateIndex<EuclideanSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Manhattan) => CreateIndex<ManhattanSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Chebyshev) => CreateIndex<ChebyshevSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Pearson) => CreateIndex<PearsonCorrelationSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Hamming) => CreateIndex<HammingSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Jaccard) => CreateIndex<JaccardSimilarity>(indexAttr, store),
                    (_, DistanceMetric.Canberra) => CreateIndex<CanberraSimilarity>(indexAttr, store),
                    _ => CreateIndex<CosineSimilarity>(indexAttr, store)
                };
            }
        }

        if (vectorFields.Count == 0)
            throw new InvalidOperationException($"Entity {type.Name} must have at least one [QuiverVector] property.");

        _vectorFields = vectorFields.ToFrozenDictionary();
        _vectorGetters = vectorGetters.ToFrozenDictionary();
        _vectorStores = vectorStores.ToFrozenDictionary();
        _indices = indices.ToFrozenDictionary();

        if (_vectorFields.Count == 1)
        {
            var first = _vectorFields.First();
            _defaultField = (first.Key, first.Value);
        }

        // ── Initialize entity cache (FullMemory or LazyPaging) ──
        if (entityCache == EntityCacheMode.LazyPaging && !string.IsNullOrEmpty(databasePath))
        {
            var pageDir = Path.Combine(databasePath + ".pages", typeof(TEntity).Name);
            _entities = new EntityPageCache<TEntity>(pageDir, maxCachedPages, pageSize);
        }
        else
        {
            _entities = new EntityPageCache<TEntity>();
        }
    }

    /// <summary>Current number of stored entities. Thread-safe (read lock).</summary>
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
    /// Whether the collection is running in lazy-paging cache mode (<see cref="EntityCacheMode.LazyPaging"/>).
    /// </summary>
    public bool IsLazyLoading => _entities.IsLazy;

    /// <summary>
    /// Read-only mapping of all vector field names to their dimensions.
    /// Built lazily on first access; subsequent calls return the cached instance.
    /// </summary>
    public IReadOnlyDictionary<string, int> VectorFields
        => _vectorFieldsCache ??= new ReadOnlyDictionary<string, int>(
            _vectorFields.ToDictionary(kv => kv.Key, kv => kv.Value.Dimensions));

    #region Enumeration

    /// <summary>
    /// Returns an enumerator over all entities. Supports <c>foreach</c> loops and LINQ queries.
    /// <para>
    /// <b>Thread-safe</b>: A shallow snapshot of entities is taken inside a read lock before enumeration;
    /// the lock is released before yielding, avoiding deadlocks caused by user code running while the lock is held.
    /// Write operations during enumeration do not affect the already-captured snapshot.
    /// </para>
    /// </summary>
    /// <returns>An enumerator over the entity snapshot.</returns>
    /// <example>
    /// <code>
    /// // foreach loop
    /// foreach (var doc in db.Documents)
    ///     Console.WriteLine(doc.Title);
    ///
    /// // LINQ query
    /// var tutorials = db.Documents
    ///     .Where(e => e.Category == "Tutorials")
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

    /// <summary>Non-generic enumerator implementation; forwards to the generic version.</summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    #region Memory compaction

    /// <summary>
    /// Flushes all dirty pages to disk and evicts all loaded pages from memory, minimizing the memory footprint.
    /// Pages are reloaded from disk transparently on next access.
    /// <para>
    /// No-op in <see cref="EntityCacheMode.FullMemory"/> mode.
    /// Vector index structures (HNSW/IVF/KDTree etc.) are not affected and always remain in memory.
    /// </para>
    /// </summary>
    public void CompactMemory()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { _entities.CompactMemory(); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Asynchronously flushes all dirty pages to disk and evicts all loaded pages from memory.
    /// Offloads the work to a thread-pool thread to avoid blocking the caller (e.g. UI thread).
    /// <para>
    /// No-op in <see cref="EntityCacheMode.FullMemory"/> mode.
    /// Vector index structures are not affected.
    /// </para>
    /// </summary>
    public Task CompactMemoryAsync()
    {
        ThrowIfDisposed();
        return Task.Run(CompactMemory);
    }

    #endregion

    #region Internal utility methods

    /// <summary>
    /// Gets the default (sole) vector field. Throws when the entity has multiple fields, directing the user to the vectorSelector overload.
    /// </summary>
    private (string Name, QuiverFieldInfo Field) GetDefaultField()
    {
        return _defaultField
            ?? throw new InvalidOperationException(
                $"Entity has {_vectorFields.Count} vector fields. " +
                $"Use the overload with a vectorSelector expression.");
    }

    /// <summary>
    /// Parses a vector field name from an expression tree and retrieves the corresponding field metadata.
    /// Only simple property-access expressions (e.g. <c>e =&gt; e.Embedding</c>) are supported;
    /// method calls and complex expressions are not.
    /// </summary>
    /// <returns>A tuple of the field name and its <see cref="QuiverFieldInfo"/>.</returns>
    /// <exception cref="ArgumentException">The expression is not a property access, or the property is not marked with <see cref="QuiverVectorAttribute"/>.</exception>
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
    /// Checks whether the collection has been disposed and throws <see cref="ObjectDisposedException"/> if so.
    /// Uses <see cref="Volatile.Read"/> to ensure cross-thread visibility,
    /// paired with <see cref="Interlocked.Exchange"/> in <see cref="Dispose"/> to set the flag.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>
    /// Compiles a property accessor delegate from an expression tree, replacing runtime reflection via <see cref="PropertyInfo.GetValue"/>.
    /// The compiled delegate performs at the same speed as a direct property access (nanoseconds), roughly 100× faster than reflection.
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
    /// Performs L2 normalization on <paramref name="source"/> and returns a new array; the original array is unchanged.
    /// Delegates internally to <see cref="NormalizeVector"/>, using <see cref="TensorPrimitives"/> SIMD acceleration.
    /// </summary>
    /// <param name="source">The raw vector data.</param>
    /// <returns>A new normalized <c>float[]</c> with the same length as <paramref name="source"/>.</returns>
    private static float[] NormalizeToArray(float[] source)
    {
        var result = new float[source.Length];
        NormalizeVector(source, result);
        return result;
    }

    /// <summary>
    /// L2 normalization: <c>destination[i] = source[i] / ‖source‖₂</c>.
    /// Computes the L2 norm via <see cref="TensorPrimitives.Norm"/> (SIMD-accelerated),
    /// then applies vectorized division via <see cref="TensorPrimitives.Divide{T}(ReadOnlySpan{T}, T, Span{T})"/>.
    /// Zero vectors (norm = 0) clear the destination array to avoid NaN.
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
    /// Generic index factory: creates the appropriate vector index instance from the index configuration.
    /// A single method covers all index types; the similarity algorithm is injected via <typeparamref name="TSim"/>.
    /// </summary>
    private static IVectorIndex CreateIndex<TSim>(QuiverIndexAttribute? config, IVectorStore store)
        where TSim : struct, ISimilarity<float>
    {
        if (config is null || config.IndexType == VectorIndexType.Flat)
            return new FlatIndex<TSim>(store);

        return config.IndexType switch
        {
            VectorIndexType.HNSW => new HnswIndex<TSim>(store, config.M, config.EfConstruction, config.EfSearch),
            VectorIndexType.IVF => new IvfIndex<TSim>(store, config.NumClusters, config.NumProbes),
            VectorIndexType.KDTree => new KDTreeIndex<TSim>(store),
            _ => new FlatIndex<TSim>(store)
        };
    }

    /// <summary>
    /// Reflection-based index factory: converts a custom metric <see cref="Type"/> to a generic parameter at runtime
    /// and calls <see cref="CreateIndex{TSim}"/> to create the index instance. Executed only once at construction.
    /// </summary>
    private static IVectorIndex CreateIndexReflection(
        QuiverIndexAttribute? config, Type simType, IVectorStore store)
    {
        if (!simType.IsValueType || !typeof(ISimilarity<float>).IsAssignableFrom(simType))
            throw new InvalidOperationException(
                $"CustomSimilarity type '{simType.Name}' must be a struct implementing ISimilarity<float>.");

        var method = typeof(QuiverSet<TEntity>)
            .GetMethod(nameof(CreateIndex), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(simType);
        return (IVectorIndex)method.Invoke(null, [config, store])!;
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Releases all resources: flushes dirty pages, disposes vector stores, index instances, and the read-write lock.
    /// <para>
    /// In <see cref="EntityCacheMode.LazyPaging"/> mode, dirty pages are written back to disk
    /// before releasing <see cref="EntityPageCache{TEntity}"/>.
    /// </para>
    /// Uses <see cref="Interlocked.Exchange"/> to ensure the release logic executes only once on concurrent calls.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Lazy-loading mode: flush all dirty pages to disk
        _entities.Dispose();

        foreach (var store in _vectorStores.Values)
            store.Dispose();

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
/// A vector search result that encapsulates the matched entity and its similarity score.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <param name="Entity">The matched entity instance.</param>
/// <param name="Similarity">
/// The similarity score. Higher values indicate greater similarity.
/// The exact range depends on the distance metric: Cosine/DotProduct in [-1, 1]; Euclidean in (0, 1].
/// </param>
public record QuiverSearchResult<TEntity>(TEntity Entity, float Similarity);