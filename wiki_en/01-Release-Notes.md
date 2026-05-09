# Release Notes — Vorcyc Quiver 4.0.1

![Vorcyc Quiver 4.0.1](../logo.jpg "Vorcyc Quiver 4.0.1")

> **Product Positioning**: A pure .NET embedded vector database — zero native dependencies, runs in-process, no standalone database server deployment required  
> **Framework Version**: .NET 10  
> **Namespace**: `Vorcyc.Quiver`  
> **Design Philosophy**: Similar to EF Core's `DbContext` pattern, achieving automatic discovery, index construction, and persistence of the vector database through declarative attribute annotations  
> **Core Features**: Code-First declarative entity definition · Multiple ANN indexes (Flat / HNSW / IVF / KDTree) · 9 built-in distance metrics + custom similarity support · Binary primary storage + JSON/XML export/import · Schema Migration (property rename / value transform) · Reader-writer lock concurrency safety · SIMD-accelerated similarity computation · payload memory modes for vectors and large fields
> **Keywords**: `Embedded Vector Database` `Pure .NET` `ANN` `Approximate Nearest Neighbor Search` `Similarity Retrieval` `HNSW` `IVF` `KDTree` `Code-First` `EF Core Style` `Embedding` `Semantic Search` `Face Recognition` `Image-to-Image Search` `RAG` `SIMD` `Schema Migration` `ISimilarity` `Custom Metric`  
> **Name Origin**: Quiver — a container for arrows (Arrow), and the mathematical essence of a vector is an arrow

---

### What's New in 4.0.1

> **File Format Compatibility**: Snapshot (`.vdb`) files remain fully backward-compatible with v1.x, v2.x, v3.0.x, v3.1.x, v3.2.x, and v3.3.x. However, `.wal` sidecar files are **no longer read or written** — see the upgrade notes below before migrating.

#### Upgrading from 3.x

Before installing 4.0.1, run your 3.2.x application once with the existing data so that any pending changes in `.wal` sidecar files are flushed into the main `.vdb` snapshot via the previous WAL compaction path:

```csharp
// Run once on 3.2.x before upgrading:
await using var db = new MyDb();
await db.LoadAsync();      // replays any pending .wal entries into memory
await db.SaveAsync();      // writes a full snapshot and clears the WAL
```

After this step, the `.wal` file is empty/obsolete and it is safe to upgrade to 4.0.1. Upgrading without doing this will cause any unflushed WAL entries to be silently discarded on load.

#### Breaking Changes

| Change | Before (≤ 3.2.1) | After (4.0.1) |
|--------|------------------|---------------|
| **WAL (Write-Ahead Log) removed** | `QuiverDbOptions.EnableWal` / `WalCompactionThreshold` / `WalFlushToDisk` enabled incremental persistence via a `.wal` sidecar file. `SaveChangesAsync()` appended deltas; `LoadAsync()` replayed them. | The entire WAL subsystem is removed. `QuiverDbOptions` no longer exposes WAL options. `WriteAheadLog`, `WalEntry`, and the `_changeLog` queue inside `QuiverSet<T>` no longer exist. `SaveChangesAsync()` is removed — call `SaveAsync()` for a full atomic snapshot save instead. `LoadAsync()` loads the snapshot only. |
| **Snapshot alias APIs removed** | `QuiverDbContext.RewriteAsync()` and `CompactAsync()` were aliases for full-snapshot persistence/compaction. | Both aliases are removed. Call `SaveAsync(path?)` directly for a full atomic snapshot and periodic multi-segment compaction. |
| **Rationale** | WAL doubled memory peak under heavy writes (`_changeLog` held a strong reference to every queued entity in addition to the live cache, on top of the full vector copies inside index stores). | Snapshot-only persistence plus `AppendAsync()` and payload-level memory modes for vectors and large fields give a much flatter memory profile during bulk ingestion. |

#### Migration

```csharp
// Before 4.0.1
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    EnableWal = true,                 // ← remove
    WalCompactionThreshold = 10_000,  // ← remove
    WalFlushToDisk = true             // ← remove
};

// Before 4.0.1
await db.SaveChangesAsync();          // ← replace with SaveAsync()

// After 4.0.1
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb"
};
await db.SaveAsync();
```

