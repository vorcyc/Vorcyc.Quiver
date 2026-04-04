namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
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
            foreach (var store in _vectorStores.Values)
                store.Clear();
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
}