## 15. 完整示例

### 15.1 人脸识别系统

```csharp
using Vorcyc.Quiver;

// ═══ 定义实体 ═══
public class FaceFeature
{
    [QuiverKey]
    public string PersonId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime RegisterTime { get; set; }

    [QuiverVector(128, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}

// ═══ 定义数据库上下文 ═══
public class FaceDb : QuiverDbContext
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;

    public FaceDb(string path) : base(new QuiverDbOptions
    {
        DatabasePath = path,
        DefaultMetric = DistanceMetric.Cosine
    })
    { }
}

// ═══ 使用 ═══
await using var db = new FaceDb("faces.vdb");
await db.LoadAsync();

// 批量注册人脸
var faces = employees.Select(e => new FaceFeature
{
    PersonId = e.Id,
    Name = e.Name,
    RegisterTime = DateTime.UtcNow,
    Embedding = GetFaceEmbedding(e.Photo)
}).ToList();
db.Faces.AddRange(faces);

// 实时人脸识别
float[] probeVector = GetFaceEmbedding(cameraFrame);
var match = db.Faces.SearchTop1(probeVector);

if (match is { Similarity: > 0.9f })
{
    Console.WriteLine($"识别成功: {match.Entity.Name} (置信度: {match.Similarity:P1})");
}
else
{
    Console.WriteLine("未识别到匹配人脸");
}
```

### 15.2 多模态搜索引擎（HNSW 索引）

```csharp
using Vorcyc.Quiver;

// ═══ 多模态实体 ═══
public class MediaItem
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsPublished { get; set; }

    [QuiverVector(384, DistanceMetric.Cosine)]
    [QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 200, EfSearch = 100)]
    public float[] TextEmbedding { get; set; } = [];

    [QuiverVector(512, DistanceMetric.Cosine)]
    [QuiverIndex(VectorIndexType.HNSW, M = 24, EfConstruction = 200, EfSearch = 80)]
    public float[] ImageEmbedding { get; set; } = [];
}

// ═══ 数据库上下文 ═══
public class MediaDb : QuiverDbContext
{
    public QuiverSet<MediaItem> Items { get; set; } = null!;

    public MediaDb() : base(new QuiverDbOptions
    {
        DatabasePath = "media.vdb"
    })
    { }
}

// ═══ 使用 ═══
await using var db = new MediaDb();
await db.LoadAsync();

// 批量导入
await db.Items.AddRangeAsync(LoadMediaItems());

// 文本搜索 + 发布状态过滤
float[] textQuery = GetTextEmbedding("机器学习教程");
var textResults = db.Items.Search(
    e => e.TextEmbedding,
    textQuery,
    topK: 10,
    filter: e => e.IsPublished
);

// 图像搜索
float[] imageQuery = GetImageEmbedding(uploadedImage);
var imageResults = db.Items.Search(
    e => e.ImageEmbedding, imageQuery, topK: 10);

// 按类别过滤 + 高过采样率
Func<MediaItem, bool> categoryFilter = e => e.Category == "技术";
var filtered = db.Items.Search(
    e => e.TextEmbedding, textQuery, topK: 20,
    filter: categoryFilter,
    overFetchMultiplier: 8);
```

### 15.3 使用主构造函数简化上下文

```csharp
public class MyFaceDb(string path)
    : QuiverDbContext(new QuiverDbOptions
    {
        DatabasePath = path,
        DefaultMetric = DistanceMetric.Cosine
    })
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

// 使用
var db1 = new MyFaceDb("faces1.vdb");
var db2 = new MyFaceDb("faces2.vdb");
```

### 15.4 增量追加批量入库服务