If you previously relied on `SaveChangesAsync()` being incremental, use `AppendAsync()` for batch ingest and call `SaveAsync()` periodically to defragment multi-segment files.

> **Schema migration during offline format upgrades**: `ConfigureMigration<T>()` is applied by `QuiverDbContext.LoadAsync()` when reading a supported runtime format. If you upgrade a v1/v2/v3 file with `QuiverMigrator.MigrateAsync`, pass the same schema rules through its `migrationRules` parameter; otherwise renamed fields may be skipped while the old file is decoded.

```csharp
using Vorcyc.Quiver.Migration;

var rule = MigrationBuilder<Document>.Build(m => m
    .RenameProperty("OldTitle", "Title"));

await QuiverMigrator.MigrateAsync(
    sourceFile: "old.vdb",
    destinationFile: "data.vdb",
    typeMap: new Dictionary<string, Type>
    {
        [typeof(Document).FullName!] = typeof(Document)
    },
    migrationRules: new Dictionary<string, SchemaMigrationRule>
    {
        [typeof(Document).FullName!] = rule
    });
```

#### New: v4 file format (`QDB\x04`) — segment + footer + per-segment CRC32

4.0.1 uses the v4 on-disk binary format. v1/v2/v3 files remain readable through the migration path; new writes always produce v4.

```
[Magic "QDB\x04"][HeaderLen u32][Header bytes]
[Segment 1] [Segment 2] ... [Segment N]
[FooterTopMagic "QDBF"][SegmentCount u32]
  for each: [TypeName][Offset u64][Length u64][EntityCount u32][CRC32 u32]
[FooterOffset u64][TrailerMagic "QDBE"]
```

This unlocks three file-level capabilities without reintroducing WAL:

| API | Behavior | Cost |
|---|---|---|
| `QuiverDbContext.AppendAsync()` | Appends current in-memory entities as a **new** segment to an existing v4 file; rewrites only the footer. | O(Δ) bytes. Truly incremental — replaces the use case WAL covered, without the memory doubling. |
| `QuiverDbContext.SaveAsync()` | Writes a full snapshot and defragments a multi-segment file into one segment. | O(N). Run periodically. |
| `QuiverDbFile.MergeAsync(sources, dest, options, typeMap?)` | Merges multiple v4 files. `MergeConflictPolicy.Append` is a pure byte-copy of segments. `LastWriterWins` / `FirstWriterWins` deduplicate by `[QuiverKey]`. | Append: O(I/O), no decode. LWW/FWW: decode-and-rewrite. |
| `QuiverDbFile.InspectAsync(path, verifyCrc)` | Returns `QuiverFileInfo` (version, segments, per-segment CRC validation, per-type entity counts). | O(file size) when verifying CRC. |

```csharp
// Incremental bulk ingest — replaces the pre-4.0 SaveChangesAsync workflow.
// Use synchronous `using`; `await using` runs DisposeAsync(), which performs a final full SaveAsync().
using var db = new MyDb("data.vdb");
await db.LoadAsync();
db.Faces.AddRange(batch);
await db.AppendAsync();              // O(batch) write, no full rewrite

// Periodic defrag
await db.SaveAsync();

// Merge several archive files into one, dedup by [QuiverKey], last writer wins
var typeMap = new Dictionary<string, Type>
{
    [typeof(FaceFeature).FullName!] = typeof(FaceFeature)
};
await QuiverDbFile.MergeAsync(
    sourceFiles: ["a.vdb", "b.vdb", "c.vdb"],
    destinationFile: "merged.vdb",
    options: new MergeOptions { ConflictPolicy = MergeConflictPolicy.LastWriterWins },
    typeMap: typeMap);

// Diagnostics
var info = await QuiverDbFile.InspectAsync("merged.vdb");
Console.WriteLine($"v{info.FormatVersion}, {info.Segments.Count} segments, crcValid={info.CrcValid}");
```

> **Note**: All sections below describing WAL, `SaveChangesAsync`, `EnableWal`, `WriteAheadLog`, `WalEntry`, or `.wal` sidecar files refer to the pre-4.0 architecture and are kept only for historical reference. They no longer reflect runtime behavior.

---

### Project Background

#### Creation Overview

