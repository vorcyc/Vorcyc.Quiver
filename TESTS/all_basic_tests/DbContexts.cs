using Vorcyc.Quiver;

namespace AllBasicTests;

/// <summary>单向量数据库上下文。</summary>
public class MyFaceDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>多向量数据库上下文，包含单个多字段集合。</summary>
public class MyMultiVectorDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

/// <summary>富类型数据库上下文，用于测试新增属性类型。</summary>
public class MyRichTypeDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<RichTypeEntity> RichItems { get; set; } = null!;
}

/// <summary>WAL 模式数据库上下文（默认阈值 10,000）。</summary>
public class MyWalDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = StorageFormat.Binary,
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
    StorageFormat = StorageFormat.Binary,
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
    StorageFormat = StorageFormat.Binary,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

// ── MemoryMapped 模式数据库上下文 ──

/// <summary>MemoryMapped 模式单向量数据库上下文。</summary>
public class MyMmapFaceDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine,
    MemoryMode = MemoryMode.MemoryMapped
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>MemoryMapped 模式多向量数据库上下文。</summary>
public class MyMmapMultiVectorDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine,
    MemoryMode = MemoryMode.MemoryMapped
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

/// <summary>MemoryMapped + WAL 模式数据库上下文。</summary>
public class MyMmapWalDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = StorageFormat.Binary,
    DefaultMetric = DistanceMetric.Cosine,
    MemoryMode = MemoryMode.MemoryMapped,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

// ── 度量测试数据库上下文 ──

public class ManhattanDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<ManhattanEntity> Items { get; set; } = null!; }
public class ChebyshevDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<ChebyshevEntity> Items { get; set; } = null!; }
public class PearsonDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<PearsonEntity> Items { get; set; } = null!; }
public class HammingDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<HammingEntity> Items { get; set; } = null!; }
public class JaccardDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<JaccardEntity> Items { get; set; } = null!; }
public class CanberraDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<CanberraEntity> Items { get; set; } = null!; }
public class CustomSimDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions { DatabasePath = path, StorageFormat = format }) { public QuiverSet<CustomSimEntity> Items { get; set; } = null!; }
