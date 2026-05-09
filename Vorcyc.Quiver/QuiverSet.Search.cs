using System.Linq.Expressions;

namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
    #region Vector Search (Synchronous)

    /// <summary>
    /// Searches the specified vector field for the top-K most similar entities.
    /// </summary>
    /// <param name="vectorSelector">
    /// Vector field selector expression. Must be a simple property access, e.g. <c>e =&gt; e.Embedding</c>.
    /// </param>
    /// <param name="queryVector">The query vector. Its dimension must match the field definition.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of search results sorted by descending similarity.</returns>
    /// <exception cref="ArgumentException">The selector is not a property-access expression, or the property is not marked with [QuiverVector].</exception>
    /// <exception cref="ArgumentOutOfRangeException">The query vector dimension does not match the field definition.</exception>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ValidateQueryDim(field, queryVector);
            return MapResults(SearchIndex(name, field, queryVector, topK));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Searches the specified vector field for the top-K most similar entities and filters results by an expression.
    /// <para>
    /// <b>Note</b>: each call compiles the expression tree (approximately ~50 µs overhead).
    /// For high-frequency scenarios use the <see cref="Func{T, TResult}"/> overload and cache the compiled delegate externally.
    /// </para>
    /// </summary>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        Expression<Func<TEntity, bool>> filter)
    {
        return Search(vectorSelector, queryVector, topK, filter.Compile());
    }

    /// <summary>
    /// Searches the specified vector field for the top-K most similar entities and filters results by a delegate.
    /// <para>
    /// Uses an over-fetch strategy internally: retrieves <c>topK × overFetchMultiplier</c> candidates
    /// from the index and filters them until <c>topK</c> results are collected. Increase
    /// <paramref name="overFetchMultiplier"/> when the filter rejects a large proportion of candidates.
    /// </para>
    /// </summary>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        Func<TEntity, bool> filter,
        int overFetchMultiplier = 4)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overFetchMultiplier);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ValidateQueryDim(field, queryVector);

            var overFetch = Math.Min(topK * overFetchMultiplier, _indices[name].Count);

            var results = new List<QuiverSearchResult<TEntity>>(topK);
            foreach (var (id, similarity) in SearchIndex(name, field, queryVector, overFetch))
            {
                if (!_entities.TryGetValue(id, out var entity))
                    continue;
                if (!filter(entity))
                    continue;

                results.Add(new QuiverSearchResult<TEntity>(entity, similarity));
                if (results.Count >= topK)
                    break;
            }
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Returns all entities whose similarity to the query vector is at or above the threshold.
    /// The number of results varies depending on the data distribution.
    /// </summary>
    public List<QuiverSearchResult<TEntity>> SearchByThreshold(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        float threshold)
    {
        ThrowIfDisposed();
        if (float.IsNaN(threshold))
            throw new ArgumentException("threshold must not be NaN.", nameof(threshold));
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ValidateQueryDim(field, queryVector);
            return MapResults(SearchIndexByThreshold(name, field, queryVector, threshold));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Returns the single most similar entity. Equivalent to <c>Search(selector, query, topK: 1)</c>
    /// but avoids an intermediate <c>List</c> allocation.
    /// </summary>
    public QuiverSearchResult<TEntity>? SearchTop1(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            ValidateQueryDim(field, queryVector);
            return MapTop1(SearchIndex(name, field, queryVector, 1));
        }
        finally { _lock.ExitReadLock(); }
    }

    #endregion

    #region Vector Search (Asynchronous)

    /// <summary>Asynchronous version of <see cref="Search(Expression{Func{TEntity, float[]}}, float[], int)"/>.</summary>
    /// <remarks>
    /// <b>This is not true I/O async</b>: the synchronous search is dispatched to the thread pool via
    /// <see cref="Task.Run(Action, CancellationToken)"/> to offload CPU work from the UI or request thread.
    /// In high-concurrency server scenarios use the synchronous overload directly to avoid unnecessary thread-pool overhead.
    /// The cancellation token only takes effect before the task is scheduled; once the search loop starts it cannot be interrupted.
    /// </remarks>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(vectorSelector, queryVector, topK), cancellationToken);
    }

    /// <summary>Asynchronous version of <see cref="Search(Expression{Func{TEntity, float[]}}, float[], int, Func{TEntity, bool}, int)"/>.</summary>
    /// <remarks>CPU-bound offload wrapper; same semantics as <see cref="SearchAsync(Expression{Func{TEntity, float[]}}, float[], int, CancellationToken)"/>.</remarks>
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

    /// <summary>Asynchronous version of <see cref="SearchByThreshold"/>.</summary>
    /// <remarks>CPU-bound offload wrapper; not true I/O async.</remarks>
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

    /// <summary>Asynchronous version of <see cref="SearchTop1(Expression{Func{TEntity, float[]}}, float[])"/>.</summary>
    /// <remarks>CPU-bound offload wrapper; not true I/O async.</remarks>
    public Task<QuiverSearchResult<TEntity>?> SearchTop1Async(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => SearchTop1(vectorSelector, queryVector), cancellationToken);
    }

    #endregion

    #region Vector Search (Half[] Query Overloads)

    // Note: fp16 fields can be queried with either float[] (reuses the existing float overloads)
    // or Half[] directly. Half[] queries are widened to float[] at the entry point once,
    // then the standard float search/normalization/truncation pipeline is reused,
    // guaranteeing results identical to float[] queries.

    /// <summary>
    /// Searches the specified vector field for the top-K most similar entities using a <c>Half[]</c> query vector.
    /// The query vector is widened to <c>float[]</c> at the entry point and the standard search path is reused.
    /// </summary>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, Half[]>> vectorSelector,
        Half[] queryVector,
        int topK)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentNullException.ThrowIfNull(queryVector);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            var query = WidenQuery(queryVector);
            ValidateQueryDim(field, query);
            return MapResults(SearchIndex(name, field, query, topK));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Searches the specified vector field for the top-K most similar entities using a <c>Half[]</c> query vector,
    /// filtered by a delegate (same over-fetch strategy as the <c>float[]</c> overload).
    /// </summary>
    public List<QuiverSearchResult<TEntity>> Search(
        Expression<Func<TEntity, Half[]>> vectorSelector,
        Half[] queryVector,
        int topK,
        Func<TEntity, bool> filter,
        int overFetchMultiplier = 4)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overFetchMultiplier);
        ArgumentNullException.ThrowIfNull(queryVector);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            var query = WidenQuery(queryVector);
            ValidateQueryDim(field, query);

            var overFetch = Math.Min(topK * overFetchMultiplier, _indices[name].Count);

            var results = new List<QuiverSearchResult<TEntity>>(topK);
            foreach (var (id, similarity) in SearchIndex(name, field, query, overFetch))
            {
                if (!_entities.TryGetValue(id, out var entity))
                    continue;
                if (!filter(entity))
                    continue;

                results.Add(new QuiverSearchResult<TEntity>(entity, similarity));
                if (results.Count >= topK)
                    break;
            }
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Searches all entities whose similarity to the <c>Half[]</c> query vector meets or exceeds the threshold.</summary>
    public List<QuiverSearchResult<TEntity>> SearchByThreshold(
        Expression<Func<TEntity, Half[]>> vectorSelector,
        Half[] queryVector,
        float threshold)
    {
        ThrowIfDisposed();
        if (float.IsNaN(threshold))
            throw new ArgumentException("threshold must not be NaN.", nameof(threshold));
        ArgumentNullException.ThrowIfNull(queryVector);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            var query = WidenQuery(queryVector);
            ValidateQueryDim(field, query);
            return MapResults(SearchIndexByThreshold(name, field, query, threshold));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Returns the single most similar entity using a <c>Half[]</c> query vector.</summary>
    public QuiverSearchResult<TEntity>? SearchTop1(
        Expression<Func<TEntity, Half[]>> vectorSelector,
        Half[] queryVector)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(queryVector);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = ResolveField(vectorSelector);
            var query = WidenQuery(queryVector);
            ValidateQueryDim(field, query);
            return MapTop1(SearchIndex(name, field, query, 1));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Asynchronous version of <see cref="Search(Expression{Func{TEntity, Half[]}}, Half[], int)"/> (CPU-bound offload).</summary>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        Expression<Func<TEntity, Half[]>> vectorSelector,
        Half[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(vectorSelector, queryVector, topK), cancellationToken);
    }

    /// <summary>Widens a <c>Half[]</c> query vector to a new <c>float[]</c> for reuse by the existing float search pipeline.</summary>
    private static float[] WidenQuery(Half[] queryVector)
        => Vorcyc.Quiver.Numerics.VectorMath.WidenHalfToFloat(queryVector);

    #endregion

    #region Default-Field Convenience Methods (Synchronous + Asynchronous)

    /// <summary>
    /// Searches the default (sole) vector field for the top-K most similar entities.
    /// Only available when the entity has exactly one <c>[QuiverVector]</c> field.
    /// </summary>
    public List<QuiverSearchResult<TEntity>> Search(float[] queryVector, int topK)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        _lock.EnterReadLock();
        try
        {
            var (name, field) = GetDefaultField();
            ValidateQueryDim(field, queryVector);
            return MapResults(SearchIndex(name, field, queryVector, topK));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Returns the single most similar entity on the default vector field.</summary>
    public QuiverSearchResult<TEntity>? SearchTop1(float[] queryVector)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var (name, field) = GetDefaultField();
            ValidateQueryDim(field, queryVector);
            return MapTop1(SearchIndex(name, field, queryVector, 1));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Asynchronous version of <see cref="Search(float[], int)"/>.</summary>
    /// <remarks>CPU-bound offload wrapper; not true I/O async.</remarks>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        float[] queryVector, int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(queryVector, topK), cancellationToken);
    }

    /// <summary>Asynchronous version of <see cref="SearchTop1(float[])"/>.</summary>
    /// <remarks>CPU-bound offload wrapper; not true I/O async.</remarks>
    public Task<QuiverSearchResult<TEntity>?> SearchTop1Async(
        float[] queryVector,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => SearchTop1(queryVector), cancellationToken);
    }

    #endregion

    #region Core Search Helpers

    /// <summary>
    /// Normalizes the query vector if needed. For Cosine metric (<c>PreNormalize=true</c>) returns a
    /// normalized copy; for other metrics returns the original array (zero-copy).
    /// When Matryoshka truncation is active (<see cref="QuiverFieldInfo.EffectiveDimensions"/> &lt;
    /// <see cref="QuiverFieldInfo.Dimensions"/>), the first <c>EffectiveDimensions</c> elements are
    /// extracted first, then normalized if required.
    /// </summary>
    private static float[] NormalizeIfNeeded(QuiverFieldInfo field, float[] queryVector)
    {
        float[] aligned;
        if (field.EffectiveDimensions < field.Dimensions && queryVector.Length == field.Dimensions)
        {
            aligned = new float[field.EffectiveDimensions];
            Array.Copy(queryVector, aligned, field.EffectiveDimensions);
        }
        else
        {
            aligned = queryVector;
        }

        if (!field.PreNormalize) return aligned;

        // After truncation, re-normalization is required (preserves semantics: does not mutate the caller's array in-place)
        if (ReferenceEquals(aligned, queryVector))
            return NormalizeToArray(queryVector);
        NormalizeVector(aligned, aligned);
        return aligned;
    }

    /// <summary>Invokes the index's top-K search, handling query vector normalization automatically.</summary>
    private List<(int Id, float Similarity)> SearchIndex(
        string name, QuiverFieldInfo field, float[] queryVector, int topK)
        => _indices[name].Search(NormalizeIfNeeded(field, queryVector), topK);

    /// <summary>校验查询向量长度：必须等于声明维度或 EffectiveDimensions。</summary>
    private static void ValidateQueryDim(QuiverFieldInfo field, float[] queryVector)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        if (queryVector.Length != field.Dimensions && queryVector.Length != field.EffectiveDimensions)
            throw new ArgumentOutOfRangeException(
                nameof(queryVector),
                $"Query vector length {queryVector.Length} does not match field dim {field.Dimensions} (or effective {field.EffectiveDimensions}).");
    }

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
    /// Maps only the first valid result, avoiding an intermediate List allocation on the SearchTop1 path.
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
}