The inspiration for creating Quiver can be traced back to my development of the `Vorcyc.AwesomeAI.Ash` class, which provided simple vector storage and retrieval functionality to meet some lightweight semantic search needs. Although Ash pursued minimalism and ease of use in its design, as application scenarios evolved, its design bottlenecks became increasingly apparent:

- **Non-customizable table structure** — `Ash`'s storage architecture was internally fixed by the framework. Users could only access data according to a preset field layout and could not freely define entity properties and structures based on business requirements. This limitation was particularly prominent when designing differentiated data models for different scenarios (such as face recognition, document retrieval, multimodal search).
- **Only brute-force search supported** — `Ash`'s retrieval method was brute-force search, traversing each record and computing similarity one by one, with time complexity O(n*d). While acceptable for small data volumes, search latency increased dramatically when vector scale grew to tens or even hundreds of thousands. The lack of Approximate Nearest Neighbor (ANN) index support made it unsuitable for production scenarios requiring fast response times.
- **No concurrent operations supported** — `Ash`'s internal data structures had no thread synchronization protection. Performing read and write operations simultaneously in a multi-threaded environment would cause data races and unpredictable exceptions. For server-side scenarios requiring concurrent queries (such as ASP.NET Web API handling multiple search requests simultaneously), users had to add their own external locks, which increased usage complexity and easily led to performance bottlenecks or deadlock risks due to improper lock granularity.

While reflecting on these pain points, EF Core's design philosophy provided key inspiration — especially its "Code-First" concept: developers simply annotate entity class properties with attributes, and the framework automatically completes model discovery, relationship mapping, and data persistence, all in a declarative and non-intrusive manner.
Meanwhile, the Python library Annoy (Approximate Nearest Neighbors Oh Yeah) also provided inspiration, but its .NET wrapper HNSWSharp did not support a structured database-like design and only offered a single HNSW index type, lacking flexibility and diversity.

Therefore, I decided to design a brand-new vector database framework that would maintain EF Core-style ease of use and declarative modeling, support multiple ANN index algorithms to accommodate scenarios with different scales and performance requirements, and also include built-in concurrency safety mechanisms and efficient persistence solutions.

---

### Historical Versions

#### What's New in 3.2.1

> **File Format Compatibility**: v3.2.1 is fully backward-compatible with all previous data files (v1.x, v2.x, v3.0.0, v3.1.0, v3.2.0).

#### Bug Fixes

| Fix | Description |
|-----|-------------|
| **`EntityPageCache` thread-safety** | Fixed a data race in `LazyPaging` mode where concurrent readers (e.g., `Parallel.ForEach` calling `Find` / `Search` simultaneously) could corrupt the internal LRU state (`_loadedPages`, `_lru`, `_lruNodes`). All paths that mutate LRU state (`GetOrLoadPage`, `FlushDirty`, `CompactMemory`, `Clear`) are now protected by an internal `Lock (_pageLock)`. `FullMemory` mode is unaffected (zero overhead). |

---

#### Memory-Mapped Vectors, Payload Segments, Tombstones & Background Merge

> Builds on the v4 (`QDB\x04`) segment + footer format. Existing v4 files are read transparently; new writes extend the footer with schema v2 (per-segment `Kind` / `FieldName` / `Dim` / `FirstId`).

This update completes the v4 storage redesign by physically separating vectors and large fields from entity metadata, and replacing in-place delete with a tombstone + merge model. The goal is a flat managed-heap profile even with millions of high-dimensional vectors.

#### New: `VectorMemoryMode.MemoryMapped` — zero-copy vector access

The on-disk `VectorBlob` segment is mapped into the process via `MemoryMappedFile`; vectors are served from the OS page cache without copying into the managed heap.

```csharp
new QuiverDbOptions
{
    DatabasePath = "data.vdb",
    Vectors.MemoryMode = GlobalVectorMemoryMode.MemoryMapped, // InMemory (default) / LazyLoad / MemoryMapped / Auto / PerField
    Vectors.MemoryMapThresholdBytes = 256L * 1024 * 1024,      // Auto mode only: switch to mmap above this size
};
```

