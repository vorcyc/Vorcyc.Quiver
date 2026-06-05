namespace Vorcyc.Quiver;

using System.IO.MemoryMappedFiles;
using System.Reflection;
using Vorcyc.Quiver.Indexing;
using Vorcyc.Quiver.Storage;

public abstract partial class QuiverDbContext
{
    /// <summary>
    /// <see cref="IPromotionCoordinator"/> 实现：set 在写路径越限时调用。
    /// <para>
    /// 仅当满足以下全部条件时排队一次后台升级：
    /// <list type="number">
    ///   <item><see cref="QuiverVectorOptions.AutoPromoteToMemoryMapped"/> 为 <c>true</c>；</item>
    ///   <item><see cref="QuiverVectorOptions.MaxInMemoryBytes"/> 大于 0 且 <paramref name="currentHeapBytes"/> 越限；</item>
    ///   <item><see cref="QuiverDbOptions.DatabasePath"/> 已配置（mmap 必须以文件为支撑）；</item>
    ///   <item>该实体类型尚无飞行中的升级任务（单飞 CAS）。</item>
    /// </list>
    /// </para>
    /// </summary>
    void IPromotionCoordinator.NotifyHeapBytesChanged(Type entityType, long currentHeapBytes)
    {
        if (_disposed != 0) return;
        if (!_options.Vectors.AutoPromoteToMemoryMapped) return;
        if (_options.Vectors.MaxInMemoryBytes <= 0) return;
        if (currentHeapBytes < _options.Vectors.MaxInMemoryBytes) return;
        if (string.IsNullOrEmpty(_options.DatabasePath)) return;
        if (!_sets.TryGetValue(entityType, out var set)) return;

        // 单飞门：同一 entityType 同时只允许一个升级任务在排队/执行。
        lock (_promotionLock)
        {
            _promotionInFlight.TryGetValue(entityType, out var state);
            if (state != 0) return;
            _promotionInFlight[entityType] = 1;
        }

        // 把 I/O 密集的升级动作甩到线程池，绝不阻塞 set 写路径。
        _ = Task.Run(() => RunPromotionAsync(entityType, set));
    }

    private async Task RunPromotionAsync(Type entityType, object set)
    {
        try
        {
            await PromoteSetToMmapAsync(entityType, set).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[Vorcyc.Quiver] Heap → Mmap promotion for '{0}' failed: {1}", entityType.FullName, ex);
        }
        finally
        {
            lock (_promotionLock) _promotionInFlight[entityType] = 0;
        }
    }

    /// <summary>
    /// 实际执行升级：把指定 set 的所有非 mmap 向量字段 flush 到磁盘文件，
    /// 然后用新文件的 mmap 区域 hot-swap 这些字段的内层 store。
    /// 由于 <see cref="VectorStoreSlot"/> 提供稳定引用，索引拓扑无需重建。
    /// </summary>
    private async Task PromoteSetToMmapAsync(Type entityType, object set)
    {
        var filePath = _options.DatabasePath!;

        // 1) 整库 SaveAsync 已经覆盖了"flush 到磁盘 + 重新打开 mmap 区域"。这里直接复用，
        //    简化实现且严格保证文件内容与内存快照一致；后续可针对单 set 优化。
        await SaveAsync(filePath).ConfigureAwait(false);

        // 2) SaveAsync 之后，如果全局 Vectors.MemoryMode 不是 InMemory，本 set 的 mmap 字段已被 rebind；
        //    InMemory 下 SaveAsync 没有 rebind 步骤，需要这里手动把 Heap → Mmap 切换。
        if (_options.Vectors.MemoryMode != GlobalVectorMemoryMode.InMemory)
            return; // SaveAsync 已经处理过

        var regions = BinaryStorageProvider.ReadVectorBlobRegions(filePath);
        if (regions.Count == 0) return;

        var typeFullName = EntityTypeName.Resolve(entityType);
        // 取出本 set 当前的 store 表 + 实体顺序，构造 fieldName → 绑定回调。
        var vectorStoresField = set.GetType()
            .GetField("_vectorStores", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stores = (System.Collections.IDictionary)vectorStoresField.GetValue(set)!;

        // 仅升级当前仍为 Heap 的字段（不包括 mmap 字段，也不包括其他类型）。
        var heapFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in stores)
        {
            var name = (string)kv.Key;
            var slot = kv.Value as VectorStoreSlot;
            if (slot is null) continue;
            if (slot.Inner is HeapVectorStore) heapFields.Add(name);
        }
        if (heapFields.Count == 0) return;

        // 依据 SaveAsync 写盘后的实体顺序构造 row id 映射（与 RebindMmapStoresAfterSave 等价的逻辑）。
        var entitiesField = set.GetType()
            .GetField("_entities", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pageCache = entitiesField.GetValue(set)!;
        var idsProp = pageCache.GetType().GetProperty("Ids", BindingFlags.Instance | BindingFlags.Public)!;
        var idsEnum = (System.Collections.IEnumerable)idsProp.GetValue(pageCache)!;
        var idsInOrder = new List<int>();
        foreach (int id in idsEnum) idsInOrder.Add(id);

        var perField = new Dictionary<string, List<MmapVectorRegion>>(StringComparer.Ordinal);
        foreach (var r in regions)
        {
            if (r.TypeName != typeFullName) continue;
            if (!heapFields.Contains(r.FieldName)) continue;
            if (!perField.TryGetValue(r.FieldName, out var list))
                perField[r.FieldName] = list = new List<MmapVectorRegion>();
            list.Add(r);
        }
        if (perField.Count == 0) return;

        var binders = new Dictionary<string, Action<MmapVectorStore>>(StringComparer.Ordinal);
        foreach (var (fieldName, list) in perField)
        {
            var sortedList = list.OrderBy(r => r.EntityChunkStart).ToList();
            binders[fieldName] = newStore =>
            {
                foreach (var region in sortedList)
                {
                    var rowIds = new int[region.RowCount];
                    Array.Fill(rowIds, -1);
                    int take = Math.Min(region.EntityChunkCount, idsInOrder.Count - region.EntityChunkStart);
                    for (int i = 0; i < take; i++)
                        rowIds[i] = idsInOrder[region.EntityChunkStart + i];

                    var mmf = MemoryMappedFile.CreateFromFile(
                        filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
                    newStore.BindRegion(mmf, region.PayloadOffset, region.RowCount, rowIds,
                        region.Encoding, region.StorageDim > 0 ? region.StorageDim : region.Dim, region.Sq8Scales);
                }
            };
        }

        var promoteM = set.GetType()
            .GetMethod("PromoteFieldsToMmap", BindingFlags.Instance | BindingFlags.NonPublic)!;
        promoteM.Invoke(set, [binders]);
    }
}
