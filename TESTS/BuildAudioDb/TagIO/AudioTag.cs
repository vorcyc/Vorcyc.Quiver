using System.IO;

namespace Niubi.Shared.TagIO;

/// <summary>
/// 细粒度 TAG 便捷访问层。
/// 每个方法只读写单一字段，内部委托给 <see cref="AudioTagIO"/> 的聚合接口，不产生额外 IO。
/// 适合只需要单项数据的临时场景；需要多字段时建议直接调用 <see cref="AudioTagIO.ReadMp3Tags"/>。
/// </summary>
public static class AudioTag
{
    // -------------------------------------------------------------------------
    // MP3 读取 — 基本字段
    // -------------------------------------------------------------------------

    /// <summary>从 MP3 文件读取曲目标题。</summary>
    public static string? ReadTitle(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Title;

    /// <summary>从 MP3 文件读取艺人名。</summary>
    public static string? ReadArtist(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Artist;

    /// <summary>从 MP3 文件读取专辑名。</summary>
    public static string? ReadAlbum(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Album;

    /// <summary>从 MP3 文件读取发行年份。</summary>
    public static string? ReadYear(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Year;

    /// <summary>从 MP3 文件读取流派。</summary>
    public static string? ReadGenre(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Genre;

    /// <summary>从 MP3 文件读取曲目序号。</summary>
    public static uint ReadTrack(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Track;

    /// <summary>从 MP3 文件读取备注。</summary>
    public static string? ReadComment(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Comment;

    // -------------------------------------------------------------------------
    // MP3 读取 — 封面与歌词
    // -------------------------------------------------------------------------

    /// <summary>从 MP3 文件读取封面图片字节。</summary>
    public static byte[]? ReadCover(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).Cover;

    /// <summary>从 MP3 文件读取 USLT 歌词文本（原始 LRC，含时间戳）。</summary>
    public static string? ReadUsltLyrics(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).UsltLyrics;

    /// <summary>从 MP3 文件读取 SYLT 同步歌词条目列表。</summary>
    public static IReadOnlyList<(uint TimestampMs, string Text)> ReadSyltLyrics(string mp3FilePath)
        => AudioTagIO.ReadMp3Tags(mp3FilePath).SyltLyrics;

    // -------------------------------------------------------------------------
    // MP3 写入
    // -------------------------------------------------------------------------

    /// <summary>向 MP3 文件写入封面图片（一次 IO）。</summary>
    public static bool WriteCover(string mp3FilePath, string coverImagePath)
    {
        var data = AudioTagIO.ReadMp3Tags(mp3FilePath);
        return AudioTagIO.WriteMp3Tags(mp3FilePath, data with { Cover = File.ReadAllBytes(coverImagePath) });
    }

    /// <summary>向 MP3 文件写入 USLT + SYLT 歌词（一次 IO）。</summary>
    public static bool WriteLyrics(string mp3FilePath, string lrcText)
    {
        var data = AudioTagIO.ReadMp3Tags(mp3FilePath);
        return AudioTagIO.WriteMp3Tags(mp3FilePath, data with { UsltLyrics = lrcText });
    }

    // -------------------------------------------------------------------------
    // WMA
    // -------------------------------------------------------------------------

    /// <summary>从 WMA 文件读取封面图片字节。</summary>
    public static byte[]? ReadWmaCover(string wmaFilePath)
        => AudioTagIO.ReadWmaCover(wmaFilePath);

    /// <summary>将封面图片写入 WMA 文件。</summary>
    public static bool WriteWmaCover(string wmaFilePath, string coverImagePath)
        => AudioTagIO.WriteWmaCover(wmaFilePath, coverImagePath);

    // -------------------------------------------------------------------------
    // 通用
    // -------------------------------------------------------------------------

    /// <summary>自动根据扩展名读取封面（支持 .mp3 / .wma）。</summary>
    public static byte[]? ReadCoverAuto(string filePath)
        => AudioTagIO.ReadCoverAuto(filePath);
}
