## 15. Complete Examples

### 15.1 Face Recognition System

```csharp
using Vorcyc.Quiver;

// ═══ Define Entity ═══
public class FaceFeature
{
    [QuiverKey]
    public string PersonId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime RegisterTime { get; set; }

    [QuiverVector(128, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}

// ═══ Define Database Context ═══
public class FaceDb : QuiverDbContext
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;

    public FaceDb(string path) : base(new QuiverDbOptions
    {
        DatabasePath = path,          // binary .vdb file
        DefaultMetric = DistanceMetric.Cosine
    })
    { }
}

// ═══ Usage ═══
await using var db = new FaceDb("faces.vdb");
await db.LoadAsync();

// Batch register faces
var faces = employees.Select(e => new FaceFeature
{
    PersonId = e.Id,
    Name = e.Name,
    RegisterTime = DateTime.UtcNow,
    Embedding = GetFaceEmbedding(e.Photo)
}).ToList();
db.Faces.AddRange(faces);

// Real-time face recognition
float[] probeVector = GetFaceEmbedding(cameraFrame);
var match = db.Faces.SearchTop1(probeVector);

if (match is { Similarity: > 0.9f })
{
    Console.WriteLine($"Recognition successful: {match.Entity.Name} (confidence: {match.Similarity:P1})");
}
else
{
    Console.WriteLine("No matching face recognized");
}
```

### 15.2 Multimodal Search Engine (HNSW Index)

```csharp
using Vorcyc.Quiver;

// ═══ Multimodal Entity ═══
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

// ═══ Database Context ═══
public class MediaDb : QuiverDbContext
{
    public QuiverSet<MediaItem> Items { get; set; } = null!;

    public MediaDb() : base(new QuiverDbOptions
    {
        DatabasePath = "media.vdb"
    })
    { }
}

// ═══ Usage ═══
await using var db = new MediaDb();
await db.LoadAsync();

// Batch import
await db.Items.AddRangeAsync(LoadMediaItems());

// Text search + published status filtering
float[] textQuery = GetTextEmbedding("machine learning tutorial");
var textResults = db.Items.Search(
    e => e.TextEmbedding,
    textQuery,
    topK: 10,
    filter: e => e.IsPublished
);

// Image search
float[] imageQuery = GetImageEmbedding(uploadedImage);
var imageResults = db.Items.Search(
    e => e.ImageEmbedding, imageQuery, topK: 10);

// Category filtering + high over-fetch rate
Func<MediaItem, bool> categoryFilter = e => e.Category == "Technology";
var filtered = db.Items.Search(
    e => e.TextEmbedding, textQuery, topK: 20,
    filter: categoryFilter,
    overFetchMultiplier: 8);
```

### 15.3 Simplifying Context with Primary Constructor

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

// Usage
var db = new MyFaceDb("data.vdb");

// Export to JSON for inspection or migration
await db.ExportAsync("backup.json", ExportFormat.Json);
```

### 15.4 Incremental Append Service (v4)

```csharp
using Vorcyc.Quiver;

// ═══ v4 segmented append context ═══
public class MyAppendDocDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    Vectors.MemoryMode = GlobalVectorMemoryMode.Auto,
    LargeFields.MemoryMode = GlobalLargeFieldMemoryMode.PagedCache,
    EnableBackgroundMerge = true,
    AutoMergeMaxSegments = 32,
    AutoMergeTombstoneRatio = 0.25
})
{
    public QuiverSet<Document> Documents { get; set; } = null!;
}

// ═══ Usage: streaming / batched ingest ═══
// IMPORTANT: synchronous using is recommended for staged ingest.
// DisposeAsync only auto-saves when SaveOnDispose = true.
using var db = new MyAppendDocDb("documents.vdb");
await db.LoadAsync();

foreach (var batch in EnumerateBatches())
{
    foreach (var doc in batch)
        db.Documents.Add(doc);

    // One new segment per batch; only the footer is rewritten. O(Δ) on disk.
    await db.AppendAsync();

    // Free memory before the next batch. Tombstones flushed below.
    db.Documents.Clear();
}

// Delete-heavy follow-up
foreach (var staleKey in staleKeys)
    db.Documents.RemoveByKey(staleKey);
await db.FlushTombstonesAsync();

// Manual compaction — coalesce live segments and drop tombstones
await db.SaveAsync();
```

### 15.5 Legacy File Format Migration Tool

```csharp
using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Migration;

// Map the type full names stored in the old file to the current CLR types.
var typeMap = new Dictionary<string, Type>
{
    [typeof(Document).FullName!] = typeof(Document)
};

// If the old schema used renamed properties, pass the rules during migration.
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

// Validate the v4 result without loading entities.
var info = await QuiverDbFile.InspectAsync("documents-v4.vdb", verifyCrc: true);
Console.WriteLine($"v{info.FormatVersion}, segments={info.Segments.Count}, crcValid={info.CrcValid}");
```

### 15.6 Async Concurrent Search Service

```csharp
public class SearchService
{
    private readonly MyDocumentDb _db;

    public SearchService(string dbPath)
    {
        _db = new MyDocumentDb(dbPath, StorageFormat.Binary);
        _db.LoadAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Concurrency-safe search method that can be called by multiple ASP.NET requests simultaneously.
    /// The reader-writer lock inside QuiverSet guarantees thread safety.
    /// </summary>
    public async Task<List<QuiverSearchResult<Document>>> SearchAsync(
        float[] queryVector, int topK, CancellationToken ct)
    {
        return await _db.Documents.SearchAsync(
            e => e.Embedding, queryVector, topK, ct);
    }

    /// <summary>Search with category filtering.</summary>
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