| Mode | Backend | When to pick |
|---|---|---|
| `InMemory` (default) | `HeapVectorStore` (`Dictionary<int, float[]>`) | Small / write-heavy datasets, no `DatabasePath` required |
| `MemoryMapped` | `MmapVectorStore` (read-only view over the v4 `VectorBlob` segment) | Large read-mostly datasets (face recognition, RAG indexes), bounded managed heap |
| `Auto` | InMemory below `Vectors.MemoryMapThresholdBytes`, MemoryMapped above | Mixed workloads |

`SaveAsync` / `AppendAsync` automatically dispose mmap views before the file is replaced and re-bind to the new `VectorBlob` regions afterwards — the lifecycle is fully hidden from the caller.

#### New: non-InMemory `[QuiverVector]` properties (source-generated)

Vectors are materialized from mmap only when an entity property is actually read. Declare the property as `partial`; the `Vorcyc.Quiver.SourceGenerators` analyzer emits a getter that calls `LazyVectorAccessor.Materialize(this, "PropertyName")`.

```csharp
public partial class AudioEntity
{
    [QuiverKey]
    public string Id { get; set; } = "";

    [QuiverVector(1024, DistanceMetric.Cosine, Nullable = true, MemoryMode = VectorMemoryMode.MemoryMapped)]
    public partial float[]? Embedding { get; set; }   // backing field + getter are generated
}
```

Search hot paths still read vectors directly from the mmap region (zero allocation). User code that touches `entity.Embedding` triggers a one-shot copy out of the mapped view.

Lazy vector source generation requires the vector property and every containing type in its nesting chain to be `partial`, and the property type must be `float[]` or `float[]?`. Invalid declarations produce analyzer diagnostics: `QVR001` (property is not partial), `QVR002` (containing type chain is not fully partial), or `QVR003` (invalid property type).

> Add the analyzer to consuming projects:
> ```xml
> <ProjectReference Include="..\Vorcyc.Quiver.SourceGenerators\Vorcyc.Quiver.SourceGenerators.csproj"
>                   OutputItemType="Analyzer"
>                   ReferenceOutputAssembly="false" />
> ```

#### New: `[QuiverLargeField]` — large `byte[]` fields split into their own segment

Inline `byte[]` fields (thumbnails, raw audio, packed features…) used to fatten the `EntityMeta` segment and inflate working-set memory on load. Annotate them with `[QuiverLargeField]` and they are written to a separate `SegmentKind.Blob` segment.

```csharp
public partial class Photo
{
    [QuiverKey] public string Id { get; set; } = "";
    [QuiverVector(512, DistanceMetric.Cosine, Nullable = true, MemoryMode = VectorMemoryMode.MemoryMapped)] public partial float[]? Embedding { get; set; }
    [QuiverLargeField(Nullable = true, MemoryMode = LargeFieldMemoryMode.PagedCache)] public partial byte[]? Thumbnail { get; set; }   // ← lives in its own Blob segment
}
```

`[QuiverLargeField]` may only be applied to `byte[]` and is mutually exclusive with `[QuiverVector]`.

#### New: Tombstone segments + `FlushTombstonesAsync()`

Deletes followed by `AppendAsync()` previously had no on-disk representation. The v4 format adds `SegmentKind.Tombstone` segments listing dead internal-row ids; loaders filter them out before handing entities to the set.

```csharp
await using var db = new MyDb("data.vdb");
await db.LoadAsync();

db.Faces.RemoveByKey("F0001");
db.Faces.RemoveByKey("F0002");

// Writes ONLY a Tombstone segment — does NOT re-append the live in-memory entities as new segments.
await db.FlushTombstonesAsync();
```

| API | Writes | Use case |
|---|---|---|
| `AppendAsync()` | New `EntityMeta` / `VectorBlob` / `Blob` segments for **all** current in-memory entities, plus a Tombstone segment for any pending deletes. | Bulk ingest of new entities + opportunistic delete flushing. |
| `FlushTombstonesAsync()` | **Only** a Tombstone segment. | Load → mutate-in-place → flush deletes without re-writing live rows. |
| `SaveAsync()` | Single defragmented snapshot. All prior tombstones are physically dropped. | Periodic compaction. |

#### New: Background auto-merge

`QuiverDbOptions` gains three knobs that drive an inline best-effort merge after every `AppendAsync` / `FlushTombstonesAsync`:

