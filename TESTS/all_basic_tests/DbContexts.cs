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

/// <summary>懒加载模式数据库上下文。</summary>
public class MyLazyLoadDb(string path, int maxCachedPages = 16, int pageSize = 512)
    : QuiverDbContext(new QuiverDbOptions
    {
        DatabasePath = path,
        DefaultMetric = DistanceMetric.Cosine,
        EntityCache = EntityCacheMode.LazyPaging,
        MaxCachedPages = maxCachedPages,
        PageSize = pageSize
    })
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>懒加载 + WAL 模式数据库上下文。</summary>
public class MyLazyLoadWalDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    EntityCache = EntityCacheMode.LazyPaging,
    MaxCachedPages = 8,
    PageSize = 256,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>WAL 模式数据库上下文（默认阈值 10,000）。</summary>
public class MyWalDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>WAL 模式数据库上下文（低压缩阈值，用于测试自动压缩）。</summary>
public class MyWalDbLowThreshold(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalCompactionThreshold = 50,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>WAL 模式多向量数据库上下文。</summary>
public class MyWalMultiVecDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

// ── MemoryMapped 模式数据库上下文 ──

/// <summary>MemoryMapped 模式单向量数据库上下文。</summary>
public class MyMmapFaceDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    VectorStorage = VectorStorageMode.MemoryMapped
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>MemoryMapped 模式多向量数据库上下文。</summary>
public class MyMmapMultiVectorDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    VectorStorage = VectorStorageMode.MemoryMapped
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

/// <summary>MemoryMapped + WAL 模式数据库上下文。</summary>
public class MyMmapWalDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    VectorStorage = VectorStorageMode.MemoryMapped,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

// ── 度量测试数据库上下文 ──

public class ManhattanDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<ManhattanEntity> Items { get; set; } = null!; }
public class ChebyshevDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<ChebyshevEntity> Items { get; set; } = null!; }
public class PearsonDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<PearsonEntity> Items { get; set; } = null!; }
public class HammingDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<HammingEntity> Items { get; set; } = null!; }
public class JaccardDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<JaccardEntity> Items { get; set; } = null!; }
public class CanberraDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<CanberraEntity> Items { get; set; } = null!; }
public class CustomSimDb(string path) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path }) { public QuiverSet<CustomSimEntity> Items { get; set; } = null!; }

// ── Schema 迁移测试上下文 ──

/// <summary>
/// 迁移读取上下文，配置了以下迁移规则（应对旧格式 QDB 文件）：
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

/// <summary>迁移 + WAL 组合上下文。</summary>
public class MigrationWalDb : QuiverDbContext
{
    public QuiverSet<MigrationEntity> Items { get; set; } = null!;

    public MigrationWalDb(string path) : base(new QuiverDbOptions
    {
        DatabasePath = path,
        EnableWal = true,
        WalFlushToDisk = true
    })
    {
        ConfigureMigration<MigrationEntity>(m => m
            .RenameProperty("OldTitle", "Title"));
    }
}


