using System.Linq.Expressions;

namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
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
                    break;
            }
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 搜索所有相似度不低于阈值的实体。结果数量不固定，取决于数据分布。
    /// </summary>
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

    /// <summary><see cref="Search(Expression{Func{TEntity, float[]}}, float[], int)"/> 的异步版本。</summary>
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        Expression<Func<TEntity, float[]>> vectorSelector,
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(vectorSelector, queryVector, topK), cancellationToken);
    }

    /// <summary><see cref="Search(Expression{Func{TEntity, float[]}}, float[], int, Func{TEntity, bool}, int)"/> 的异步版本。</summary>
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

    /// <summary><see cref="SearchByThreshold"/> 的异步版本。</summary>
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

    /// <summary><see cref="SearchTop1(Expression{Func{TEntity, float[]}}, float[])"/> 的异步版本。</summary>
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

    /// <summary>
    /// 在默认（唯一）向量字段上搜索 Top-K。仅当实体只有一个 [QuiverVector] 字段时可用。
    /// </summary>
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

    /// <summary>在默认向量字段上搜索最相似的单个实体。</summary>
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
    public Task<List<QuiverSearchResult<TEntity>>> SearchAsync(
        float[] queryVector, int topK,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => Search(queryVector, topK), cancellationToken);
    }

    /// <summary><see cref="SearchTop1(float[])"/> 的异步版本。</summary>
    public Task<QuiverSearchResult<TEntity>?> SearchTop1Async(
        float[] queryVector,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => SearchTop1(queryVector), cancellationToken);
    }

    #endregion

    #region 核心搜索辅助

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
}