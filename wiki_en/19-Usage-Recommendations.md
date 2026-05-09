## 19. Usage Recommendations

### 19.1 Recommended Combination

For large, read-mostly databases where Top-K search is the main workload, prefer combining:

```csharp
new QuiverDbOptions
{
	DatabasePath = "data.vdb",
	Vectors =
	{
		MemoryMode = GlobalVectorMemoryMode.Auto,
		MemoryMapThresholdBytes = 256L * 1024 * 1024,
	},
	LargeFields =
	{
		MemoryMode = GlobalLargeFieldMemoryMode.PagedCache,
		MaxCachedPayloads = 128
	}
};
```

On the entity side:

```csharp
public partial class Document
{
	[QuiverKey]
	public string Id { get; set; } = "";

	public string Title { get; set; } = "";

	[QuiverVector(768, DistanceMetric.Cosine, Nullable = true, MemoryMode = VectorMemoryMode.MemoryMapped)]
	[QuiverIndex(VectorIndexType.HNSW)]
	public partial float[]? Embedding { get; set; }

	[QuiverLargeField(Nullable = true, MemoryMode = LargeFieldMemoryMode.PagedCache)]
	public partial byte[]? RawContent { get; set; }
}
```

This separates four kinds of data: entity objects are held by `InMemoryEntityStore`, vectors are owned by the `InMemory` / `MemoryMapped` store, large `byte[]` payloads live in dedicated `Blob` segments, and HNSW graph topology is restored from `IndexSnapshot`.

### 19.2 When Lazy Loading Is Useful

| Mechanism | Best fit | Benefit |
|---|---|---|
| `VectorMemoryMode.MemoryMapped` / `Auto` | Large read-mostly vector sets, especially high-dimensional embeddings | Reads vectors through the OS page cache and reduces managed-heap pressure |
| `[QuiverVector(MemoryMode = ...)]` | High-dimensional vectors; user code rarely reads `entity.Embedding` directly | Allows non-InMemory vector payload access through generated properties |
| `LargeFieldMemoryMode.LazyLoad` / `PagedCache` | Thumbnails, original files, audio chunks, or other large `byte[]` fields | Keeps large objects out of `EntityMeta` load paths and materializes on demand |
| `[QuiverLargeField]` | Per-field large `byte[]` memory behavior | Overrides global large-field memory mode per field |
| `HNSW + IndexSnapshot` | Large HNSW indexes where load-time graph rebuild is expensive | Restores graph topology and only replays uncovered ids |

Lazy loading is most useful for hundreds of thousands to millions of entities, high-dimensional embeddings, search-heavy workloads, and cases where only topK matches are materialized.

### 19.3 When It Is Not Worth It

| Scenario | Recommendation |
|---|---|
| Small datasets, for example a few thousand rows | Use `VectorMemoryMode.InMemory` and `LargeFieldMemoryMode.InMemory` for simplicity |
| Every operation reads every large field | Lazy/paged large fields may add I/O and cache-management overhead |
| Search results always require reading every matched vector payload | non-InMemory vector properties provide less benefit |
| Write-heavy workloads with frequent `Add` / `Upsert` | Prefer `VectorMemoryMode.InMemory`, then periodically call `SaveAsync` |
| Low-dimensional vectors or small total vector bytes | `MemoryMapped` / `Auto` has limited benefit |
| Very small `byte[]` fields | `[QuiverLargeField]` is optional |

### 19.4 Scenario Examples

#### Small database: keep it simple

For a few thousand rows, low-dimensional vectors, or frequent full-entity iteration, the simple in-memory setup is usually enough:

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "small.vdb",
	Vectors = { MemoryMode = GlobalVectorMemoryMode.InMemory },
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
};

public class Note
{
	[QuiverKey]
	public string Id { get; set; } = "";

	[QuiverVector(384, DistanceMetric.Cosine)]
	public float[] Embedding { get; set; } = [];
}
```

#### Large search database: mmap vectors + lazy large fields + HNSW snapshot

Good for face databases, RAG document stores, image vector stores, and other search-heavy workloads where only topK matches are materialized:

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "vectors.vdb",
	Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped },
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.PagedCache }
};

public partial class FaceFeature
{
	[QuiverKey]
	public string Id { get; set; } = "";

	[QuiverVector(512, DistanceMetric.Cosine, Nullable = true, MemoryMode = VectorMemoryMode.MemoryMapped)]
	[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
	public partial float[]? Embedding { get; set; }
}
```

The first `SaveAsync()` writes the HNSW `IndexSnapshot`; later `LoadAsync()` restores the graph topology while search still reads vectors from the mmap vector store.

#### Large fields only: enable only large-field lazy loading

If scalar entities are modest but `byte[]` payloads are large, lazy-load only the large fields:

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "catalog.vdb",
	Vectors = { MemoryMode = GlobalVectorMemoryMode.InMemory },
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.LazyLoad }
};
```

#### Large vectors only: use `MemoryMapped` / `Auto`

If entities are lightweight but vector bytes are large, move vectors out of the managed heap:

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "embeddings.vdb",
	Vectors =
	{
		MemoryMode = GlobalVectorMemoryMode.Auto,
		MemoryMapThresholdBytes = 128L * 1024 * 1024,
	},
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
};
```

#### Large binary fields: use `[QuiverLargeField]`

For thumbnails, original files, audio chunks, or other large `byte[]` fields, split the payload into dedicated `Blob` segments:

```csharp
public partial class Photo
{
	[QuiverKey]
	public string Id { get; set; } = "";

	[QuiverVector(768, DistanceMetric.Cosine, Nullable = true, MemoryMode = VectorMemoryMode.MemoryMapped)]
	public partial float[]? Embedding { get; set; }

	[QuiverLargeField(Nullable = true, MemoryMode = LargeFieldMemoryMode.PagedCache)]
	public partial byte[]? Thumbnail { get; set; }
}
```

#### Write-heavy ingestion: use `InMemory` first, then compact periodically

For frequent writes, keep the write path simple and explicitly persist/compact between batches:

```csharp
using var db = new MyDb(new QuiverDbOptions
{
	DatabasePath = "ingest.vdb",
	Vectors = { MemoryMode = GlobalVectorMemoryMode.InMemory },
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
});

foreach (var batch in batches)
{
	db.Documents.AddRange(batch);
	await db.AppendAsync();
	db.Documents.Clear();
}

await db.SaveAsync();
```

For `AppendAsync()` + `Clear()` batch ingestion, prefer synchronous `using`; `DisposeAsync()` only performs a full `SaveAsync()` when `SaveOnDispose = true`.

### 19.5 How the Pieces Relate

- Entities are always stored by `InMemoryEntityStore`; large-scale memory control is focused on vector and large-field payloads.
- `[QuiverVector(MemoryMode = ...)]` controls vector payload memory behavior for that field.
- `VectorMemoryMode.MemoryMapped` / `Auto` controls where vector data lives and is the key setting for reducing managed-heap pressure.
- `[QuiverLargeField]` moves large `byte[]` fields into separate blob segments and can enable lazy/paged access.
- `IndexSnapshot` stores HNSW topology only, not entities or vector copies, so it does not break lazy-loading semantics.

In short: **large dataset + large vectors/large fields + many searches + few materialized fields** is where `MemoryMapped/Auto vectors + LazyLoad/PagedCache large fields + HNSW Snapshot` is most useful.
