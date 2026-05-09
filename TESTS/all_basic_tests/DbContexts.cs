using Vorcyc.Quiver;

namespace AllBasicTests;

/// <summary>单向量数据库上下文。</summary>
public class MyFaceDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>多向量数据库上下文，包含单个多字段集合。</summary>
public class MyMultiVectorDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

/// <summary>富类型数据库上下文，用于测试新增属性类型。</summary>
public class MyRichTypeDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<RichTypeEntity> RichItems { get; set; } = null!;
}

/// <summary>默认内存模式数据库上下文。</summary>
public class MyLazyLoadDb(string path)
    : QuiverDbContext(new QuiverDbOptions
    {
        DatabasePath = path,
        DefaultMetric = DistanceMetric.Cosine,
        LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
    })
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

// ── MemoryMapped 模式数据库上下文 ──

/// <summary>原 MemoryMapped 模式单向量数据库上下文（现等同 Heap 存储）。</summary>
public class MyMmapFaceDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>原 MemoryMapped 模式多向量数据库上下文。</summary>
public class MyMmapMultiVectorDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

// ── 度量测试数据库上下文 ──

public class ManhattanDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<ManhattanEntity> Items { get; set; } = null!; }
public class ChebyshevDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<ChebyshevEntity> Items { get; set; } = null!; }
public class PearsonDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<PearsonEntity> Items { get; set; } = null!; }
public class HammingDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<HammingEntity> Items { get; set; } = null!; }
public class JaccardDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<JaccardEntity> Items { get; set; } = null!; }
public class CanberraDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<CanberraEntity> Items { get; set; } = null!; }
public class CustomSimDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<CustomSimEntity> Items { get; set; } = null!; }

// ── Half 向量测试上下文 ──

/// <summary>Half[] 向量数据库上下文。</summary>
public class HalfVectorDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<HalfVectorEntity> Items { get; set; } = null!;
}

// ── Schema 迁移测试上下文 ──
/// <list type="bullet">
///   <item>OldTitle → Title（属性重命名）</item>
///   <item>Score：int 自动强转为 double（CoerceValue 隐式迁移）</item>
///   <item>Legacy 字段已删除（加载时自动跳过）</item>
///   <item>NewField 为新增字段（旧文件中无，自动取默认值 "default"）</item>
/// </list>
/// </summary>
public class MigrationDb : QuiverDbContext
{
    public QuiverSet<MigrationEntity> Items { get; set; } = null!;

    public MigrationDb(string path) : base(new QuiverDbOptions { DatabasePath = path })
    {
        ConfigureMigration<MigrationEntity>(m => m
            .RenameProperty("OldTitle", "Title"));
    }
}


