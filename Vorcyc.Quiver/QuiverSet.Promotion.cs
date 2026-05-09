using System.Reflection;
using Vorcyc.Quiver.Indexing;
using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver;

public partial class QuiverSet<TEntity>
{
    /// <summary>
    /// <see cref="QuiverDbContext"/> 在 <c>InitializeSets</c> 之后调用，注入升级协调器。
    /// 不在构造期注入，避免 set 与 context 的相互依赖循环以及构造顺序约束。
    /// </summary>
    internal void AttachPromotionCoordinator(IPromotionCoordinator coordinator)
    {
        _promotionCoordinator = coordinator;
    }

    /// <summary>
    /// 估算本 set 当前所有 vector store 的堆字节合计。<see cref="MmapVectorStore"/> 仅计 overflow，
    /// 已 mmap 的区域不计入；因此该值近似等于"还在托管堆上的向量负载体积"。
    /// 必须在已持有 set 锁（读或写）时调用，避免在迭代过程中字典被替换。
    /// </summary>
    internal long EstimateHeapVectorBytes()
    {
        if (_disposed != 0) return 0;
        long total = 0;
        foreach (var store in _vectorStores.Values)
            total += store.HeapByteSize;
        return total;
    }

    /// <summary>
    /// 写路径在写锁内调用：把当前堆向量字节合计上报给 <see cref="IPromotionCoordinator"/>。
    /// 没有协调器或 set 已释放时静默返回。
    /// </summary>
    private void NotifyHeapBytes()
    {
        var coord = _promotionCoordinator;
        if (coord is null || _disposed != 0) return;
        long bytes = 0;
        foreach (var store in _vectorStores.Values)
            bytes += store.HeapByteSize;
        coord.NotifyHeapBytesChanged(typeof(TEntity), bytes);
    }

    /// <summary>
    /// 在写锁内把指定向量字段从 <see cref="HeapVectorStore"/> 热替换为已绑定 mmap 区域的
    /// <see cref="MmapVectorStore"/>。索引/store 引用不变（由 <see cref="VectorStoreSlot"/> 包装），
    /// 因此索引拓扑无需重建。
    /// <para>
    /// 调用约定：<paramref name="bind"/> 接收一个新的空 <see cref="MmapVectorStore"/>，
    /// 负责调用 <c>BindRegion</c> 把目标文件的 mmap 区域注册进去；返回前必须保证
    /// 当前 set 内所有 row id 都已被绑定，否则后续搜索会丢点。
    /// </para>
    /// </summary>
    /// <returns>实际成功提升的字段数量。</returns>
    internal int PromoteFieldsToMmap(IReadOnlyDictionary<string, Action<MmapVectorStore>> bind)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(bind);
        if (bind.Count == 0) return 0;

        _lock.EnterWriteLock();
        try
        {
            int promoted = 0;
            foreach (var (fieldName, binder) in bind)
            {
                if (!_vectorStores.TryGetValue(fieldName, out var slotStore)) continue;
                if (slotStore is not VectorStoreSlot slot) continue;
                if (slot.Inner is MmapVectorStore) continue; // already promoted

                if (!_vectorFields.TryGetValue(fieldName, out var info)) continue;
                var newStore = new MmapVectorStore(info.EffectiveDimensions);
                binder(newStore);

                // 旧的 heap store 在替换后即可释放：所有 id 现在都由新的 mmap store 解析。
                var old = slot.Inner;
                slot.Replace(newStore);
                old.Dispose();
                promoted++;
            }

            if (promoted > 0)
            {
                // 升级完成后所有 lazy 实体仍绑定到本 set，GetVector 会自动走新的 mmap 路径，无需 rebind。
            }
            return promoted;
        }
        finally { _lock.ExitWriteLock(); }
    }
}
