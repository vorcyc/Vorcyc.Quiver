using System.IO.MemoryMappedFiles;
using Vorcyc.Quiver.Indexing;
using Vorcyc.Quiver.Payloads;
using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
    #region Persistence support

    /// <summary>
    /// Returns a snapshot copy of all entities for use by <see cref="QuiverDbContext.SaveAsync"/> during persistence.
    /// The returned list is a shallow copy; external modifications after the read lock is released do not affect internal data.
    /// <para>
    /// <b>Note</b>: In paged-cache mode this will load every page from disk
    /// and materialize all entities into memory at once. Persistence-time peak memory is unavoidable
    /// because the underlying storage provider requires a full snapshot.
    /// </para>
    /// </summary>
    internal IEnumerable<TEntity> GetAll()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try { return [.. _entities.Values]; }
        finally { _lock.ExitReadLock(); }
    }

    internal ILargeFieldSliceSource? CaptureLargeFieldSliceSnapshot()
    {
        if (_largeFieldStore is null) return null;

        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var snapshot = new LargeFieldSliceSnapshot();
            var rowIndex = 0;
            foreach (var id in _entities.Ids)
            {
                foreach (var fieldName in _largeFields.Keys)
                {
                    if (_largeFieldStore.TryGetSlice(id, fieldName, out var slice))
                        snapshot.Set(fieldName, rowIndex, slice);
                }
                rowIndex++;
            }
            return snapshot;
        }
        finally { _lock.ExitReadLock(); }
    }

    byte[]? Vorcyc.Quiver.Runtime.ILazyLargeFieldSource.GetLargeField(int internalId, string fieldName)
    {
        if (_disposed != 0) return null;
        if (string.IsNullOrEmpty(fieldName)) return null;
        if (_largeFieldStore is null) return null;

        _lock.EnterReadLock();
        try { return _largeFieldStore.Get(internalId, fieldName); }
        finally { _lock.ExitReadLock(); }
    }

    internal bool IsLazyLargeField(string fieldName)
        => _largeFields.TryGetValue(fieldName, out var f)
           && f.MemoryMode is LargeFieldMemoryMode.LazyLoad or LargeFieldMemoryMode.PagedCache;

    internal IEnumerable<string> LargeFieldNames => _largeFields.Keys;

    /// <summary>
    /// Restores entities from persisted data. Calls <see cref="AddCore"/> for each entity to rebuild the index.
    /// Used by <see cref="QuiverDbContext.LoadAsync"/>.
    /// </summary>
    /// <param name="entities">The entity sequence loaded from storage.</param>
    internal void LoadEntities(IEnumerable<TEntity> entities)
        => LoadEntities(entities, snapshotCoveredNextIdByField: null);

    /// <summary>
    /// 带索引快照的加载入口（堆模式）。<paramref name="snapshotCoveredNextIdByField"/> 给出每个向量字段
    /// 已被快照覆盖的 id 上界（不含）：对该上界以下的实体仅向 store 写入向量、不再调用 <c>index.Add(id)</c>，
    /// 避免重复重建图。未在字典中出现的字段一律走全量重建路径。
    /// </summary>
    internal void LoadEntities(
        IEnumerable<TEntity> entities,
        IReadOnlyDictionary<string, int>? snapshotCoveredNextIdByField)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            // 延迟建图：先把全部向量写入 store，并按字段收集待建索引的 id；
            // 加载循环结束后再对每个字段一次性 BuildBulk，使支持并行的索引（HNSW）能多线程加速建图。
            var pendingIndexIds = new Dictionary<string, List<int>>(_vectorStores.Count);

            foreach (var entity in entities)
            {
                // tombstoned slot: still consume an id to keep id == disk row alignment,
                // but don't register the entity / vector / index.
                if (entity is null) { _nextId++; continue; }

                AddCoreDeferIndex(entity, snapshotCoveredNextIdByField, pendingIndexIds);
            }

            // 快照恢复的拓扑可能引用了实际未写入 store 的 id（非正常退出导致快照与数据不一致，
            // 或被快照覆盖的实体已 tombstone）。在增量建图前对账，剔除悬空节点，避免后续
            // Add/Search 解引用缺失向量时抛 KeyNotFoundException。
            if (snapshotCoveredNextIdByField is { Count: > 0 })
            {
                foreach (var field in snapshotCoveredNextIdByField.Keys)
                    if (_indices.TryGetValue(field, out var idx))
                        idx.ReconcileWithStore();
            }

            // 批量建图：对每个字段一次性导入收集到的 id。HnswIndex 会并行构建，其它索引退化为逐个 Add。
            foreach (var (name, ids) in pendingIndexIds)
            {
                if (ids.Count == 0) continue;
                _indices[name].BuildBulk(ids);
            }

            NotifyHeapBytes();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 加载专用的 AddCore 变体：写入实体与全部向量到 store，但<b>不立即</b>调用 <c>index.Add</c>，
    /// 而是把待建索引的 id 收集到 <paramref name="pendingIndexIds"/>，由调用方在加载结束后统一
    /// <see cref="Indexing.IVectorIndex.BuildBulk"/>。当 <paramref name="snapshotCoveredNextIdByField"/>
    /// 指示某字段的 id 已被快照覆盖时，跳过该字段的建图（与原快照跳过逻辑一致）。要求调用方已持有写锁。
    /// </summary>
    private void AddCoreDeferIndex(
        TEntity entity,
        IReadOnlyDictionary<string, int>? snapshotCoveredNextIdByField,
        Dictionary<string, List<int>> pendingIndexIds)
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

            // 该 id 已存在于快照恢复出的索引拓扑里，跳过建图即可。
            if (snapshotCoveredNextIdByField is not null
                && snapshotCoveredNextIdByField.TryGetValue(name, out var coveredNext)
                && id < coveredNext)
            {
                continue;
            }

            if (!pendingIndexIds.TryGetValue(name, out var list))
            {
                list = new List<int>();
                pendingIndexIds[name] = list;
            }
            list.Add(id);
        }
    }

    /// <summary>
    /// <see cref="Vorcyc.Quiver.Runtime.ILazyVectorSource"/> 实现：由 source generator 生成的 lazy 向量属性
    /// getter 通过 <see cref="Vorcyc.Quiver.Runtime.LazyVectorAccessor.Materialize"/> 间接调用。
    /// 对于 <see cref="HeapVectorStore"/> 直接返回 store 内部 float[] 引用（零拷贝，entity backing field
    /// 与 store 共享同一数组，与 InMemory 模式内存占用一致）；不支持直接引用的 store 回退到副本。
    /// 不存在或字段无效时返回 <c>null</c>。
    /// </summary>
    float[]? Vorcyc.Quiver.Runtime.ILazyVectorSource.GetVector(int internalId, string fieldName)
    {
        if (_disposed != 0) return null;
        if (string.IsNullOrEmpty(fieldName)) return null;
        if (!_vectorStores.TryGetValue(fieldName, out var store)) return null;

        _lock.EnterReadLock();
        try
        {
            if (!store.Contains(internalId)) return null;
            // For HeapVectorStore: zero-copy — return the internally owned float[] reference so
            // the entity backing field and the store share the same array (same memory footprint as InMemory).
            // For stores that cannot expose internal references (mmap, fp16): fall back to a copy.
            return store.GetArrayRef(internalId) ?? store.Get(internalId).ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// <see cref="Vorcyc.Quiver.Runtime.ILazyVectorSource.GetVectorHalf"/> 实现：fp16 字段直接返回物理
    /// <c>Half[]</c> 副本（零精度往返）；非 fp16 字段回退到默认的 float→Half narrow。
    /// </summary>
    Half[]? Vorcyc.Quiver.Runtime.ILazyVectorSource.GetVectorHalf(int internalId, string fieldName)
    {
        if (_disposed != 0) return null;
        if (string.IsNullOrEmpty(fieldName)) return null;
        if (!_vectorStores.TryGetValue(fieldName, out var store)) return null;

        _lock.EnterReadLock();
        try
        {
            if (!store.Contains(internalId)) return null;
            if (store.ElementType == Indexing.VectorElementType.Float16)
                return store.GetHalfCopy(internalId);
            // 非 fp16 物理存储：widen 视图再 narrow 回 Half。
            return Vorcyc.Quiver.Numerics.VectorMath.NarrowFloatToHalf(store.Get(internalId));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Returns true if the vector field <paramref name="fieldName"/> is currently backed by an
    /// <see cref="MmapVectorStore"/> in this set. Used by the load path to decide whether a
    /// <c>VectorBlob</c> segment should be materialized to <c>float[]</c> or bound directly to mmap.
    /// </summary>
    internal bool IsMmapField(string fieldName)
        => _vectorStores.TryGetValue(fieldName, out var s)
           && Indexing.VectorStoreSlot.As<MmapVectorStore>(s) is not null;

    /// <summary>
    /// Enumerates this set's vector field names. Used by <see cref="QuiverDbContext"/> to build
    /// the per-(type, field) mmap predicate handed to <see cref="BinaryStorageProvider.LoadAsync"/>.
    /// </summary>
    internal IEnumerable<string> VectorFieldNames => _vectorFields.Keys;

    internal void BindLargeFieldRegions(
        IReadOnlyList<LargeFieldRegion> regions,
        string filePath)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(regions);
        if (_largeFieldStore is null || regions.Count == 0) return;

        var typeFullName = EntityTypeName.Resolve(typeof(TEntity));
        var idsInOrder = new List<int>(_entities.Count);
        foreach (var id in _entities.Ids)
            idsInOrder.Add(id);

        _lock.EnterWriteLock();
        try
        {
            foreach (var region in regions.OrderBy(r => r.EntityChunkStart))
            {
                if (region.TypeName != typeFullName) continue;
                if (!IsLazyLargeField(region.FieldName)) continue;

                var rowIds = new int[region.RowCount];
                Array.Fill(rowIds, -1);
                int take = Math.Min(region.EntityChunkCount, idsInOrder.Count - region.EntityChunkStart);
                for (int i = 0; i < take; i++)
                    rowIds[i] = idsInOrder[region.EntityChunkStart + i];
                _largeFieldStore.Bind(region.FieldName, filePath, region, rowIds);
            }

            foreach (var id in idsInOrder)
                if (_entities.TryGetValue(id, out var ent) && ent is not null)
                    Vorcyc.Quiver.Runtime.LazyLargeFieldAccessor.Bind(ent!, this, id);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Drains and returns the internal-row ids that have been tombstoned since the last call.
    /// Called by <see cref="QuiverDbContext.AppendAsync"/> to emit a <c>SegmentKind.Tombstone</c>
    /// segment per type. <see cref="QuiverDbContext.SaveAsync"/> can ignore
    /// the returned ids because they fully rewrite the file from the live entity snapshot.
    /// </summary>
    internal int[] DrainPendingTombstones()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            if (_pendingTombstones.Count == 0) return Array.Empty<int>();
            var arr = _pendingTombstones.ToArray();
            _pendingTombstones.Clear();
            return arr;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Clears the pending tombstone list without writing them; called after a full rewrite.</summary>
    internal void ClearPendingTombstones()
    {
        if (_disposed != 0) return;
        _lock.EnterWriteLock();
        try { _pendingTombstones.Clear(); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Returns the current count of pending tombstones; used by auto-merge heuristics.</summary>
    internal int PendingTombstoneCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _pendingTombstones.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// 枚举本 set 中支持快照的向量字段：每项为 <c>(FieldName, NodeCount, WriteCallback)</c>。
    /// 写盘路径在写完该类型的常规段后逐个调用 <c>WriteCallback</c> 来产出 <see cref="SegmentKind.IndexSnapshot"/> 段。
    /// <para>调用方必须持有 <see cref="QuiverDbContext"/> 级别的协调权（即此调用与 <c>GetAll()</c>/Save 序列在同一时刻）。</para>
    /// </summary>
    internal IReadOnlyList<(string FieldName, int NodeCount, Func<System.IO.BinaryWriter, bool> Writer)> EnumerateIndexSnapshotWriters()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var list = new List<(string, int, Func<System.IO.BinaryWriter, bool>)>(_indices.Count);
            foreach (var (name, index) in _indices)
            {
                // 默认接口实现返回 false；仅 HNSW 等支持快照的索引会真正写出 payload。
                // 这里直接捕获 index 引用，writer 内部再调用一次 TrySaveSnapshot 即可。
                var captured = index;
                list.Add((name, captured.Count, bw => captured.TrySaveSnapshot(bw)));
            }
            return list;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 在 <see cref="LoadEntities"/> / <see cref="LoadEntitiesMmap"/> 之前调用：尝试用 <paramref name="snapshots"/>
    /// 恢复每个向量字段的索引拓扑。返回每字段“快照已覆盖”的内部 id 上界（不含），调用方在重放实体时
    /// 对 <c>id &lt; coveredNext</c> 的实体跳过 <c>IVectorIndex.Add</c>；其余实体仍走 Add 路径以保留增量正确性。
    /// 字段不存在 / 快照不兼容 / 解码失败时该字段不会出现在返回字典中。
    /// </summary>
    internal IReadOnlyDictionary<string, int> ApplyIndexSnapshots(
        IReadOnlyDictionary<string, byte[]> snapshots)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) return new Dictionary<string, int>(0);

        var result = new Dictionary<string, int>(snapshots.Count, StringComparer.Ordinal);
        _lock.EnterWriteLock();
        try
        {
            foreach (var (field, bytes) in snapshots)
            {
                if (!_indices.TryGetValue(field, out var index)) continue;
                if (bytes is null || bytes.Length == 0) continue;
                using var ms = new System.IO.MemoryStream(bytes, writable: false);
                using var br = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
                if (index.TryLoadSnapshot(br, out var coveredNext) && coveredNext > 0)
                    result[field] = coveredNext;
            }
        }
        finally { _lock.ExitWriteLock(); }
        return result;
    }

    /// <summary>
    /// mmap 模式的加载入口。与 <see cref="LoadEntities(IEnumerable{TEntity})"/> 不同：
    /// 对在 <paramref name="mmapRegions"/> 中出现的 <c>(fieldName, region)</c>，
    /// 实体上的对应向量属性预期为 <c>null</c>（BinaryStorageProvider 已跳过物化）。
    /// 本方法会：
    /// <list type="bullet">
    ///   <item>为每个实体分配 internal id 并写入 <c>_entities</c> / <c>_keyToId</c>；</item>
    ///   <item>对 mmap 字段：把 <c>id</c> 加入对应索引，写入 region 的 rowIds，并把实体绑定到
    ///     <see cref="Vorcyc.Quiver.Runtime.LazyVectorAccessor"/>，使其声明的 lazy 属性
    ///     可以按需从 mmap 拉取向量；</item>
    ///   <item>对非 mmap 字段：走与 <see cref="AddCore"/> 等价的写入路径，要求向量已物化。</item>
    /// </list>
    /// 所有 region 的 mmap 视图最终由 <paramref name="bindAction"/> 在写入完成后通过
    /// <see cref="MmapVectorStore.BindRegion"/> 注册到对应 store。
    /// </summary>
    internal void LoadEntitiesMmap(
        IReadOnlyList<TEntity> entities,
        IReadOnlyDictionary<string, IReadOnlyList<MmapVectorRegion>> mmapRegions,
        Action<IReadOnlyDictionary<string, IReadOnlyList<(MmapVectorRegion Region, int[] RowIds)>>> bindAction)
        => LoadEntitiesMmap(entities, mmapRegions, bindAction, snapshotCoveredNextIdByField: null);

    /// <summary>
    /// 带索引快照的 mmap 加载入口：与 <see cref="LoadEntitiesMmap(IReadOnlyList{TEntity}, IReadOnlyDictionary{string, IReadOnlyList{MmapVectorRegion}}, Action{IReadOnlyDictionary{string, IReadOnlyList{ValueTuple{MmapVectorRegion, int[]}}}})"/>
    /// 行为一致，但对 <paramref name="snapshotCoveredNextIdByField"/> 中已被快照覆盖的字段，在 BindRegion 完成后
    /// 仅对 <c>id &gt;= coveredNext</c> 的实体调用 <c>index.Add</c>，避免重建已经从快照恢复出来的图拓扑。
    /// </summary>
    internal void LoadEntitiesMmap(
        IReadOnlyList<TEntity> entities,
        IReadOnlyDictionary<string, IReadOnlyList<MmapVectorRegion>> mmapRegions,
        Action<IReadOnlyDictionary<string, IReadOnlyList<(MmapVectorRegion Region, int[] RowIds)>>> bindAction,
        IReadOnlyDictionary<string, int>? snapshotCoveredNextIdByField)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(mmapRegions);
        ArgumentNullException.ThrowIfNull(bindAction);

        // 同一 (type, field) 在多次 Append 后可能对应多个 VectorBlob region。按 EntityChunkStart 排序，
        // 并为每个 region 单独分配 rowIds（长度 = region.RowCount，初值 -1 表示空槽位）。
        var sortedRegions = new Dictionary<string, List<MmapVectorRegion>>(mmapRegions.Count, StringComparer.Ordinal);
        var rowIdsByFieldByRegion = new Dictionary<string, int[][]>(mmapRegions.Count, StringComparer.Ordinal);
        var cursorByField = new Dictionary<string, int>(mmapRegions.Count, StringComparer.Ordinal);
        foreach (var (field, list) in mmapRegions)
        {
            var sorted = list.OrderBy(r => r.EntityChunkStart).ToList();
            sortedRegions[field] = sorted;
            var arr = new int[sorted.Count][];
            for (int k = 0; k < sorted.Count; k++)
            {
                arr[k] = new int[sorted[k].RowCount];
                Array.Fill(arr[k], -1);
            }
            rowIdsByFieldByRegion[field] = arr;
            cursorByField[field] = 0;
        }

        // 延迟到 BindRegion 之后再向索引登记 mmap 字段的 id：
        // 否则 IVectorIndex.Add(id) 内部立即 _vectorStore.Get(id)（HNSW 在构图时即解引用），
        // 但此时 mmap region 尚未通过 bindAction 注册，会抛 "Vector id N not found in mmap store."。
        var pendingMmapIndexAdds = new Dictionary<string, List<int>>(sortedRegions.Count, StringComparer.Ordinal);
        foreach (var name in sortedRegions.Keys)
            pendingMmapIndexAdds[name] = [];

        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity is null)
                {
                    // tombstoned slot: still consume an id so internal id == disk row,
                    // ensuring future tombstone segments can target it precisely.
                    _nextId++;
                    continue;
                }
                var key = _getKey(entity)
                    ?? throw new InvalidOperationException("Key property value cannot be null.");
                if (_keyToId.ContainsKey(key))
                    throw new InvalidOperationException($"Duplicate key '{key}' loaded from storage.");

                var id = _nextId++;
                _entities.Set(id, entity);
                _keyToId[key] = id;

                bool boundForVectorAccessor = false;
                foreach (var (name, field) in _vectorFields)
                {
                    if (sortedRegions.TryGetValue(name, out var regionList))
                    {
                        // 推进游标到包含 entity index i 的 region。
                        int cur = cursorByField[name];
                        while (cur < regionList.Count
                               && i >= regionList[cur].EntityChunkStart + regionList[cur].EntityChunkCount)
                            cur++;
                        cursorByField[name] = cur;

                        if (cur < regionList.Count)
                        {
                            var region = regionList[cur];
                            if (i >= region.EntityChunkStart && i < region.EntityChunkStart + region.EntityChunkCount)
                            {
                                int localRow = i - region.EntityChunkStart;
                                // 若 region 标记本行为 null 槽位（Nullable 向量未提供），不参与索引/store 绑定，
                                // 否则零向量会污染相似度搜索（Cosine 上是 NaN）。与堆模式 PrepareVectors 行为对齐。
                                bool isNull = region.NullBitmap is { } nb && ((nb[localRow >> 3] >> (localRow & 7)) & 1) != 0;
                                if (isNull)
                                {
                                    if (!field.Nullable)
                                        throw new InvalidOperationException(
                                            $"Vector field '{name}' is required but was null at row {localRow} during mmap load.");
                                }
                                else
                                {
                                    rowIdsByFieldByRegion[name][cur][localRow] = id;
                                    pendingMmapIndexAdds[name].Add(id);
                                }
                            }
                            else
                            {
                                // 该实体不在任何 region 范围内：当作 Nullable 缺失处理（堆模式同义）。
                                if (!field.Nullable)
                                    throw new InvalidOperationException(
                                        $"Vector field '{name}' is required but no mmap region covers entity index {i}.");
                            }
                        }
                        else
                        {
                            if (!field.Nullable)
                                throw new InvalidOperationException(
                                    $"Vector field '{name}' is required but no mmap region covers entity index {i}.");
                        }

                        // 任一非 InMemory 字段都触发一次 LazyVectorAccessor 绑定（绑定本身按 entity 维度，与字段无关）。
                        if (field.MemoryMode != VectorMemoryMode.InMemory && !boundForVectorAccessor)
                        {
                            Vorcyc.Quiver.Runtime.LazyVectorAccessor.Bind(entity!, this, id);
                            boundForVectorAccessor = true;
                        }
                    }
                    else
                    {
                        // 非 mmap 字段：要求向量已物化（与 AddCore 等价）。
                        var v = _vectorGetters[name](entity);
                        if (v is null || v.Length == 0)
                        {
                            if (!field.Nullable)
                                throw new InvalidOperationException(
                                    $"Vector field '{name}' is required but was null/empty during load.");
                            continue;
                        }
                        if (v.Length != field.Dimensions)
                            throw new InvalidOperationException(
                                $"Vector dimension mismatch on '{name}' during load. " +
                                $"Expected {field.Dimensions}, got {v.Length}.");
                        if (field.PreNormalize)
                        {
                            if (v is Half[] hv)
                                NormalizeHalfInPlace(hv);
                            else
                                NormalizeVector((float[])v, (float[])v);
                        }
                        StoreVector(name, id, v);
                        _indices[name].Add(id);
                    }
                }
            }

            // 把所有 (field → [(region, rowIds), ...]) 通过回调交还 QuiverDbContext，由其打开 mmap 并 BindRegion。
            var payload = new Dictionary<string, IReadOnlyList<(MmapVectorRegion, int[])>>(sortedRegions.Count, StringComparer.Ordinal);
            foreach (var (name, regionList) in sortedRegions)
            {
                var arrs = rowIdsByFieldByRegion[name];
                var combined = new List<(MmapVectorRegion, int[])>(regionList.Count);
                for (int k = 0; k < regionList.Count; k++)
                    combined.Add((regionList[k], arrs[k]));
                payload[name] = combined;
            }
            bindAction(payload);

            // BindRegion 之后再把 mmap 字段的 id 喂给索引：此时 _vectorStore.Get(id) 可正常返回。
            // 对已被快照覆盖的字段，仅添加 id >= coveredNext 的实体，保留快照恢复出的拓扑。
            // 使用 BuildBulk 一次性批量建图，使支持并行的索引（HNSW）能多线程加速重建。
            foreach (var (name, ids) in pendingMmapIndexAdds)
            {
                var index = _indices[name];
                int coveredNext = 0;
                bool hasSnapshot = snapshotCoveredNextIdByField is not null
                    && snapshotCoveredNextIdByField.TryGetValue(name, out coveredNext);
                if (!hasSnapshot)
                {
                    index.BuildBulk(ids);
                }
                else
                {
                    // 快照恢复的拓扑可能引用了实际未绑定到 mmap store 的 id（非正常退出导致快照与
                    // VectorBlob 行数不一致，或被覆盖的实体已 tombstone / 向量为 null）。在增量建图前
                    // 对账，剔除悬空节点，避免后续 Add/Search 解引用缺失向量时抛
                    // "Vector id N not found in mmap store."。
                    index.ReconcileWithStore();

                    var toAdd = new List<int>(ids.Count);
                    foreach (var id in ids)
                        if (id >= coveredNext) toAdd.Add(id);
                    index.BuildBulk(toAdd);
                }
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// SaveAsync 在 mmap 模式下需要在写盘前释放所有 mmap 视图，写完再重新绑定。
    /// 该方法清空当前 store 的 mmap 区域和 overflow，确保文件不再被句柄持有。
    /// 调用方持有 <see cref="QuiverDbContext"/> 级别的协调权，无需再加锁。
    /// </summary>
    internal void DisposeMmapStoresForSave()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            foreach (var (_, store) in _vectorStores)
                if (Indexing.VectorStoreSlot.As<MmapVectorStore>(store) is { } m)
                {
                    // 释放映射但保留 _idToRow/overflow 由调用方在 rebind 阶段处理：
                    // 这里直接 Clear() 来释放 view，并清空内部状态——SaveAsync 写完会重建 mmap 绑定并重新登记 id。
                    m.Clear();
                }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// 在写盘后调用：用新的 mmap region 重新绑定本 set 的 mmap stores，并把绑定后的实体重新接入
    /// <see cref="Vorcyc.Quiver.Runtime.LazyVectorAccessor"/>。<paramref name="openMmap"/> 用于按需打开
    /// <see cref="MemoryMappedFile"/>（同一份文件可被多个 store 共享）。
    /// </summary>
    internal void RebindMmapStoresAfterSave(
        IReadOnlyList<MmapVectorRegion> regions,
        Func<MemoryMappedFile> openMmap)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(openMmap);

        var typeFullName = EntityTypeName.Resolve(typeof(TEntity));

        // 收集本 set 名义上属于 mmap 的字段。
        var mmapFieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, store) in _vectorStores)
            if (Indexing.VectorStoreSlot.As<MmapVectorStore>(store) is not null) mmapFieldNames.Add(name);
        if (mmapFieldNames.Count == 0) return;

        _lock.EnterWriteLock();
        try
        {
            // SaveAsync 通过 GetAll() → _entities.Values 写盘；Dictionary<int,TEntity> 的枚举顺序
            // 等于插入顺序，而 Upsert (= Remove + Add) 会把同一个 key 的实体重新插到尾部。
            // 因此**不能**用 _keyToId.Values 升序近似还原 row→id 映射——必须严格按 _entities 的枚举顺序。
            var idsInOrder = new List<int>(_entities.Count);
            foreach (var id in _entities.Ids)
                idsInOrder.Add(id);

            var perField = new Dictionary<string, List<MmapVectorRegion>>(StringComparer.Ordinal);
            foreach (var r in regions)
            {
                if (r.TypeName != typeFullName) continue;
                if (!mmapFieldNames.Contains(r.FieldName)) continue;
                if (!perField.TryGetValue(r.FieldName, out var list))
                    perField[r.FieldName] = list = new List<MmapVectorRegion>();
                list.Add(r);
            }
            if (perField.Count == 0) return;

            foreach (var (fieldName, list) in perField)
            {
                var store = Indexing.VectorStoreSlot.As<MmapVectorStore>(_vectorStores[fieldName])!;
                foreach (var region in list.OrderBy(r => r.EntityChunkStart))
                {
                    var rowIds = new int[region.RowCount];
                    Array.Fill(rowIds, -1);
                    int take = Math.Min(region.EntityChunkCount, idsInOrder.Count - region.EntityChunkStart);
                    for (int i = 0; i < take; i++)
                        rowIds[i] = idsInOrder[region.EntityChunkStart + i];

                    var mmf = openMmap();
                    store.BindRegion(mmf, region.PayloadOffset, region.RowCount, rowIds,
                        region.Encoding, region.StorageDim > 0 ? region.StorageDim : region.Dim, region.Sq8Scales);
                }
            }

            // 重新把所有实体绑定到 LazyVectorAccessor，使非 InMemory 向量属性继续工作。
            bool anyVectorAccessor = false;
            foreach (var (_, f) in _vectorFields) if (f.MemoryMode != VectorMemoryMode.InMemory) { anyVectorAccessor = true; break; }
            if (anyVectorAccessor)
            {
                foreach (var id in idsInOrder)
                    if (_entities.TryGetValue(id, out var ent) && ent is not null)
                        Vorcyc.Quiver.Runtime.LazyVectorAccessor.Bind(ent!, this, id);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    #endregion
}