namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
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
            var batch = new (object Key, List<(string Name, float[] Vector)> Vectors)[entityList.Count];
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

            // ── 阶段 2：全部提交（此后不会再抛异常）──
            for (var idx = 0; idx < entityList.Count; idx++)
            {
                var id = _nextId++;
                _entities[id] = entityList[idx];
                _keyToId[batch[idx].Key] = id;

                foreach (var (name, vector) in batch[idx].Vectors)
                    _indices[name].Add(id, vector);

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
    /// 判断指定主键的实体是否存在。仅查找主键字典，O(1) 复杂度，
    /// 比 <see cref="Find"/> 少一次字典查找（无需反查实体对象）。
    /// </summary>
    /// <param name="key">要检查的主键值。</param>
    /// <returns>存在返回 <c>true</c>；不存在返回 <c>false</c>。</returns>
    public bool Exists(object key)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try { return _keyToId.ContainsKey(key); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 判断是否存在满足条件的实体。在读锁内拍摄实体快照后逐一检查，
    /// 遇到第一个匹配项即短路返回 <c>true</c>。
    /// <para>
    /// 复杂度 O(n)（最坏情况）。如果仅按主键判断，请使用 <see cref="Exists(object)"/> 重载（O(1)）。
    /// </para>
    /// </summary>
    /// <param name="predicate">条件谓词。</param>
    /// <returns>存在至少一个满足条件的实体返回 <c>true</c>；否则返回 <c>false</c>。</returns>
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

        var prepared = PrepareVectors(entity);

        var id = _nextId++;
        _entities[id] = entity;
        _keyToId[key] = id;

        foreach (var (name, indexVector) in prepared)
            _indices[name].Add(id, indexVector);

        if (logChanges)
            _changeLog.Add((1, key, entity)); // Op=1: Add
    }

    /// <summary>
    /// 收集并校验实体的所有向量字段，返回准备好的索引数据。
    /// 此方法不修改任何状态，校验失败时可安全抛出异常。
    /// <list type="bullet">
    ///   <item>Optional 字段：向量为 <c>null</c> 时跳过，不加入索引</item>
    ///   <item>Required 字段：向量为 <c>null</c> 时抛出 <see cref="ArgumentNullException"/></item>
    ///   <item>PreNormalize 字段：执行 L2 归一化，返回新数组</item>
    ///   <item>其他字段：防御性复制（Clone），防止外部修改数组导致索引损坏</item>
    /// </list>
    /// </summary>
    private List<(string Name, float[] IndexVector)> PrepareVectors(TEntity entity)
    {
        var prepared = new List<(string Name, float[] IndexVector)>(_vectorFields.Count);
        foreach (var (name, field) in _vectorFields)
        {
            var vector = _vectorGetters[name](entity);

            if (vector is null)
            {
                if (!field.Optional)
                    throw new ArgumentNullException(name,
                        $"Vector field '{name}' is required but was null. " +
                        $"Mark [QuiverVector(Optional = true)] to allow null.");
                continue;
            }

            if (vector.Length != field.Dimensions)
                throw new ArgumentException(
                    $"Vector dimension mismatch on '{name}'. Expected {field.Dimensions}, got {vector.Length}");

            prepared.Add(field.PreNormalize
                ? (name, NormalizeToArray(vector))
                : (name, (float[])vector.Clone()));
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

        if (logChanges)
            _changeLog.Add((2, key, null)); // Op=2: Remove

        return true;
    }

    #endregion
}