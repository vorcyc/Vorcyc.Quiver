using Vorcyc.Quiver;
using Vorcyc.Quiver.Similarity;

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
/// 富类型实体：覆盖 BinaryStorageProvider 支持的扩展 TypeCode。
/// 包含 byte、short、Half、DateTimeOffset、TimeSpan、byte[]、double[]，
/// 以及 ushort、uint、ulong、sbyte、char、DateOnly、TimeOnly 标量，
/// 和 ushort[]、uint[]、ulong[]、sbyte[]、DateOnly[]、TimeOnly[]、short[]、int[]、long[]、bool[]、Half[] 数组形式，
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

    // 新增标量类型
    public ushort UShortVal { get; set; }
    public uint UIntVal { get; set; }
    public ulong ULongVal { get; set; }
    public sbyte SByteVal { get; set; }
    public char CharVal { get; set; }
    public DateOnly DateVal { get; set; }
    public TimeOnly TimeVal { get; set; }

    // 新增数组类型
    public ushort[] UShortArr { get; set; } = [];
    public uint[] UIntArr { get; set; } = [];
    public ulong[] ULongArr { get; set; } = [];
    public sbyte[] SByteArr { get; set; } = [];
    public DateOnly[] DateArr { get; set; } = [];
    public TimeOnly[] TimeArr { get; set; } = [];
    public short[] ShortArr { get; set; } = [];
    public int[] IntArr { get; set; } = [];
    public long[] LongArr { get; set; } = [];
    public bool[] BoolArr { get; set; } = [];
    public Half[] HalfArr { get; set; } = [];

    [QuiverVector(128)]
    public float[] Embedding { get; set; } = [];
}

// ══════════════════════════════════════════════════════════════════
// Half[] 向量实体
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Half 精度向量实体（fp16 物理存储）。
/// 用于验证 Half[] 向量的增删查改、float/Half 双重查询重载、以及 Float16 持久化往返。
/// </summary>
public class HalfVectorEntity
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    [QuiverVector(16, DistanceMetric.Cosine)]
    public Half[] Vec { get; set; } = [];
}

/// <summary>
/// Half[] + MemoryMapped 向量实体。
/// 用于验证 fp16 向量在内存映射模式下的持久化往返与 lazy 物化（partial 属性 + 源生成器 Half[] 访问器）。
/// </summary>
public partial class MmapHalfVectorEntity
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    [QuiverVector(16, DistanceMetric.Cosine, MemoryMode = VectorMemoryMode.MemoryMapped)]
    public partial Half[]? Vec { get; set; }
}

// ══════════════════════════════════════════════════════════════════
// 度量测试实体
// ══════════════════════════════════════════════════════════════════

/// <summary>曼哈顿度量实体。</summary>
public class ManhattanEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, DistanceMetric.Manhattan)] public float[] Vec { get; set; } = [];
}

/// <summary>切比雪夫度量实体。</summary>
public class ChebyshevEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, DistanceMetric.Chebyshev)] public float[] Vec { get; set; } = [];
}

/// <summary>皮尔逊相关度量实体。</summary>
public class PearsonEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, DistanceMetric.Pearson)] public float[] Vec { get; set; } = [];
}

/// <summary>汉明度量实体（二值向量）。</summary>
public class HammingEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, DistanceMetric.Hamming)] public float[] Vec { get; set; } = [];
}

/// <summary>Jaccard 度量实体（非负向量）。</summary>
public class JaccardEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, DistanceMetric.Jaccard)] public float[] Vec { get; set; } = [];
}

/// <summary>堪培拉度量实体。</summary>
public class CanberraEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, DistanceMetric.Canberra)] public float[] Vec { get; set; } = [];
}

/// <summary>自定义度量实体（通过 CustomSimilarity 指定 ManhattanSimilarity struct）。</summary>
public class CustomSimEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(64, CustomSimilarity = typeof(ManhattanSimilarity))] public float[] Vec { get; set; } = [];
}

// ══════════════════════════════════════════════════════════════════
// Schema 迁移测试实体
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Schema 迁移测试实体（当前/新版本）。
/// <para>
/// 历史 Schema（旧版二进制文件中）：
/// <list type="bullet">
///   <item><b>OldTitle</b>（string）→ 迁移后重命名为 <see cref="Title"/></item>
///   <item><b>Score</b>（int）→ 迁移后仍叫 Score，但类型变为 double</item>
///   <item><b>Legacy</b>（string）→ 迁移后已删除，加载时自动跳过</item>
/// </list>
/// 新增字段：
/// <list type="bullet">
///   <item><b>NewField</b>（string）→ 旧文件中不存在，加载后取默认值 "default"</item>
/// </list>
/// </para>
/// </summary>
public class MigrationEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double Score { get; set; }
    public string NewField { get; set; } = "default";

    [QuiverVector(32)] public float[] Embedding { get; set; } = [];
}

