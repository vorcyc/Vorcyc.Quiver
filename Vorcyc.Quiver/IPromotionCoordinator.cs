namespace Vorcyc.Quiver;

/// <summary>
/// QuiverSet → QuiverDbContext 反向回调接口。仅暴露 set 在写路径上需要触发的协调动作，
/// 避免 set 直接持有 <see cref="QuiverDbContext"/> 强引用导致的循环依赖。
/// </summary>
internal interface IPromotionCoordinator
{
    /// <summary>
/// Set 在写操作累计字节超过 <see cref="QuiverVectorOptions.MaxInMemoryBytes"/> 时回调；
    /// 由 context 用单飞 CAS 决定是否在后台执行 Heap → Mmap 升级。
    /// 实现必须是非阻塞的（典型做法：CAS 后 <see cref="Task.Run(Action)"/>）。
    /// </summary>
    /// <param name="entityType">触发升级评估的实体类型，对应 <c>QuiverSet&lt;TEntity&gt;</c> 的 <c>TEntity</c>。</param>
    /// <param name="currentHeapBytes">该 set 当前快照下所有 vector store 的堆字节合计。</param>
    void NotifyHeapBytesChanged(Type entityType, long currentHeapBytes);
}
