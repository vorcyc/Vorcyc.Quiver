namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 统一负载管线支持的负载类型。
/// </summary>
internal enum PayloadKind : byte
{
    /// <summary>向量负载（<c>float[]</c>）。</summary>
    Vector = 1,

    /// <summary>大字段负载（<c>byte[]</c>）。</summary>
    LargeField = 2,
}
