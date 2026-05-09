namespace Niubi.Shared.TagIO;

/// <summary>
/// MP3 标签的完整数据快照，包含基本字段、封面和歌词。
/// 作为 <see cref="AudioTagIO"/> 读写操作的统一载体，避免多次 IO。
/// </summary>
/// <param name="Title">曲目标题。</param>
/// <param name="Artist">艺人名。</param>
/// <param name="Album">专辑名。</param>
/// <param name="Year">发行年份。</param>
/// <param name="Genre">流派。</param>
/// <param name="Track">曲目序号。</param>
/// <param name="Comment">备注。</param>
/// <param name="Cover">封面图片字节（JPEG）；无封面为 <see langword="null"/>。</param>
/// <param name="UsltLyrics">USLT 原始 LRC 文本（含时间戳）；无歌词为 <see langword="null"/>。</param>
/// <param name="SyltLyrics">SYLT 同步歌词条目列表；无 SYLT 帧为空列表。</param>
public sealed record Mp3TagData(
    string?  Title,
    string?  Artist,
    string?  Album,
    string?  Year,
    string?  Genre,
    uint     Track,
    string?  Comment,
    byte[]?  Cover,
    string?  UsltLyrics,
    IReadOnlyList<(uint TimestampMs, string Text)> SyltLyrics
)
{
    /// <summary>所有字段均为空/默认值的空实例，用于"文件无标签"场景。</summary>
    public static readonly Mp3TagData Empty = new(
        null, null, null, null, null, 0, null, null, null, []);
}
