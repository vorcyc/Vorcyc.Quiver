using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;
using Vorcyc.Quiver.Indexing;
using Vorcyc.Quiver.Numerics;
using Vorcyc.Quiver.Payloads;
using Vorcyc.Quiver.Similarity;

namespace Vorcyc.Quiver;

/// <summary>
/// Metadata for a single vector field: dimensions, distance metric, index configuration,
/// pre-normalization flag, and nullability flag.
/// <para>
/// Created automatically during <see cref="QuiverSet{TEntity}"/> construction by scanning
/// <see cref="QuiverVectorAttribute"/> via reflection. Frozen into a
/// <see cref="System.Collections.Frozen.FrozenDictionary{TKey, TValue}"/> after construction;
/// immutable for the lifetime of the set.
/// </para>
/// </summary>
/// <param name="Dimensions">
/// Fixed vector dimension. The array length supplied on write must equal this value;
/// otherwise an <see cref="ArgumentException"/> is thrown.
/// Sourced from <see cref="QuiverVectorAttribute.Dimensions"/>.
/// </param>
/// <param name="Metric">
/// Distance metric that determines how similarity is computed.
/// Sourced from <see cref="QuiverVectorAttribute.Metric"/>.
/// </param>
/// <param name="IndexConfig">
/// Index configuration. When <c>null</c>, a <see cref="Indexing.FlatIndex{TSim}"/> brute-force search is used.
/// Sourced from the optional <see cref="QuiverIndexAttribute"/> on the property.
/// </param>
/// <param name="PreNormalize">
/// Whether to L2-normalize vectors on write and at query time.
/// Automatically enabled when <paramref name="Metric"/> is <see cref="DistanceMetric.Cosine"/>,
/// allowing Dot to be substituted for CosineSimilarity at search time for better performance.
/// </param>
/// <param name="Nullable">
/// Whether the vector value may be <c>null</c>. Sourced from <see cref="QuiverVectorAttribute.Nullable"/>.
/// <para>
/// When <c>true</c>, entities with a <c>null</c> vector can still be stored but are not added to
/// the field's index, so searches on that field will not return them. When <c>false</c> (default),
/// a <c>null</c> vector throws <see cref="ArgumentNullException"/>.
/// </para>
/// </param>
/// <param name="MemoryMode">
/// The resolved vector memory mode for this field.
/// </param>
/// <param name="EffectiveDimensions">
/// The actual working dimension after Matryoshka truncation. Equals <paramref name="Dimensions"/>
/// when no truncation is applied. Indexing, similarity computation, mmap row layout, and lazy
/// materialization all use this dimension.
/// </param>
/// <param name="Quantization">
/// Storage quantization mode for this field. <see cref="VectorQuantization.None"/> means native
/// float32; <see cref="VectorQuantization.Sq8"/> means SQ8-encoded persistence and mmap while
/// indexing and search still operate on a float32 view.
/// </param>
internal record QuiverFieldInfo(
    int Dimensions,
    DistanceMetric Metric,
    QuiverIndexAttribute? IndexConfig,
    bool PreNormalize,
    bool Nullable,
    VectorMemoryMode MemoryMode,
    int EffectiveDimensions,
    VectorQuantization Quantization,
    Vorcyc.Quiver.Indexing.VectorElementType ElementType = Vorcyc.Quiver.Indexing.VectorElementType.Float32);

internal record QuiverLargeFieldInfo(
    bool Nullable,
    LargeFieldMemoryMode MemoryMode);


/// <summary>
/// A typed vector set that provides CRUD operations and vector similarity search for entities.
/// <para>
/// <b>Thread safety</b>: Uses <see cref="ReaderWriterLockSlim"/> for read-write separation.
/// Multiple search operations can run concurrently; write operations are mutually exclusive.
/// </para>
/// <para>
/// <b>Async support</b>: All potentially long-running operations (search, bulk writes) have
/// <c>Async</c> overloads that offload CPU-intensive work to the thread pool via
/// <see cref="Task.Run(Action)"/>, avoiding blocking the calling thread (e.g. the UI thread).
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
public partial class QuiverSet<TEntity> : IDisposable, IEnumerable<TEntity>, Vorcyc.Quiver.Runtime.ILazyVectorSource, Vorcyc.Quiver.Runtime.ILazyLargeFieldSource where TEntity : class, new()
{
    // ──────────────────────────────────────────────────────────────
    // Storage layer: bidirectional mapping of internal ID → entity; internal IDs are auto-incremented by _nextId
    // ──────────────────────────────────────────────────────────────