```csharp
using Vorcyc.Quiver;

// ═══ 开启后台 Merge 的上下文 ═══
public class MyAppendDocDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    EnableBackgroundMerge = true,
    AutoMergeMaxSegments = 32,
    AutoMergeTombstoneRatio = 0.25
})
{
    public QuiverSet<Document> Documents { get; set; } = null!;
}

// ═══ 使用：高频写入 / 分阶段批量入库 ═══
// ⚠：批量入库路径推荐使用同步 using，并显式调用 AppendAsync / SaveAsync。
// DisposeAsync 只有在 SaveOnDispose = true 时才会自动 SaveAsync。
using var db = new MyAppendDocDb("documents.vdb");
await db.LoadAsync();

foreach (var batch in batches)        // 每批 1000 条
for (int i = 0; i < 1000; i++)
{
    db.Documents.Add(new Document
    {
        Id = $"doc-{i:D5}",
        Title = $"文档 {i}",
        Category = "技术",
        Embedding = GetEmbedding($"文档内容 {i}")
    });
}

// 增量追加：只写本批进一个新段，仅重写 footer，O(Δ)
await db.AppendAsync();

// 后续增量 Upsert / Remove
db.Documents.Upsert(new Document
{
    Id = "doc-00000",
    Title = "更新后的文档 0",
    Category = "教程",
    Embedding = GetEmbedding("更新后的内容")
});
db.Documents.RemoveByKey("doc-00999");

// 再次追加：实体变更作为新段，删除作为 Tombstone 段
await db.AppendAsync();

// 或者只 Flush 墓碑（此场景下并不重写存活实体）
await db.FlushTombstonesAsync();

// 需要时手动碎片整理（或交给后台 Merge 自动触发）
await db.SaveAsync();

// 不加载实体的情况下诊断文件
var info = await QuiverDbFile.InspectAsync("documents.vdb", verifyCrc: true);
Console.WriteLine($"v{info.FormatVersion}, {info.Segments.Count} 段, crcValid={info.CrcValid}");
```

### 15.5 旧文件格式迁徙工具

```csharp
using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Migration;

// 当前版本实体类型映射。旧文件中的 Type.FullName 必须能映射到当前 CLR 类型。
var typeMap = new Dictionary<string, Type>
{
    [typeof(Document).FullName!] = typeof(Document)
};

// 如果旧 Schema 中有属性重命名，在迁徙阶段就传入规则。
var rule = MigrationBuilder<Document>.Build(m => m
    .RenameProperty("OldTitle", "Title"));

var migrationRules = new Dictionary<string, SchemaMigrationRule>
{
    [typeof(Document).FullName!] = rule
};

await QuiverMigrator.MigrateAsync(
    sourceFile: "documents-v3.vdb",
    destinationFile: "documents-v4.vdb",
    typeMap: typeMap,
    migrationRules: migrationRules,
    options: new MigrateOptions
    {
        Overwrite = true,
        DeleteSourceOnSuccess = false,
        AllowNoop = true
    });

// 迁徙后不加载实体即可检查 v4 文件结构和 CRC。
var info = await QuiverDbFile.InspectAsync("documents-v4.vdb", verifyCrc: true);
Console.WriteLine($"v{info.FormatVersion}, 段数={info.Segments.Count}, CRC={info.CrcValid}");
```

### 15.6 异步并发搜索服务

```csharp
public class SearchService
{
    private readonly MyDocumentDb _db;

    public SearchService(string dbPath)
    {
        _db = new MyDocumentDb(dbPath);
        _db.LoadAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 并发安全的搜索方法，可被多个 ASP.NET 请求同时调用。
    /// QuiverSet 内部的读写锁保证线程安全。
    /// </summary>
    public async Task<List<QuiverSearchResult<Document>>> SearchAsync(
        float[] queryVector, int topK, CancellationToken ct)
    {
        return await _db.Documents.SearchAsync(
            e => e.Embedding, queryVector, topK, ct);
    }

    /// <summary>带类别过滤的搜索。</summary>
    public async Task<List<QuiverSearchResult<Document>>> SearchByCategoryAsync(
        float[] queryVector, string category, int topK, CancellationToken ct)
    {
        Func<Document, bool> filter = e => e.Category == category;
        return await _db.Documents.SearchAsync(
            e => e.Embedding, queryVector, topK,
            filter, overFetchMultiplier: 8, ct);
    }
}
```

---

