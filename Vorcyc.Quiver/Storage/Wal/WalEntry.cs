namespace Vorcyc.Quiver.Storage.Wal;

// ══════════════════════════════════════════════════════════════════
// WAL（Write-Ahead Log）条目定义
// 每条记录描述一次写操作，用于增量持久化和崩溃恢复。
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// WAL 操作类型。每种操作在二进制日志中占 1 字节。
/// </summary>
internal enum WalOperation : byte
{
    /// <summary>新增实体。载荷为实体的 JSON 序列化。</summary>
    Add = 1,

    /// <summary>删除实体。载荷为主键的 JSON 序列化。</summary>
    Remove = 2,

    /// <summary>清空整个向量集合。无载荷。</summary>
    Clear = 3
}

/// <summary>
/// 一条 WAL 变更记录，包含操作类型、目标集合类型名和 JSON 载荷。
/// <para>
/// 设计为不可变记录类型，在内存中作为变更缓冲，最终序列化为二进制写入 WAL 文件。
/// </para>
/// </summary>
/// <param name="Operation">操作类型。</param>
/// <param name="TypeName">实体类型全名（如 <c>"MyApp.Document"</c>），用于回放时定位目标集合。</param>
/// <param name="PayloadJson">
/// JSON 格式载荷：
/// <list type="bullet">
///   <item><see cref="WalOperation.Add"/>：实体的完整 JSON</item>
///   <item><see cref="WalOperation.Remove"/>：主键值的 JSON</item>
///   <item><see cref="WalOperation.Clear"/>：空字符串</item>
/// </list>
/// </param>
internal sealed record WalEntry(
    WalOperation Operation,
    string TypeName,
    string PayloadJson);