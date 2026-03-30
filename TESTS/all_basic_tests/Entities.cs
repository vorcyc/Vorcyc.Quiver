using Vorcyc.Quiver;

namespace AllBasicTests;

/// <summary>单向量实体：面部特征。</summary>
public class FaceFeature
{
    [QuiverKey]
    public string PersonId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime RegisterTime { get; set; }

    [QuiverVector(128)]
    public float[] Embedding { get; set; } = [];
}

/// <summary>
/// 多向量实体：同时持有文本、图像、音频三组不同维度的向量。
/// 用于验证多字段独立索引、分字段搜索、持久化往返等场景。
/// </summary>
public class MultiVectorEntity
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Score { get; set; }
    public bool IsActive { get; set; }

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] TextEmbedding { get; set; } = [];

    [QuiverVector(512, DistanceMetric.Euclidean)]
    public float[] ImageEmbedding { get; set; } = [];

    [QuiverVector(256, DistanceMetric.DotProduct)]
    public float[] AudioEmbedding { get; set; } = [];
}

/// <summary>
/// 富类型实体：覆盖 BinaryStorageProvider 新增的 7 种 TypeCode。
/// 包含 byte、short、Half、DateTimeOffset、TimeSpan、byte[]、double[] 属性，
/// 用于验证二进制序列化往返的完整性和精度。
/// </summary>
public class RichTypeEntity
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    public byte ByteVal { get; set; }
    public short ShortVal { get; set; }
    public Half HalfVal { get; set; }
    public DateTimeOffset OffsetTime { get; set; }
    public TimeSpan Duration { get; set; }
    public byte[] Blob { get; set; } = [];
    public double[] Weights { get; set; } = [];

    [QuiverVector(128)]
    public float[] Embedding { get; set; } = [];
}
