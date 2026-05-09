namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 描述旧数据库文件中一个可复用的大字段字节切片。
/// </summary>
/// <param name="FilePath">切片所在文件路径。</param>
/// <param name="Offset">切片在文件中的起始偏移。</param>
/// <param name="Length">切片字节长度。</param>
/// <param name="IsNull">该行是否表示逻辑 null。</param>
internal readonly record struct LargeFieldSlice(
    string FilePath,
    long Offset,
    int Length,
    bool IsNull)
{
    /// <summary>
    /// 将该切片的字节范围复制到目标流；null 或空切片不会写入任何内容。
    /// </summary>
    /// <param name="destination">接收切片内容的目标流。</param>
    public void CopyTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (IsNull || Length == 0) return;

        using var source = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        source.Position = Offset;
        source.CopyRangeTo(destination, Length);
    }
}

/// <summary>
/// 面向大字段切片复用的流复制辅助方法。
/// </summary>
internal static class LargeFieldSliceStreamExtensions
{
    /// <summary>
    /// 从当前流位置开始精确复制指定长度的字节到目标流。
    /// </summary>
    /// <param name="source">源流。</param>
    /// <param name="destination">目标流。</param>
    /// <param name="length">需要复制的字节数。</param>
    public static void CopyRangeTo(this Stream source, Stream destination, int length)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(65536, Math.Max(1, length)));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0) throw new EndOfStreamException();
                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