| Option | Default | Purpose |
|---|---|---|
| `EnableBackgroundMerge` | `false` | Master switch. |
| `AutoMergeMaxSegments` | `32` | Trigger a `SaveAsync()` once the footer contains at least this many segments. |
| `AutoMergeTombstoneRatio` | `0.25` | Trigger once `tombstones / live ≥ ratio`. |

Failures inside auto-merge are swallowed — they never propagate out of the user's `AppendAsync` call.

#### `QuiverDbFile.InspectAsync()` now reports per-segment `Kind` / `FieldName` / `Dim`

`SegmentInfo` exposes the new `Kind` (Mixed / EntityMeta / VectorBlob / Blob / Tombstone), `FieldName`, and `Dim` columns. Entity counts are no longer double-counted when a type spans multiple `VectorBlob` / `Blob` / `Tombstone` segments.

#### Footer schema v2

```
[FooterTopMagic "QDB2"][SegmentCount u32]
  for each entry:
    [TypeName s][Offset u64][Length u64][EntityCount u32][CRC32 u32]
    [Kind u8][FieldName s][Dim i32][FirstId i32]
[FooterOffset u64][TrailerMagic "QDBE"]
```

`"QDBF"` (v1) is still read; new files always emit `"QDB2"`.

---

#### Vector Quantization, Matryoshka Truncation & Runtime Heap → Mmap Promotion

> Builds on the segmented file format and mmap vector storage. Existing raw-float32 `VectorBlob` segments remain readable; new writes carry a per-segment `VectorBlobEncoding` byte plus an optional SQ8 scale table. Index topology and public APIs are unchanged.

This update focuses on keeping disk size, managed-heap size, and runtime working set bounded **without knowing the source embedding model**: field-level quantization and Matryoshka truncation compress vectors on the I/O path, and a heap-byte budget drives automatic Heap → Mmap promotion at runtime.

#### New: per-field vector quantization (`[QuiverVector(..., Quantization = ...)]`)

`QuiverVectorAttribute` gains a `Quantization` property. Two encodings are supported today:

| Value | On-disk size | Notes |
|---|---|---|
| `VectorQuantization.None` (default) | `dim × 4B` | Raw float32, identical to previous v4 raw-vector segments |
| `VectorQuantization.Sq8` | `dim × 1B + 4B scale` | Per-row SQ8 scalar quantization (int8 + single scale). ≈ 1/4 of raw size on disk. Search-side decode goes through `Sq8Codec.DecodeRow` into a thread-local buffer, zero allocation. |

```csharp
public partial class FaceFeature
{
    [QuiverKey] public string Id { get; set; } = "";

    // SQ8 + Matryoshka: a 1024-dim embedding is indexed/searched on its first 512 dims;
    // on-disk size ≈ 1024×1B + 4B.
    [QuiverVector(1024, DistanceMetric.Cosine,
                  Nullable = true,
                  MemoryMode = VectorMemoryMode.MemoryMapped,
                  Quantization = VectorQuantization.Sq8,
                  EffectiveDimensions = 512)]
    public partial float[]? Embedding { get; set; }
}
```

Encoding is persisted per segment in the v4 `VectorBlob` header (`VectorBlobEncoding` enum + version byte). `MmapVectorStore` and `BinaryStorageProvider` decode each segment using its own metadata, so the upstream embedding model is allowed to be unknown.

#### New: Matryoshka truncation (`EffectiveDimensions`)

`QuiverVectorAttribute.EffectiveDimensions` lets you index/search on only the first N dims of a vector without touching the source embedding:

