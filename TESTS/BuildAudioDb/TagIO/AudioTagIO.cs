using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using TagLibSharp2.Asf;
using TagLibSharp2.Core;
using TagLibSharp2.Id3.Id3v2;
using TagLibSharp2.Id3.Id3v2.Frames;
using TagLibSharp2.Mpeg;

namespace Niubi.Shared.TagIO;

/// <summary>
/// 音频 TAG 聚合 IO 层。
/// 每次操作只打开文件一次，以 <see cref="Mp3TagData"/> 为载体统一读写所有字段；
/// 细粒度便捷方法见 <see cref="AudioTag"/>。
/// </summary>
public static class AudioTagIO
{
    // -------------------------------------------------------------------------
    // 读取
    // -------------------------------------------------------------------------

    /// <summary>
    /// 一次 IO 读取 MP3 文件的所有标签字段（基本信息、封面、歌词）。
    /// </summary>
    /// <param name="mp3FilePath">MP3 文件路径。</param>
    /// <returns>标签快照；文件不可读或无 ID3v2 标签时返回 <see cref="Mp3TagData.Empty"/>。</returns>
    public static Mp3TagData ReadMp3Tags(string mp3FilePath)
    {
        var result = Mp3File.ReadFromFile(mp3FilePath);
        if (!result.IsSuccess) return Mp3TagData.Empty;
        using var mp3 = result.File!;
        var tag = mp3.Id3v2Tag;
        if (tag is null) return Mp3TagData.Empty;

        var cover = (tag.PictureFrames.FirstOrDefault(p => p.PictureType == PictureType.FrontCover)
                     ?? tag.PictureFrames.FirstOrDefault())
                    ?.PictureData.ToArray();

        var uslt = tag.LyricsFrames.FirstOrDefault()?.Text;

        var syltFrame = tag.SyncLyricsFrames.FirstOrDefault();
        IReadOnlyList<(uint, string)> sylt = syltFrame is null
            ? []
            : syltFrame.SyncItems.Select(i => (i.Timestamp, i.Text)).ToList();

        return new Mp3TagData(
            FixEncoding(tag.Title),
            FixEncoding(tag.Artist),
            FixEncoding(tag.Album),
            FixEncoding(tag.Year),
            FixEncoding(tag.Genre),
            tag.Track.GetValueOrDefault(),
            FixEncoding(tag.Comment),
            cover, uslt, sylt);
    }

    /// <summary>
    /// 从 WMA 文件读取封面图片字节。
    /// </summary>
    public static byte[]? ReadWmaCover(string wmaFilePath)
    {
        var result = AsfFile.ReadFromFile(wmaFilePath);
        if (!result.IsSuccess) return null;
        using var asf = result.File!;
        var pic = asf.Tag.Pictures.FirstOrDefault(p => p.PictureType == PictureType.FrontCover)
                  ?? asf.Tag.Pictures.FirstOrDefault();
        return pic?.PictureData.ToArray();
    }

    /// <summary>
    /// 自动根据扩展名读取封面（支持 .mp3 / .wma）。
    /// </summary>
    public static byte[]? ReadCoverAuto(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp3" => ReadMp3Tags(filePath).Cover,
            ".wma" => ReadWmaCover(filePath),
            _ => null,
        };

    // -------------------------------------------------------------------------
    // 写入
    // -------------------------------------------------------------------------

    /// <summary>
    /// 一次 IO 将 <see cref="Mp3TagData"/> 中的所有字段写入 MP3 文件。
    /// 强制使用 ID3v2.3 以兼容 Windows 资源管理器封面显示。
    /// </summary>
    public static bool WriteMp3Tags(string mp3FilePath, Mp3TagData data)
        => WriteMp3Tags(sourcePath: mp3FilePath, destPath: mp3FilePath, data);

    /// <summary>
    /// 一次 IO 将 <see cref="Mp3TagData"/> 写入 MP3 文件，支持读源与写目标分离（导出场景）。
    /// 强制使用 ID3v2.3 以兼容 Windows 资源管理器封面显示。
    /// </summary>
    public static bool WriteMp3Tags(string sourcePath, string destPath, Mp3TagData data)
    {
        var result = Mp3File.ReadFromFile(sourcePath);
        if (!result.IsSuccess) return false;
        using var mp3 = result.File!;

        var tag = BuildV23Tag(data);

        if (data.Cover is not null)
            WriteCoverBytesToTag(tag, data.Cover);

        if (data.UsltLyrics is not null)
        {
            WriteUsltLyricsToTag(tag, data.UsltLyrics);
            WriteSyltLyricsToTag(tag, data.UsltLyrics);
        }

        mp3.Id3v2Tag = tag;
        mp3.SaveToFile(destPath, File.ReadAllBytes(sourcePath));
        return true;
    }

