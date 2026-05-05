namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
    #region Persistence support

    // ── Change tracking (WAL incremental persistence support) ──

    /// <summary>
    /// Change log buffer. Records all write operations since the last <see cref="DrainChanges"/> call.
    /// <para>
    /// Tuple meaning:
    /// <list type="bullet">
    ///   <item><c>Op</c>: Operation type (1=Add, 2=Remove, 3=Clear)</item>
    ///   <item><c>Key</c>: Entity primary key (null for Clear)</item>
    ///   <item><c>Entity</c>: Entity instance (non-null only for Add)</item>
    /// </list>
    /// </para>
    /// <para>
    /// Accessed only under the write lock; no additional synchronization is needed. Load (<see cref="LoadEntities"/>)
    /// and replay (<see cref="ReplayAdd"/>/<see cref="ReplayRemove"/>/<see cref="ReplayClear"/>)
    /// do not record changes to avoid circular writes.
    /// </para>
    /// </summary>
    private readonly List<(byte Op, object? Key, object? Entity)> _changeLog = [];

    /// <summary>Whether there are unpersisted changes. Protected by the read lock.</summary>
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
    /// Gets and clears the change log (snapshot-and-drain semantics).
    /// <para>
    /// Called by <see cref="QuiverDbContext.SaveChangesAsync"/> to convert changes into WAL records for persistence.
    /// </para>
    /// </summary>
    /// <returns>All change records since the last call. Returns an empty list when there are no changes.</returns>
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

    // ── WAL replay methods ──
    // Changes are not recorded during replay (logChanges: false) to avoid circular writes.

    /// <summary>
    /// Replays a WAL Add operation. Does not trigger change log recording.
    /// <para>Silently skips on primary key conflict (WAL may contain records already present in the snapshot).</para>
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

    /// <summary>Replays a WAL Remove operation. Does not trigger change log recording.</summary>
    internal void ReplayRemove(object key)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { RemoveCore(key, logChanges: false); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Replays a WAL Clear operation. Does not trigger change log recording.</summary>
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
    /// Returns a snapshot copy of all entities for use by <see cref="QuiverDbContext.SaveAsync"/> during persistence.
    /// The returned list is a shallow copy; external modifications after the read lock is released do not affect internal data.
    /// </summary>
    internal IEnumerable<TEntity> GetAll()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try { return [.. _entities.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Restores entities from persisted data. Calls <see cref="AddCore"/> for each entity to rebuild the index.
    /// Used by <see cref="QuiverDbContext.LoadAsync"/>. Does not record change log entries.
    /// </summary>
    /// <param name="entities">The entity sequence loaded from storage.</param>
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