- **Write path** — when `EffectiveDimensions < Dimensions`, `PrepareVectors` copies the first N dims into a fresh array (without mutating the entity's own array) and optionally L2-normalizes before storing/indexing.
- **Query path** — `Search` / `SearchKnn` apply the same truncation and normalization to the query vector so the query geometry matches the store geometry.
- **Index topology** — all index implementations (Flat / HNSW / IVF / KDTree) operate on `EffectiveDimensions`; distance-computation cost drops linearly.

Designed for Matryoshka-style embeddings (OpenAI `text-embedding-3-large`, Nomic, …) and for two-stage "low-dim recall + full-dim rerank" pipelines.

#### New: heap-byte budget + runtime InMemory → MemoryMapped auto-promotion

`QuiverDbOptions` gains two runtime memory controls:

| Option | Default | Purpose |
|---|---|---|
| `Vectors.MaxInMemoryBytes` | `0` (disabled) | Per-`QuiverSet` upper bound on in-memory vector payload bytes. |
| `Vectors.AutoPromoteToMemoryMapped` | `false` | When the budget is exceeded, automatically promote the set's in-memory vector stores to mmap. |

Flow:

1. `QuiverSet`'s `Add` / `AddRange` / `Upsert` write paths call `NotifyHeapBytes()` at the tail of the write lock, reporting the sum of `IVectorStore.HeapByteSize` to `QuiverDbContext`.
2. `QuiverDbContext` (implementing the internal `IPromotionCoordinator`) checks `Vectors.AutoPromoteToMemoryMapped && bytes ≥ Vectors.MaxInMemoryBytes && DatabasePath != null` and uses a CAS gate to single-flight one promotion task per entity type.
3. The background task runs `SaveAsync()` (to guarantee on-disk = in-memory), then promotes each InMemory vector field by `QuiverSet.PromoteFieldsToMmap(...)`, binding fresh mmap views over the new `VectorBlob` segments.
4. The swap goes through a new `VectorStoreSlot` indirection — indices keep their stable slot reference, **no index rebuild is required**, and the search hot path is never interrupted.

```csharp
new QuiverDbOptions
{
    DatabasePath = "audio.vdb",
    Vectors.MemoryMode = GlobalVectorMemoryMode.InMemory,
    Vectors.MaxInMemoryBytes = 512L * 1024 * 1024,
    Vectors.AutoPromoteToMemoryMapped = true,
};
```

Promotion failures (e.g. disk unwritable) are logged via `Trace.TraceWarning` and the in-flight flag is cleared. They never propagate out of the user's write call.

#### Public API additions

| Member | Namespace | Notes |
|---|---|---|
| `VectorQuantization` enum | `Vorcyc.Quiver.Quantization` | Field-level quantization strategy. |
| `VectorBlobEncoding` enum | `Vorcyc.Quiver.Storage` | `VectorBlob` segment encoding version. |
| `Sq8Codec` | `Vorcyc.Quiver.Storage` | SQ8 row encode/decode with thread-local buffers. |
| `IVectorStore.HeapByteSize` | `Vorcyc.Quiver.Indexing` | Managed-heap bytes currently held by the store. |
| `IVectorStore.EffectiveDim` | `Vorcyc.Quiver.Indexing` | Dimension actually used by index/search. |
| `QuiverDbOptions.Vectors.MaxInMemoryBytes` | same | In-memory vector byte budget. |
| `QuiverDbOptions.Vectors.AutoPromoteToMemoryMapped` | same | Master switch for auto-promotion. |
| `QuiverVectorAttribute.Quantization` | `Vorcyc.Quiver` | Field quantization strategy. |
| `QuiverVectorAttribute.EffectiveDimensions` | same | Matryoshka truncation target. |

#### Compatibility

- **File format** — v4 `VectorBlob` segments now carry an encoding byte + optional SQ8 scale region, still embedded in the `QDB\x04` container and footer schema v2. Existing raw-float32 segments are transparently read.
- **Public API** — `QuiverDbOptions`, `QuiverVectorAttribute`, `IVectorStore`, `QuiverSet<T>` only gain additive members; existing call sites compile unchanged.
- **Indexes** — `VectorStoreSlot` is an internal wrapper inside `QuiverSet<T>`; index implementations are unaware of the swap.

---

#### HNSW Snapshot Persistence & Mmap Load Fixes

This update removes the need to rebuild large HNSW graphs on every load. `SaveAsync()` writes an optional `SegmentKind.IndexSnapshot` segment for indexes that support snapshots; `LoadAsync()` restores the topology first and only replays ids not covered by the snapshot.

#### New: HNSW index snapshots

The HNSW snapshot stores the entry point, max level, node levels, per-layer neighbor lists, and the covered `NextId`, avoiding the O(N log N) graph rebuild normally caused by replaying `Add(id)` for every vector. For large vector sets, load cost shifts from “rebuild the graph” to “read and deserialize topology”.

Snapshots carry fingerprints for similarity type, HNSW parameters, and effective dimension. If the runtime model, dimension, effective dimension after quantization/truncation, or index parameters do not match, the loader rejects the snapshot and automatically falls back to the previous rebuild path. Old files without `IndexSnapshot` segments remain fully readable.

#### Fixed: mmap vector load ordering and stable type names

When `Vectors.MemoryMode = MemoryMapped / Auto`, the load pipeline now binds `VectorBlob` regions to `MmapVectorStore` before replaying ids not covered by a snapshot. This prevents HNSW from dereferencing mmap vectors too early and throwing `KeyNotFoundException: Vector id ... not found in mmap store.`

Mmap region matching also accepts both `[QuiverEntity("stable-name")]` and the legacy `Type.FullName` alias, so adding a stable entity name no longer causes old v4 vector regions to be skipped silently.

#### Compatibility

- **File format** — adds an optional `SegmentKind.IndexSnapshot` segment. Existing v4 files load unchanged; index types without snapshot support keep rebuilding normally.
- **Lazy loading** — non-InMemory vector materialization and `[QuiverLargeField]` large-object loading are unaffected. The snapshot stores index topology only, not entity or vector copies.
- **Mmap** — snapshot restore and mmap binding remain separate; search hot paths still read vectors directly from mmap.

---

#### What's New in 3.2.0

> **File Format Compatibility**: v3.2.0 is fully backward-compatible with v1.x, v2.x, v3.0.0, and v3.1.0 data files.

#### New Features

| Feature | Description |
|---------|-------------|
| **`CompactMemory()` / `CompactMemoryAsync()`** | Flushes all dirty pages to disk and evicts every loaded page from memory on demand, minimizing the working-set footprint. Exposed on `QuiverSet<T>` (per-collection) and as `CompactAllMemoryAsync()` on `QuiverDbContext` (all collections at once). No-op in `FullMemory` mode. Vector index structures are unaffected. |

---

#### What's New in 3.1.0

> **File Format Compatibility**: v3.1.0 is fully backward-compatible with v1.x, v2.x, and v3.0.0 data files.

#### Breaking Changes

| Change | Before (v3.0.0) | After (v3.1.0) |
|--------|-----------------|----------------|
| **`VectorStorageMode` removed** | `QuiverDbOptions.VectorStorage = VectorStorageMode.MemoryMapped` — optional memory-mapped vector arena via `MmapVectorStore` | Removed entirely. Vectors are always stored on the GC heap (`HeapVectorStore`). The `LazyPaging` entity cache already bounds total memory; a separate mmap layer is no longer needed. |
| **`QuiverSet` constructor simplified** | Accepted `DistanceMetric defaultMetric` as a parameter | The `defaultMetric` parameter is removed. Each vector field independently declares its metric via `[QuiverVector(dim, metric)]`. |

#### Migration from v3.0.0

If you previously set `VectorStorage = VectorStorageMode.MemoryMapped` in your `QuiverDbOptions`, simply remove that line — no other changes are required. Data files remain fully compatible.

```csharp
// v3.0.0 (remove the VectorStorage line)
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    // VectorStorage = VectorStorageMode.MemoryMapped,  ← remove this
    EntityCache = EntityCacheMode.LazyPaging,
    MaxCachedPages = 32,
    PageSize = 512
};
```

##### What's New in 3.1.0

> Same lazy-loading page cache features as 3.0.0, now with a simpler and more consistent architecture.

#### New Features

| Feature | Description |
|---------|-------------|
| **Lazy-loading page cache** | `EntityCache = EntityCacheMode.LazyPaging` — entity objects are no longer fully resident in memory. They are split into fixed-size pages (`PageSize` entities/page), loaded on demand, and evicted via LRU when `MaxCachedPages` is exceeded. Idle cold pages are serialized to binary `.qvpg` page files and read back only when accessed. |
| **Controllable memory ceiling** | Actual entity memory usage is bounded by `MaxCachedPages × PageSize × entity size` regardless of total dataset size. |
| **Vector indexes remain resident** | HNSW / IVF / KDTree index structures always stay in memory, so search performance is unaffected by lazy-loading. |
| **`IsLazyLoading` property** | `QuiverSet<T>.IsLazyLoading` exposes the current caching mode for diagnostics. |
| **Transparent API** | `EntityPageCache<T>` presents the same interface as the previous `Dictionary<int, TEntity>` — zero changes required in calling code. |

#### New Configuration Options (`QuiverDbOptions`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EntityCache` | `EntityCacheMode` | `FullMemory` | Entity caching mode: `FullMemory` (all entities in memory) / `LazyPaging` (LRU page cache). Requires `DatabasePath` for `LazyPaging`. |
| `MaxCachedPages` | `int` | `16` | Max pages kept in memory per `QuiverSet`. |
| `PageSize` | `int` | `512` | Max entities per page. |

#### Quick Start — Lazy-Loading Mode

```csharp
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    EntityCache = EntityCacheMode.LazyPaging,  // ← enable lazy paging
    MaxCachedPages = 32,                       // at most 32 pages in memory
    PageSize = 512                             // 512 entities per page
    // memory ceiling ≈ 32 × 512 × entity size
};
```

> Page files are stored under `{DatabasePath}.pages/{EntityTypeName}/page_XXXXXXXX.qvpg` (custom binary format, no external dependencies).
>
> **Page file binary layout (v1)**:
> ```
> [4B uint32]  Magic = 0x51565047  ("QVPG" identifier)
> [1B byte]    Version = 0x01
> [4B int32]   PropCount            ← number of property descriptors
> PropDescriptor × PropCount:
>   [string]   PropName             ← BinaryWriter length-prefixed UTF-8
> [4B int32]   EntityCount          ← entities in this page
> Entity × EntityCount:
>   [4B int32] InternalId
>   per-field (descriptor order): [1B bool isNotNull] + value
>                                 (same type encoding as BinaryStorageProvider)
> ```

---

#### What's New in 3.0.0

> **File Format Compatibility**: v3.0.0 is fully backward-compatible with v1.x and v2.x data files.

| Feature | Description |
|---------|-------------|
| **Lazy-loading page cache** | `EntityCache = EntityCacheMode.LazyPaging` — entity objects loaded on demand in fixed-size pages, evicted by LRU when `MaxCachedPages` is exceeded. |
| **Controllable memory ceiling** | Actual entity memory usage is bounded by `MaxCachedPages × PageSize × entity size` regardless of total dataset size. |
| **Vector indexes remain resident** | HNSW / IVF / KDTree index structures always stay in memory; search performance is unaffected by lazy-loading. |
| **`IsLazyLoading` property** | `QuiverSet<T>.IsLazyLoading` exposes the current caching mode for diagnostics. |
| **Memory-mapped vector storage** | `VectorStorage = VectorStorageMode.MemoryMapped` (introduced in 3.0.0, **removed in 3.1.0** — see above). |

---

#### What's New in 2.0.0

> **File Format Compatibility**: v2.0.0 is fully backward-compatible with v1.x data files. All three storage formats (JSON / XML / Binary) and WAL files can be loaded without any migration.

#### Breaking Changes

| Change | Before (v1.x) | After (v2.0.0) |
|--------|---------------|----------------|
| Similarity computation | `SimilarityFunc` delegate | `ISimilarity<T>` static abstract interface — JIT generates specialized machine code per type, zero virtual dispatch |
| Vector data ownership | Each index stores vectors internally | `IVectorStore` abstraction — indexes only manage topology (graph/tree/inverted list), vectors unified by store |

#### New Features

| Feature | Description |
|---------|-------------|
| **6 new distance metrics** | Manhattan (L1), Chebyshev (L∞), Pearson correlation, Hamming, Jaccard, Canberra — plus the original 3 (Cosine / Euclidean / DotProduct), totaling 9 built-in metrics |
| **Custom similarity** | `[QuiverVector(128, CustomSimilarity = typeof(MySimilarity))]` — plug in any `ISimilarity<float>` struct |
| **IVectorStore abstraction** | `HeapVectorStore` (GC heap) — pluggable vector storage backend |

#### Performance Improvements

| Improvement | Details |
|-------------|--------|
| **SIMD for all metrics** | All 9 similarity implementations use internal `VectorMath` / `Vector<float>` paths, auto-adapting to SSE4 / AVX2 / AVX-512 register width without extra NuGet dependencies |
| **Zero-overhead dispatch** | `ISimilarity<T>` with `static abstract` + `readonly struct` enables JIT to inline `TSim.Compute()` at call sites — no delegate indirection |

---
