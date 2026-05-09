
using Niubi.Shared.TagIO;
using System.Security.Cryptography;
using Vorcyc.Quiver;

public class AudioDbContext : QuiverDbContext
{
    public QuiverSet<AudioMediaEntity> Audios { get; set; } = null!;

    public AudioDbContext(string databasePath) : base(new QuiverDbOptions
    {
        DatabasePath = databasePath,
        DefaultMetric = DistanceMetric.Cosine,
        //LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory },
        //Vectors = { MemoryMode = GlobalVectorMemoryMode.LazyLoad },
        LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.LazyLoad},
        Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped },
    })
    { }
}

/// <summary>
/// 音频媒体实体，存储于 Vorcyc.Quiver 向量数据库。
/// </summary>
[QuiverEntity("audio_entity")]
public partial class AudioMediaEntity
{
    /// <summary>
    /// 主键：GUID 字符串，每次写入新条目时生成，与文件内容/路径无关。
    /// </summary>
    [QuiverKey]
    //public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid Id { get; set; }

    /// <summary>
    /// MP3 文件完整路径（可变，文件移动后仅更新此字段）。
    /// </summary>
    public string AudioFilePath { get; set; } = string.Empty;

    public long SourceId { get; set; }

    /// <summary>
    /// 文件指纹：取前 64 KB 内容 + 文件大小拼合的 MD5 十六进制串。
    /// 用于检测文件是否被移动（MD5 相同但路径变化 → 只更新路径，跳过推断）。
    /// </summary>
    public string FileMd5 { get; set; } = string.Empty;

    /// <summary>歌曲标题（来自 ID3 标签）。</summary>
    public string? Title { get; set; }

    /// <summary>歌手（来自 ID3 标签）。</summary>
    public string? Artist { get; set; }

    /// <summary>专辑（来自 ID3 标签）。</summary>
    public string? Album { get; set; }

    /// <summary>是否在 ID3 TAG 中包含封面图片。</summary>
    public bool HasCover { get; set; }

    /// <summary>是否在 ID3 TAG 中包含歌词（USLT）。</summary>
    public bool HasLyric { get; set; }

    /// <summary>音频时长（秒），从 NAudio 解析获取。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// 流派分类 Top-3（GenreClassifier 输出）。
    /// 格式："摇滚(rock):0.85|流行(pop):0.12|蓝调(blues):0.03"。
    /// 未推断时为 null。
    /// </summary>
    public string? GenreTop3 { get; set; }

    /// <summary>
    /// AudioSet 命中标签（AudioClassifier 输出）。
    /// 格式："音乐(Music):0.91|流行音乐(Pop music):0.72|说唱(Rapping):0.45"。
    /// 未推断时为 null。
    /// </summary>
    public string? AudioLabels { get; set; }

    /// <summary>
    /// MERT-v1-95M 音频语义嵌入（768 维均值池化）。
    /// 捕获音乐风格、情感、旋律等深层语义，适合高质量相似推荐。
    /// 未推断时为 null。
    /// </summary>
    [QuiverVector(768, DistanceMetric.Cosine, Nullable = true)]
    [QuiverIndex(VectorIndexType.HNSW, EfConstruction = 500, EfSearch = 500, M = 40)]
    public partial float[]? MertEmbedding { get; set; }


    public override string ToString()
    {
        return $"AudioMediaEntity(SourceId={SourceId}, Title={Title}, Artist={Artist}, Album={Album}, Duration={DurationSeconds:F1}s, GenreTop3={GenreTop3}, AudioLabels={AudioLabels})";
    }


    /// <summary>
    /// 从平铺 MP3 文件构建新实体（通过 ID3 TAG 读取元数据，计算文件指纹）。
    /// </summary>
    public static AudioMediaEntity ToEntityFromMp3(string mp3Path)
    {
        Mp3TagData tags;
        try { tags = AudioTagIO.ReadMp3Tags(mp3Path); }
        catch { tags = Mp3TagData.Empty; }

        var fileName = Path.GetFileNameWithoutExtension(mp3Path);
        double durationSeconds = 0;
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(mp3Path);
            durationSeconds = reader.TotalTime.TotalSeconds;
        }
        catch { }

        return new AudioMediaEntity
        {
            Id = Guid.NewGuid(),
            AudioFilePath = mp3Path,
            SourceId = long.Parse(System.IO.Path.GetFileName(mp3Path).Split('_')[0]),
            FileMd5 = ComputeFileMd5(mp3Path),
            Title = string.IsNullOrWhiteSpace(tags.Title) ? fileName : tags.Title,
            Artist = tags.Artist,
            Album = tags.Album,
            HasCover = tags.Cover is { Length: > 0 },
            HasLyric = !string.IsNullOrEmpty(tags.UsltLyrics),
            DurationSeconds = durationSeconds,
        };
    }

    /// <summary>
    /// 计算文件指纹：读取前 64 KB 内容 + 文件大小，拼合后求 MD5。
    /// 速度快，对 MP3 误碰撞概率极低。
    /// </summary>
    public static string ComputeFileMd5(string filePath)
    {
        const int prefixBytes = 64 * 1024; // 64 KB
        try
        {
            var info = new FileInfo(filePath);
            long fileSize = info.Length;

            Span<byte> buf = stackalloc byte[prefixBytes];
            int read;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                read = fs.Read(buf);

            // 把文件大小（8 字节 little-endian）追加到缓冲区末尾一起哈希
            Span<byte> combined = new byte[read + 8];
            buf[..read].CopyTo(combined);
            BitConverter.TryWriteBytes(combined[read..], fileSize);

            return Convert.ToHexString(MD5.HashData(combined)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