    /// <summary>Internal ID → entity mapping. Entities are ordinary in-memory records.</summary>
    private readonly InMemoryEntityStore<TEntity> _entities;

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

    /// <summary>Vector field name → compiled vector property accessor. Frozen after construction.
    /// 返回基类 <see cref="Array"/>：Float32 字段为 <c>float[]</c>，Float16 字段为 <c>Half[]</c>。</summary>
    private readonly FrozenDictionary<string, Func<TEntity, Array?>> _vectorGetters;

    /// <summary>Vector field name → corresponding vector index instance. Frozen after construction.</summary>
    private readonly FrozenDictionary<string, IVectorIndex> _indices;

    /// <summary>Vector field name → vector data store instance. Frozen after construction.</summary>
    private readonly FrozenDictionary<string, IVectorStore> _vectorStores;

    private readonly FrozenDictionary<string, QuiverLargeFieldInfo> _largeFields;

    private readonly LargeFieldStore? _largeFieldStore;

    /// <summary>
    /// Injected by <see cref="QuiverDbContext"/> after construction. Used to signal heap-vector byte
    /// budget overflows back to the context, which then performs a single-flight Heap → Mmap promotion.
    /// May be <c>null</c> for standalone sets not managed by a context (no promotion is triggered).
    /// </summary>
    private IPromotionCoordinator? _promotionCoordinator;

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
    /// Tombstoned internal-row ids accumulated since the last persist.
    /// Populated by <see cref="QuiverSet{TEntity}.RemoveCore"/>; consumed and cleared by
    /// <see cref="QuiverDbContext.AppendAsync"/> (writes a <c>SegmentKind.Tombstone</c> segment)
    /// or <see cref="QuiverDbContext.SaveAsync"/> (which physically rewrites the file).
    /// Access must be guarded by the write lock.
    /// </summary>
    private readonly List<int> _pendingTombstones = new();

    /// <summary>
    /// Initializes the vector collection. Scans properties of <typeparamref name="TEntity"/> via reflection
    /// to auto-discover the primary key and vector fields, compiles property accessors, and creates
    /// the corresponding index and vector store instances for each vector field.
    /// </summary>
    /// <param name="databasePath">
    /// Database path.
    /// </param>
    /// <param name="largeFieldMemoryMode">Memory strategy for large fields.</param>
    /// <param name="vectorMemoryMode">Global memory strategy for vector payloads.</param>
    /// <param name="vectorMemoryMapThresholdBytes">File-size threshold used by <see cref="GlobalVectorMemoryMode.Auto"/>.</param>
    /// <param name="largeFieldMaxCachedPayloads">Maximum number of cached large-field payloads in paged-cache mode.</param>
    /// <exception cref="InvalidOperationException">
    /// The entity type is missing a <see cref="QuiverKeyAttribute"/> primary key, or has no <see cref="QuiverVectorAttribute"/> vector fields.
    /// </exception>
    internal QuiverSet(
        string? databasePath = null,
        GlobalLargeFieldMemoryMode largeFieldMemoryMode = GlobalLargeFieldMemoryMode.InMemory,
        GlobalVectorMemoryMode vectorMemoryMode = GlobalVectorMemoryMode.InMemory,
        long vectorMemoryMapThresholdBytes = 256L * 1024 * 1024,
        int largeFieldMaxCachedPayloads = 128)
    {
        var type = typeof(TEntity);

        // ── Discover primary key property ──
        var keyProp = type.GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<QuiverKeyAttribute>() != null)
            ?? throw new InvalidOperationException($"Entity {type.Name} must have a [QuiverKey] property.");

        _getKey = CompileGetter<object?>(keyProp);

