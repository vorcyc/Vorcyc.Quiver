# Vorcyc Quiver 1.1.2

![Vorcyc Quiver 1.1.2](logo.jpg "Vorcyc Quiver 1.1.2")

> A pure .NET embedded vector database — zero native dependencies, runs in-process, no standalone database server deployment required.

📖 [Github Repo (full documention)](https://github.com/vorcyc/Vorcyc.Quiver)

**Quiver** draws on EF Core's `DbContext` design pattern, allowing developers to define entities and indexing strategies through declarative attributes such as `[QuiverKey]`, `[QuiverVector]`, and `[QuiverIndex]`, with the framework automatically completing model discovery, index construction, and persistence management at runtime.

---

## ✨ Core Features

- **Code-First Declarative Modeling** — Annotate entity classes with attributes; the framework auto-discovers and registers `QuiverSet<T>` collections via reflection — zero configuration.
- **Multiple ANN Index Algorithms** — Built-in Flat (brute-force), HNSW, IVF, and KDTree indexes, covering small-scale exact search to million-scale approximate search.
- **Flexible Persistence** — JSON (readable), XML (compatible), Binary (high-performance) formats, plus WAL incremental persistence reducing complexity from O(N) to O(Δ).
- **Concurrency Safe** — `QuiverSet<T>` uses `ReaderWriterLockSlim` internally; concurrent reads and writes are safe out-of-the-box.
- **SIMD Accelerated** — `TensorPrimitives`-based SIMD for vector similarity and L2 normalization.

**Typical Use Cases**: Semantic search · RAG · Face recognition · Image-to-image search · Recommendation systems · Multimodal retrieval

---

## 🚀 Quick Start

### 1. Define Entity

```csharp
using Vorcyc.Quiver;

public class Document
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}
```

### 2. Define Database Context

```csharp
public class MyDocumentDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;

    public MyDocumentDb() : base(new QuiverDbOptions
    {
        DatabasePath = "documents.json",
        StorageFormat = StorageFormat.Json,
        DefaultMetric = DistanceMetric.Cosine
    })
    { }
}
```

### 3. Use It

```csharp
await using var db = new MyDocumentDb();
await db.LoadAsync();

// Add
db.Documents.Add(new Document
{
    Id = "doc-001",
    Title = "Introduction to Vector Databases",
    Category = "Tutorial",
    Embedding = new float[384] // embedding vector from your model
});

// Search Top-5
float[] queryVector = new float[384];
var results = db.Documents.Search(e => e.Embedding, queryVector, topK: 5);

foreach (var r in results)
    Console.WriteLine($"{r.Entity.Title} — {r.Similarity:F4}");

// Auto-saved on DisposeAsync
```

---

## 📐 Distance Metrics

| Metric | Range | Use Case |
|--------|-------|----------|
| `Cosine` | [-1, 1] | Text embeddings, semantic search |
| `Euclidean` | (0, 1] | Spatial coordinates, physical distances |
| `DotProduct` | $(-\infty, +\infty)$ | Pre-normalized vectors, MIPS |

---

## 🗂️ Index Types

| Index | Search Speed | Accuracy | Best For |
|-------|-------------|----------|----------|
| **Flat** | O(n×d) | 100% | < 10K entries, exact search |
| **HNSW** | O(log n) | ~95-99%+ | 10K–10M, universal preferred |
| **IVF** | O(n/k×d) | ~90-99% | 100K+, high throughput |
| **KDTree** | O(log n) | 100% | < 10K, dimensions < 20 |

```csharp
// HNSW example
[QuiverVector(768, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
public float[] Embedding { get; set; } = [];

// IVF example
[QuiverVector(128, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.IVF, NumClusters = 100, NumProbes = 15)]
public float[] Feature { get; set; } = [];
```

---

## 🔧 CRUD Operations

```csharp
// Add
db.Documents.Add(entity);
db.Documents.AddRange(batch);          // Atomic two-phase commit
await db.Documents.AddRangeAsync(batch);

// Upsert (insert or update, single write lock)
db.Documents.Upsert(entity);

// Remove
db.Documents.Remove(entity);           // By entity (matched by key)
db.Documents.RemoveByKey("doc-001");   // By key directly

// Find
Document? doc = db.Documents.Find("doc-001"); // O(1)

// Exists
bool exists = db.Documents.Exists("doc-001");              // By key, O(1)
bool hasTutorial = db.Documents.Exists(e => e.Category == "Tutorial"); // By predicate, O(n) short-circuit

// Clear
db.Documents.Clear();

// Count
int count = db.Documents.Count;
```

---

## 🔍 Vector Search

```csharp
// Top-K
var results = db.Documents.Search(e => e.Embedding, queryVector, topK: 10);

// Threshold
var results = db.Documents.SearchByThreshold(e => e.Embedding, queryVector, threshold: 0.85f);

// Filtered
var results = db.Documents.Search(
    e => e.Embedding, queryVector, topK: 10,
    filter: e => e.Category == "Tutorial",
    overFetchMultiplier: 4);

// Top-1
var best = db.Documents.SearchTop1(e => e.Embedding, queryVector);

// Async (all methods have Async variants)
var results = await db.Documents.SearchAsync(e => e.Embedding, queryVector, topK: 10, ct);

// Default field shorthand (single vector field entities)
var results = db.Documents.Search(queryVector, topK: 5);
```

---

## 💾 Persistence

```csharp
await db.SaveAsync();           // Full snapshot
await db.SaveChangesAsync();    // WAL incremental, O(Δ)
await db.CompactAsync();        // Full snapshot + clear WAL
await db.LoadAsync();           // Load snapshot + replay WAL
```

| Format | Readability | Size | Speed | Use Case |
|--------|-------------|------|-------|----------|
| `Json` | ✅ Excellent | Largest | Average | Development and debugging |
| `Xml` | ✅ Good | Large | Average | Compatibility |
| `Binary` | ❌ | **Smallest** | **Fastest** | Production |

### WAL Mode

```csharp
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    StorageFormat = StorageFormat.Binary,
    EnableWal = true,
    WalCompactionThreshold = 10_000,
    WalFlushToDisk = true
};
```

---

## ⚙️ Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DatabasePath` | `string?` | `null` | Storage path; `null` = in-memory mode |
| `DefaultMetric` | `DistanceMetric` | `Cosine` | Default distance metric |
| `StorageFormat` | `StorageFormat` | `Json` | `Json` / `Xml` / `Binary` |
| `JsonOptions` | `JsonSerializerOptions` | Indented + CamelCase | JSON serialization options |
| `EnableWal` | `bool` | `false` | Enable WAL incremental persistence |
| `WalCompactionThreshold` | `int` | `10,000` | Auto-compact threshold |
| `WalFlushToDisk` | `bool` | `true` | fsync after WAL write |

---

## 🔒 Concurrency

`QuiverSet<T>` uses `ReaderWriterLockSlim`:

- **Read operations** (Search / Find / Exists / Count) — shared lock, parallel execution ✅
- **Write operations** (Add / Upsert / Remove / Clear) — exclusive lock 🔒

No external locking needed.

---

## ♻️ Lifecycle

```csharp
// Recommended: await using (auto-save on dispose)
await using var db = new MyDocumentDb();
await db.LoadAsync();
// ... use db ...
// Scope ends → DisposeAsync → auto-save → release resources
```

| Disposal | Auto-Save | Recommended |
|----------|-----------|-------------|
| `DisposeAsync()` | ✅ Yes | ✅ Use with `await using` |
| `Dispose()` | ❌ No | Manual control scenarios |
