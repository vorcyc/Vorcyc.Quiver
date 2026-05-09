namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 统一负载管线内部使用的内存模式。
/// </summary>
internal enum PayloadMemoryMode : byte
{
    /// <summary>负载直接保存在托管内存中。</summary>
    InMemory = 0,

    /// <summary>负载在首次访问时按需物化。</summary>
    LazyLoad = 1,

    /// <summary>负载由内存映射文件区域提供。</summary>
    MemoryMapped = 2,

    /// <summary>负载按需读取，并使用有界缓存保存最近访问的内容。</summary>
    PagedCache = 3,
}