        // ── Discover and validate [QuiverLargeField] large-field properties ──
        // 仅做类型校验：实际的 Blob 段写入/读取在 BinaryStorageProvider 中通过属性反射完成，
        // 无需在 QuiverSet 上维护额外的字典（与向量不同，blob 字段没有索引/store 概念）。
        var largeFields = new Dictionary<string, QuiverLargeFieldInfo>(StringComparer.Ordinal);
        foreach (var bp in type.GetProperties())
        {
            var largeFieldAttr = bp.GetCustomAttribute<QuiverLargeFieldAttribute>();
            if (largeFieldAttr is null) continue;
            if (bp.PropertyType != typeof(byte[]))
                throw new InvalidOperationException(
                    $"[QuiverLargeField] property '{bp.Name}' on {type.Name} must be of type byte[].");
            if (bp.GetCustomAttribute<QuiverVectorAttribute>() != null)
                throw new InvalidOperationException(
                    $"Property '{bp.Name}' on {type.Name} cannot have both [QuiverLargeField] and [QuiverVector].");

            var fieldMode = ResolveLargeFieldMemoryMode(largeFieldMemoryMode, largeFieldAttr.MemoryMode);
            if (fieldMode != LargeFieldMemoryMode.InMemory)
                ValidateLazyLargeFieldAccessor(type, bp, fieldMode);
            largeFields[bp.Name] = new QuiverLargeFieldInfo(largeFieldAttr.Nullable, fieldMode);
        }
        _largeFields = largeFields.ToFrozenDictionary(StringComparer.Ordinal);
        _largeFieldStore = _largeFields.Values.Any(f => f.MemoryMode == LargeFieldMemoryMode.PagedCache)
            ? new LargeFieldStore(cacheEnabled: true, largeFieldMaxCachedPayloads)
            : _largeFields.Values.Any(f => f.MemoryMode == LargeFieldMemoryMode.LazyLoad)
                ? new LargeFieldStore(cacheEnabled: false)
                : null;

        // ── Discover and register all vector fields ──
        var vectorProps = type.GetProperties()
            .Where(p => p.GetCustomAttribute<QuiverVectorAttribute>() != null)
            .ToList();

        // ── Resolve GlobalVectorMemoryMode.Auto ──
        // Auto 语义（按阈值切换）：
        //   1. DatabasePath 必须已配置；
        //   2. 目标文件已存在 且 文件长度 ≥ vectorMemoryMapThresholdBytes 时启用 MemoryMapped，
        //      否则继续使用 InMemory（追求最低延迟）。
        // 这里以"文件总字节数"作为"向量负载体积"的近似代理：v4 文件绝大部分体积都是 VectorBlob 段，
        // 选用文件长度避免了构造期就 open/parse 整个 footer，且对首次空库友好（默认 Heap）。
        // 数据增长到阈值后，下一次打开数据库会自动切换到 Mmap。
        var globalVectorMode = vectorMemoryMode;
        if (globalVectorMode == GlobalVectorMemoryMode.Auto)
        {
            long fileBytes = 0;
            if (!string.IsNullOrEmpty(databasePath) && File.Exists(databasePath))
            {
                try { fileBytes = new FileInfo(databasePath).Length; }
                catch { fileBytes = 0; }
            }

            globalVectorMode = (!string.IsNullOrEmpty(databasePath)
                                && fileBytes >= vectorMemoryMapThresholdBytes)
                ? GlobalVectorMemoryMode.MemoryMapped
                : GlobalVectorMemoryMode.InMemory;
        }

        var vectorFields = new Dictionary<string, QuiverFieldInfo>();
        var vectorGetters = new Dictionary<string, Func<TEntity, Array?>>();
        var vectorStores = new Dictionary<string, IVectorStore>();
        var indices = new Dictionary<string, IVectorIndex>();

        foreach (var prop in vectorProps)
        {
            // 支持两种向量元素类型：float[]（fp32，默认）与 Half[]（fp16，内存/磁盘减半）。
            // 其它类型（如 double[]）暂不支持。
            Indexing.VectorElementType elementType;
            if (prop.PropertyType == typeof(float[]))
                elementType = Indexing.VectorElementType.Float32;
            else if (prop.PropertyType == typeof(Half[]))
                elementType = Indexing.VectorElementType.Float16;
            else
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name} must be of type float[] or Half[].");

            var vectorAttr = prop.GetCustomAttribute<QuiverVectorAttribute>()!;
            var indexAttr = prop.GetCustomAttribute<QuiverIndexAttribute>();
            var metric = vectorAttr.Metric;
            var preNormalize = vectorAttr.CustomSimilarity is null && metric == DistanceMetric.Cosine;
            var fieldMemoryMode = ResolveVectorFieldMemoryMode(globalVectorMode, vectorAttr.MemoryMode);

