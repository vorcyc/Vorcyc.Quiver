using Vorcyc.Quiver;

namespace NiubiServer.Database;

/// <summary>
/// 音频媒体实体，存储于 Vorcyc.Quiver 向量数据库。
/// </summary>
[QuiverEntity("niubi_audio")]
public partial class AudioMediaEntity
{
    /// <summary>
    /// 主键：GUID 字符串，每次写入新条目时生成，与文件内容/路径无关。
    /// </summary>
    [QuiverKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// MP3 文件完整路径（可变，文件移动后仅更新此字段）。
    /// </summary>
    public string AudioFilePath { get; set; } = string.Empty;

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
    [QuiverVector(768, DistanceMetric.Cosine, MemoryMode = VectorMemoryMode.MemoryMapped, Nullable = true)]
    [QuiverIndex(VectorIndexType.Flat)]
    public partial float[]? MertEmbedding { get; set; }
}
