namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
    #region CRUD Operations

    /// <summary>
    /// Adds a single entity. Throws if the primary key already exists.
    /// </summary>
    /// <param name="entity">The entity to add. The key property value must not be <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">The key is <c>null</c> or an entity with the same key already exists.</exception>
    /// <exception cref="ArgumentException">A vector field dimension does not match the declared dimension.</exception>
    public void Add(TEntity entity)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { AddCore(entity); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Adds a batch of entities using a two-phase commit: validates all entities first, then writes them atomically.
    /// <para>
    /// <b>Atomic semantics</b>: if any entity fails validation the entire batch is rolled back and no data is written.
    /// </para>
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    /// <exception cref="InvalidOperationException">A key is <c>null</c>, or a duplicate key exists within the batch or in the existing data.</exception>
    /// <exception cref="ArgumentException">A vector field dimension does not match for one of the entities.</exception>
    public void AddRange(IEnumerable<TEntity> entities)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            var entityList = entities as IList<TEntity> ?? [.. entities];
            if (entityList.Count == 0) return;

            // ── Phase 1: full pre-validation (no state changes; exception-safe) ──
            var batch = new (object Key, List<(string Name, Array Vector)> Vectors)[entityList.Count];
            var keysInBatch = new HashSet<object>(entityList.Count);

            for (var idx = 0; idx < entityList.Count; idx++)
            {
                var key = _getKey(entityList[idx])
                    ?? throw new InvalidOperationException("Key property value cannot be null.");

                if (_keyToId.ContainsKey(key) || !keysInBatch.Add(key))
                    throw new InvalidOperationException(
                        $"Duplicate key '{key}'. An entity with the same [QuiverKey] already exists.");

                batch[idx] = (key, PrepareVectors(entityList[idx]));
            }

            // ── Phase 2: commit all (no further exceptions after this point) ──
            for (var idx = 0; idx < entityList.Count; idx++)
            {
                var id = _nextId++;
                _entities.Set(id, entityList[idx]);
                _keyToId[batch[idx].Key] = id;

                foreach (var (name, vector) in batch[idx].Vectors)
                {
                    StoreVector(name, id, vector);
                    _indices[name].Add(id);
                }
            }
            NotifyHeapBytes();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Asynchronous version of <see cref="AddRange"/>. Offloads the CPU-intensive validation and
    /// index construction to the thread pool to avoid blocking the UI thread.
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    /// <param name="cancellationToken">Cancellation token. If cancelled, the operation may have partially completed; internal transaction semantics guarantee a consistent state.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => AddRange(entities), cancellationToken);
    }

    /// <summary>
    /// Inserts or updates an entity (upsert semantics). If an entity with the same key already exists
    /// it is removed first, then the new entity is added. Completes within a single write lock,
    /// making it more efficient than an external <c>Remove + Add</c>.
    /// </summary>
    /// <param name="entity">The entity to insert or update.</param>
    /// <exception cref="InvalidOperationException">The key is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A vector field dimension does not match.</exception>
    public void Upsert(TEntity entity)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            var key = _getKey(entity)
                ?? throw new InvalidOperationException("Key property value cannot be null.");

            RemoveCore(key);
            AddCore(entity);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes an entity by value. Matches by the entity's key property, not by reference.
    /// </summary>
    /// <param name="entity">The entity to remove.</param>
    /// <returns><c>true</c> if the entity was successfully removed; <c>false</c> if the key is <c>null</c> or not found.</returns>
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
    /// Removes an entity directly by its key value without requiring a reference to the entity.
    /// </summary>
    /// <param name="key">The key value of the entity to remove.</param>
    /// <returns><c>true</c> if the entity was removed; <c>false</c> if not found.</returns>
    public bool RemoveByKey(object key)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { return RemoveCore(key); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Finds an entity by its primary key. Uses a two-level dictionary (key → internal ID → entity)
    /// for O(1) complexity.
    /// </summary>
    /// <param name="key">The key value to look up.</param>
    /// <returns>The matching entity, or <c>null</c> if not found.</returns>
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
    /// Checks whether an entity with the given key exists. Queries only the key dictionary at O(1)
    /// complexity — one fewer lookup than <see cref="Find"/> since no entity object is retrieved.
    /// </summary>
    /// <param name="key">The key value to check.</param>
    /// <returns><c>true</c> if an entity with the key exists; otherwise <c>false</c>.</returns>
    public bool Exists(object key)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try { return _keyToId.ContainsKey(key); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Checks whether at least one entity satisfies the predicate. Takes a snapshot of entities
    /// under a read lock and short-circuits on the first match.
    /// <para>
    /// O(n) worst-case complexity. Use the <see cref="Exists(object)"/> overload (O(1)) when
    /// checking by primary key only.
    /// </para>
    /// </summary>
    /// <param name="predicate">The condition to test against each entity.</param>
    /// <returns><c>true</c> if at least one entity matches; otherwise <c>false</c>.</returns>
    public bool Exists(Func<TEntity, bool> predicate)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            foreach (var entity in _entities.Values)
            {
                if (predicate(entity))
                    return true;
            }
            return false;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Removes all entities, key mappings, and index data. The internal ID counter is reset to 0.
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
            foreach (var store in _vectorStores.Values)
                store.Clear();
            _pendingTombstones.Clear();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 添加实体的核心逻辑。采用两阶段提交保证异常安全：
    /// 阶段 1 校验并准备向量数据（不修改任何状态），阶段 2 原子写入。
    /// 调用方须持有写锁。
    /// </summary>
    private void AddCore(TEntity entity)
    {
        var key = _getKey(entity)
            ?? throw new InvalidOperationException("Key property value cannot be null.");

        if (_keyToId.ContainsKey(key))
            throw new InvalidOperationException(
                $"Duplicate key '{key}'. An entity with the same [QuiverKey] already exists.");

        var prepared = PrepareVectors(entity);

        var id = _nextId++;
        _entities.Set(id, entity);
        _keyToId[key] = id;

        foreach (var (name, indexVector) in prepared)
        {
            StoreVector(name, id, indexVector);
            _indices[name].Add(id);
        }

        NotifyHeapBytes();
    }

    /// <summary>
    /// 收集并校验实体的所有向量字段，返回准备好的索引数据。
    /// 此方法不修改任何状态（除 PreNormalize 字段会就地归一化实体数组），校验失败时可安全抛出异常。
    /// <list type="bullet">
    ///   <item>Nullable 字段：向量为 <c>null</c> 时跳过，不加入索引</item>
    ///   <item>Required 字段：向量为 <c>null</c> 时抛出 <see cref="ArgumentNullException"/></item>
    ///   <item>PreNormalize 字段：就地 L2 归一化实体本身持有的数组（零拷贝）</item>
    ///   <item>其他字段：直接复用实体的数组引用（零拷贝，调用方须避免后续修改）</item>
    /// </list>
    /// <para>
    /// <b>内存语义</b>：自 3.3.0 起，向量不再被防御性复制。向量数据是实体的“一部分”，
    /// 由实体对象 + <see cref="IVectorStore"/> 共同持有同一引用，避免在百万级数据上产生双份内存峰值。
    /// </para>
    /// </summary>
    private List<(string Name, Array IndexVector)> PrepareVectors(TEntity entity)
    {
        var prepared = new List<(string Name, Array IndexVector)>(_vectorFields.Count);
        foreach (var (name, field) in _vectorFields)
        {
            var raw = _vectorGetters[name](entity);

            if (raw is null || raw.Length == 0)   // ✅ 同时处理 null 和空数组
            {
                if (!field.Nullable)
                    throw new ArgumentNullException(name,
                        $"Vector field '{name}' is required but was null or empty. " +
                        $"Mark [QuiverVector(Nullable = true)] to allow null.");
                continue;
            }

            // 允许调用方传入声明维度的完整向量，也允许直接传入已截断到 EffectiveDimensions 的向量
            if (raw.Length != field.Dimensions && raw.Length != field.EffectiveDimensions)
                throw new ArgumentException(
                    $"Vector dimension mismatch on '{name}'. Expected {field.Dimensions} (or {field.EffectiveDimensions} when Matryoshka-truncated), got {raw.Length}");

            Array indexVector = field.ElementType == Indexing.VectorElementType.Float16
                ? PrepareHalfVector(field, (Half[])raw)
                : PrepareFloatVector(field, (float[])raw);

            prepared.Add((name, indexVector));
        }
        return prepared;
    }

    /// <summary>准备 fp32 索引向量：按需 Matryoshka 截断与 L2 归一化（语义同旧版本）。</summary>
    private static float[] PrepareFloatVector(QuiverFieldInfo field, float[] vector)
    {
        if (field.EffectiveDimensions < field.Dimensions && vector.Length == field.Dimensions)
        {
            // Matryoshka 截断：复制前 EffectiveDimensions 维，避免修改实体本身的数组
            var indexVector = new float[field.EffectiveDimensions];
            Array.Copy(vector, indexVector, field.EffectiveDimensions);
            if (field.PreNormalize)
                NormalizeVector(indexVector, indexVector);
            return indexVector;
        }

        if (field.PreNormalize)
            NormalizeVector(vector, vector); // in-place L2 normalization on the entity's own array
        return vector;
    }

    /// <summary>
    /// 准备 fp16 索引向量。截断/归一化语义与 fp32 一致，但归一化必须在 float 域进行：
    /// 先 widen 到 float、归一化、再 narrow 回 Half[]，避免 fp16 累加误差。
    /// 不需要归一化时直接复用/截断实体自身的 Half[]（零或低拷贝）。
    /// </summary>
    private static Half[] PrepareHalfVector(QuiverFieldInfo field, Half[] vector)
    {
        bool truncate = field.EffectiveDimensions < field.Dimensions && vector.Length == field.Dimensions;
        int dim = truncate ? field.EffectiveDimensions : vector.Length;

        if (!field.PreNormalize)
        {
            if (!truncate) return vector; // 零拷贝复用实体数组
            var sliced = new Half[dim];
            Array.Copy(vector, sliced, dim);
            return sliced;
        }

        // 归一化路径：fp16 → fp32 → 归一化 → fp16
        var f = new float[dim];
        Vorcyc.Quiver.Numerics.VectorMath.WidenHalfToFloat(vector.AsSpan(0, dim), f);
        NormalizeVector(f, f);
        return Vorcyc.Quiver.Numerics.VectorMath.NarrowFloatToHalf(f);
    }

    /// <summary>
    /// 按字段元素类型把准备好的索引向量写入对应 store：
    /// Float16 字段走 <see cref="Indexing.IVectorStore.StoreByRefHalf"/>（保持 fp16 物理存储），
    /// 其余走 <see cref="Indexing.IVectorStore.StoreByRef"/>。
    /// </summary>
    private void StoreVector(string name, int id, Array indexVector)
    {
        if (indexVector is Half[] halfVec)
            _vectorStores[name].StoreByRefHalf(id, halfVec);
        else
            _vectorStores[name].StoreByRef(id, (float[])indexVector);
    }

    /// <summary>
    /// 删除实体的核心逻辑。从实体存储、主键映射和所有索引中移除。
    /// 调用方须持有写锁。
    /// </summary>
    /// <param name="key">要删除的实体主键值。</param>
    /// <returns>成功删除返回 <c>true</c>；主键不存在返回 <c>false</c>。</returns>
    private bool RemoveCore(object key)
    {
        if (!_keyToId.TryGetValue(key, out var id))
            return false;

        _entities.Remove(id);
        _keyToId.Remove(key);
        foreach (var index in _indices.Values)
            index.Remove(id);
        foreach (var store in _vectorStores.Values)
            store.Remove(id);

        // Record the dead internal id so the next AppendAsync can emit a Tombstone segment.
        _pendingTombstones.Add(id);
        return true;
    }

    #endregion
}