    /// <summary>
    /// 将封面图片写入 WMA 文件（WM/Picture 属性）。
    /// </summary>
    public static bool WriteWmaCover(string wmaFilePath, string coverImagePath)
    {
        var result = AsfFile.ReadFromFile(wmaFilePath);
        if (!result.IsSuccess) return false;
        using var asf = result.File!;
        var coverBytes = File.ReadAllBytes(coverImagePath);
        asf.Tag.Pictures = [new AsfPicture("image/jpeg", PictureType.FrontCover, string.Empty, new BinaryData(coverBytes))];
        asf.SaveToFile(wmaFilePath, File.ReadAllBytes(wmaFilePath));
        return true;
    }

    // -------------------------------------------------------------------------
    // 供 AudioFolder 直接操作标签对象时复用（原 internal，跨程序集改为 public）
    // -------------------------------------------------------------------------

    /// <summary>用 <see cref="Mp3TagData"/> 的基本字段创建 ID3v2.3 标签对象。</summary>
    public static Id3v2Tag BuildV23Tag(Mp3TagData data)
    {
        var tag = new Id3v2Tag(Id3v2Version.V23);
        tag.Title   = data.Title;
        tag.Artist  = data.Artist;
        tag.Album   = data.Album;
        tag.Year    = data.Year;
        tag.Genre   = data.Genre;
        tag.Track   = data.Track;
        tag.Comment = data.Comment;
        return tag;
    }

    /// <summary>向标签对象写入封面图片文件（APIC 帧）。</summary>
    public static void WriteCoverToTag(Id3v2Tag tag, string coverImagePath) =>
        WriteCoverBytesToTag(tag, File.ReadAllBytes(coverImagePath));

    /// <summary>向标签对象写入封面字节（APIC 帧）。</summary>
    public static void WriteCoverBytesToTag(Id3v2Tag tag, byte[] coverBytes)
    {
        var pic = new PictureFrame("image/jpeg", PictureType.FrontCover, string.Empty,
            coverBytes, TextEncodingType.Latin1);
        tag.SetPicture(PictureType.FrontCover, pic);
    }

    /// <summary>向标签对象写入 USLT 歌词。</summary>
    public static void WriteUsltLyricsToTag(Id3v2Tag tag, string lrcText) =>
        tag.AddLyrics(new LyricsFrame(lrcText, string.Empty, string.Empty, TextEncodingType.Utf8));

    /// <summary>向标签对象写入 SYLT 同步歌词。</summary>
    public static bool WriteSyltLyricsToTag(Id3v2Tag tag, string lrcText)
    {
        var sylt = ParseLrcToSylt(lrcText);
        if (sylt is null) return false;
        tag.AddSyncLyrics(sylt);
        return true;
    }

    // -------------------------------------------------------------------------
    // 私有辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 修复 GBK/GB2312 被错误按 Latin-1 解码的乱码字符串。
    /// </summary>
    private static string? FixEncoding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (ContainsChinese(value)) return value.Trim();

        byte[] raw = Encoding.GetEncoding("iso-8859-1").GetBytes(value);

        string gbk = Encoding.GetEncoding("GBK").GetString(raw);
        if (ContainsChinese(gbk)) return gbk.Trim();

        string gb2312 = Encoding.GetEncoding("GB2312").GetString(raw);
        if (ContainsChinese(gb2312)) return gb2312.Trim();

        return value.Trim();
    }

    private static bool ContainsChinese(string text) =>
        text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>
    /// 解析 LRC 文本生成 SYLT 帧，支持 [mm:ss.xx] 和 [mm:ss.xxx] 格式。
    /// </summary>
    private static SyncLyricsFrame? ParseLrcToSylt(string lrc)
    {
        var regex = new Regex(@"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)$", RegexOptions.Multiline);
        var matches = regex.Matches(lrc);
        if (matches.Count == 0) return null;

        var sylt = new SyncLyricsFrame
        {
            TimestampFormat = TimestampFormat.Milliseconds,
            Language        = string.Empty,
            Encoding        = TextEncodingType.Utf8,
        };

        foreach (Match m in matches)
        {
            int minutes = int.Parse(m.Groups[1].Value);
            int seconds = int.Parse(m.Groups[2].Value);
            string msRaw = m.Groups[3].Value;
            int ms = msRaw.Length == 2 ? int.Parse(msRaw) * 10 : int.Parse(msRaw);
            sylt.AddSyncItem(m.Groups[4].Value.Trim(), (uint)((minutes * 60 + seconds) * 1000 + ms));
        }

        return sylt;
    }
}