            if (fieldMemoryMode == VectorMemoryMode.MemoryMapped && string.IsNullOrEmpty(databasePath))
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name}: {nameof(VectorMemoryMode.MemoryMapped)} " +
                    $"requires a valid database path.");

            if (fieldMemoryMode != VectorMemoryMode.InMemory)
                ValidateLazyVectorAccessor(type, prop, fieldMemoryMode);

            // ── Matryoshka 有效维度校验 ──
            var declaredDim = vectorAttr.Dimensions;
            var effectiveDim = vectorAttr.EffectiveDimensions;
            if (effectiveDim <= 0 || effectiveDim >= declaredDim)
            {
                effectiveDim = declaredDim; // 0 / 越界 / 等同 ⇒ 不截断
            }
            else if (effectiveDim < 1)
            {
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name}: EffectiveDimensions must be ≥ 1.");
            }

            // ── 量化与度量兼容性校验 ──
            var quantization = vectorAttr.Quantization;
            if (quantization == VectorQuantization.Sq8 && metric == DistanceMetric.Hamming)
            {
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name}: VectorQuantization.Sq8 is not compatible with DistanceMetric.Hamming.");
            }
            // Half[] 字段已经是 fp16 物理压缩，与 SQ8 标量量化互斥（两者都作用于物理存储精度）。
            if (quantization == VectorQuantization.Sq8 && elementType == Indexing.VectorElementType.Float16)
            {
                throw new InvalidOperationException(
                    $"[QuiverVector] property '{prop.Name}' on {type.Name}: VectorQuantization.Sq8 is not compatible with Half[] fields (fp16 storage already halves memory).");
            }

            vectorFields[prop.Name] = new QuiverFieldInfo(
                declaredDim, metric, indexAttr, preNormalize, vectorAttr.Nullable, fieldMemoryMode,
                effectiveDim, quantization, elementType);

            // getter 返回基类 Array：float[] 字段返回 float[]，Half[] 字段返回 Half[]，
            // 由 PrepareVectors 按 QuiverFieldInfo.ElementType 分派具体写入路径。
            vectorGetters[prop.Name] = CompileGetter<Array?>(prop);

            // ── Create the corresponding IVectorStore for each vector field ──
            // schema v2 + VectorMemoryMode.MemoryMapped：构造时即生成 Mmap 存储；具体 mmap 区域在 LoadAsync 后由
            // BinaryStorageProvider 回填（未绑定时仅 overflow 生效，行为等同 Heap）。
            // 用 VectorStoreSlot 包一层，让运行时 Heap → Mmap 升级时索引看到的引用不变。
            // Half[] 字段在 InMemory 模式使用 HalfHeapVectorStore（物理 fp16，读时 widen 到 float 视图）。
            IVectorStore inner = (fieldMemoryMode, elementType) switch
            {
                (VectorMemoryMode.MemoryMapped, _) => new MmapVectorStore(effectiveDim),
                (_, Indexing.VectorElementType.Float16) => new HalfHeapVectorStore(effectiveDim),
                _                                  => new HeapVectorStore(effectiveDim),
            };
            var store = new VectorStoreSlot(inner);
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

        // ── Initialize entity container ──
        _entities = new InMemoryEntityStore<TEntity>();
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
    /// The <c>Half[]</c> selector variant of <see cref="ResolveField(Expression{Func{TEntity, float[]}})"/>,
    /// used by query overloads targeting fp16 vector fields.
    /// </summary>
    private (string Name, QuiverFieldInfo Field) ResolveField(
        Expression<Func<TEntity, Half[]>> vectorSelector)
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
    /// Delegates internally to <see cref="NormalizeVector"/>.
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
    /// Computes the L2 norm and then divides each component by that norm.
    /// Zero vectors (norm = 0) clear the destination array to avoid NaN.
    /// </summary>
    private static void NormalizeVector(ReadOnlySpan<float> source, Span<float> destination)
    {
        var norm = VectorMath.Norm(source);
        if (norm > 0f)
            VectorMath.Divide(source, norm, destination);
        else
            destination.Clear();
    }

    /// <summary>
    /// In-place L2 normalization for <c>Half[]</c>: widens to float, normalizes, then narrows back to fp16.
    /// Zero vectors are cleared to avoid NaN.
    /// </summary>
    private static void NormalizeHalfInPlace(Half[] vector)
    {
        var f = new float[vector.Length];
        VectorMath.WidenHalfToFloat(vector, f);
        NormalizeVector(f, f);
        VectorMath.NarrowFloatToHalf(f, vector);
    }

    private static VectorMemoryMode ResolveVectorFieldMemoryMode(
        GlobalVectorMemoryMode globalMode,
        VectorMemoryMode fieldMode)
        => globalMode switch
        {
            GlobalVectorMemoryMode.InMemory => VectorMemoryMode.InMemory,
            GlobalVectorMemoryMode.MemoryMapped => VectorMemoryMode.MemoryMapped,
            GlobalVectorMemoryMode.Auto => VectorMemoryMode.InMemory,
            GlobalVectorMemoryMode.PerField => fieldMode,
            _ => VectorMemoryMode.InMemory
        };

    private static LargeFieldMemoryMode ResolveLargeFieldMemoryMode(
        GlobalLargeFieldMemoryMode globalMode,
        LargeFieldMemoryMode fieldMode)
        => globalMode switch
        {
            GlobalLargeFieldMemoryMode.InMemory => LargeFieldMemoryMode.InMemory,
            GlobalLargeFieldMemoryMode.LazyLoad => LargeFieldMemoryMode.LazyLoad,
            GlobalLargeFieldMemoryMode.PagedCache => LargeFieldMemoryMode.PagedCache,
            GlobalLargeFieldMemoryMode.PerField => fieldMode,
            _ => LargeFieldMemoryMode.InMemory
        };

    private static void ValidateLazyVectorAccessor(
        Type entityType,
        PropertyInfo prop,
        VectorMemoryMode memoryMode)
    {
        if (prop.GetMethod is null || prop.SetMethod is null)
            throw new InvalidOperationException(
                $"[QuiverVector] property '{prop.Name}' on {entityType.Name} must have both getter and setter when " +
                $"{nameof(VectorMemoryMode)}.{memoryMode} is used.");

        // 源生成器为 float[] 属性生成 float[] backing，为 Half[] 属性生成 Half[] backing；
        // 要求 backing 字段类型与属性的向量元素类型一致。
        var expectedBacking = prop.PropertyType == typeof(Half[]) ? typeof(Half[]) : typeof(float[]);
        var backing = entityType.GetField("__" + prop.Name + "_backing", BindingFlags.Instance | BindingFlags.NonPublic);
        if (backing is null || backing.FieldType != expectedBacking)
        {
            var elementTypeName = expectedBacking == typeof(Half[]) ? "Half[]" : "float[]";
            throw new InvalidOperationException(
                $"[QuiverVector] property '{prop.Name}' on {entityType.Name} uses {nameof(VectorMemoryMode)}.{memoryMode}. " +
                $"Declare it as 'public partial {elementTypeName}? {prop.Name} {{ get; set; }}' in a partial type so the Quiver source generator can create the lazy accessor.");
        }
    }

    private static void ValidateLazyLargeFieldAccessor(
        Type entityType,
        PropertyInfo prop,
        LargeFieldMemoryMode memoryMode)
    {
        if (prop.GetMethod is null || prop.SetMethod is null)
            throw new InvalidOperationException(
                $"[QuiverLargeField] property '{prop.Name}' on {entityType.Name} must have both getter and setter when " +
                $"{nameof(LargeFieldMemoryMode)}.{memoryMode} is used.");

        var backing = entityType.GetField("__" + prop.Name + "_backing", BindingFlags.Instance | BindingFlags.NonPublic);
        if (backing is null || backing.FieldType != typeof(byte[]))
            throw new InvalidOperationException(
                $"[QuiverLargeField] property '{prop.Name}' on {entityType.Name} uses {nameof(LargeFieldMemoryMode)}.{memoryMode}. " +
                $"Declare it as 'public partial byte[]? {prop.Name} {{ get; set; }}' in a partial type so the Quiver source generator can create the lazy accessor.");
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
    /// Releases all resources: disposes vector stores, index instances, and the read-write lock.
    /// Uses <see cref="Interlocked.Exchange"/> to ensure the release logic executes only once on concurrent calls.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var store in _vectorStores.Values)
            store.Dispose();

        foreach (var index in _indices.Values)
        {
            if (index is IDisposable disposable)
                disposable.Dispose();
        }

        _largeFieldStore?.Dispose();

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