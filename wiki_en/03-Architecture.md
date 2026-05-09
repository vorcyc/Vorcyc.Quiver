## 1. Architecture Overview

### 1.1 Layered Architecture

```mermaid
graph TB
    subgraph User Code Layer
        Entity["Entity<br/>[QuiverKey] [QuiverVector] [QuiverLargeField] [QuiverIndex]"]
        Context["MyDbContext : QuiverDbContext<br/>QuiverSet&lt;Entity&gt; Entities"]
    end

    subgraph QuiverDb Framework Layer
        VDC["QuiverDbContext<br/>* Auto-discover QuiverSet<br/>* SaveAsync / LoadAsync persistence<br/>* AppendAsync / FlushTombstonesAsync (v4)<br/>* IDisposable / IAsyncDisposable"]
        VS["QuiverSet&lt;TEntity&gt;<br/>* CRUD (Add / Upsert / Remove)<br/>* Search (Top-K / Threshold / Filtered)<br/>* ReaderWriterLockSlim concurrency<br/>* Pending tombstone tracking"]
    end

    subgraph Indexing Layer
        IVI["IVectorIndex Interface"]
        Flat["FlatIndex<br/>Brute-force O(n*d)<br/>100% exact"]
        HNSW["HnswIndex<br/>Layered graph O(log n)<br/>Approximate search preferred"]
        IVF["IvfIndex<br/>K-Means clustering O(n/k)<br/>Large datasets"]
        KDT["KDTreeIndex<br/>Spatial bisection O(log n)<br/>Low-dimensional exact"]
    end

    subgraph Storage Layer
        BSP["BinaryStorageProvider<br/>v4 QDB\x04 segmented format<br/>per-segment CRC32, MemoryMarshal zero-copy"]
        QDF["QuiverDbFile<br/>InspectAsync / MergeAsync<br/>file-level utilities"]
        MMR["MmapVectorRegion<br/>read-only MemoryMappedFile<br/>view over VectorBlob segment"]
        ISP["IStorageProvider Interface<br/>Export/Import only"]
        JSON["JsonExportProvider<br/>System.Text.Json<br/>Export/Import format"]
        XML["XmlExportProvider<br/>XDocument + Base64<br/>Export/Import format"]
    end

    subgraph Runtime / Source Generator
        LVA["LazyVectorAccessor<br/>ConditionalWeakTable bridge"]
        SG["VectorMemoryPropertyGenerator<br/>partial payload property emitter"]
    end

    Entity --> Context
    Context --> VDC
    VDC --> VS
    VS --> IVI
    IVI --> Flat
    IVI --> HNSW
    IVI --> IVF
    IVI --> KDT
    VDC --> BSP
    VDC --> ISP
    ISP --> JSON
    ISP --> XML
    BSP --> QDF
    BSP --> MMR
    VS --> LVA
    LVA --> SG
```

### 1.2 Core Components Overview

| Component | Type | Responsibility |
|-----------|------|----------------|
| `QuiverDbContext` | `abstract class` | Database context base class, manages automatic reflection discovery of QuiverSet collections, persistence read/write, lifecycle |
| `QuiverSet<TEntity>` | `partial class` | Vector collection, implements `IEnumerable<TEntity>`, provides full CRUD + multiple search modes + `foreach` / LINQ enumeration, internal `ReaderWriterLockSlim` reader-writer lock |
| `IVectorIndex` | `internal interface` | Unified vector index contract, defines `Add` / `Remove` / `Clear` / `Search` / `SearchByThreshold` |
| `IStorageProvider` | `internal interface` | Export/import serialization contract, supports `SaveAsync` / `LoadAsync`. Used only by `ExportAsync` / `ImportAsync` — primary storage always uses `BinaryStorageProvider` directly |
| `ExportStorageProviderFactory` | `internal static class` | Factory method, creates `JsonExportProvider` or `XmlExportProvider` based on `ExportFormat` enum |
| `QuiverVectorAttribute` | `Attribute` | Marks vector field, specifies dimensions (`dimensions`), distance metric (`metric`), nullable (`Nullable`), quantization/effective dimensions, and per-field memory mode |
| `QuiverKeyAttribute` | `Attribute` | Marks entity primary key (exactly one per entity) |
| `QuiverIndexAttribute` | `Attribute` | Configures index type and tuning parameters (optional, defaults to Flat) |
| `QuiverLargeFieldAttribute` | `Attribute` | Marks a `byte[]` field as a large field, written into a dedicated `Blob` segment instead of `EntityMeta` |
| `QuiverDbOptions` | `class` | Global configuration: storage path, default metric, vector/large-field memory modes, mmap thresholds, background-merge thresholds |
| `QuiverSearchResult<T>` | `record` | Search result DTO, contains `Entity` and `Similarity` |
| `QuiverDbFile` | `static class` | v4 file-level utilities: `InspectAsync` (version / segment table / per-segment CRC32) and `MergeAsync` (Append / FirstWriterWins / LastWriterWins) |
| `MigrationBuilder<T>` | `class` | Fluent API builder for Schema migration rules (property rename + value transform) |
| `SchemaMigrationRule` | `internal class` | Stores migration rules for a single entity type: property rename map + value transform functions |
| `ISimilarity<T>` | `public interface` | Static abstract similarity computation contract. JIT-inlined per concrete type, zero virtual dispatch |
| `IVectorStore` | `internal interface` | Vector data storage abstraction. Decouples vector ownership from index topology; includes `StoreByRef` zero-copy ingestion |
| `HeapVectorStore` | `internal sealed class` | Default GC-heap vector store (`Dictionary<int, float[]>`) |
| `MmapVectorStore` | `internal sealed class` | Read-only mmap vector store backed by `MmapVectorRegion` over the v4 `VectorBlob` segment; selected via `Vectors.MemoryMode = MemoryMapped / Auto` |
| `LazyVectorAccessor` | `static runtime class` | Runtime bridge for source-generated lazy vector properties; uses `ConditionalWeakTable` to weakly bind entity → owning `QuiverSet` |
| `LazyLargeFieldAccessor` | `static runtime class` | Runtime bridge for source-generated large-field properties; materializes `byte[]` payloads on demand |
| `InMemoryEntityStore<TEntity>` | `internal sealed class` | Entity object store used by `QuiverSet<TEntity>`; payload memory control is handled by vector and large-field stores |

### 1.3 Class Relationship Diagram

```mermaid
classDiagram
    class QuiverDbContext {
        <<abstract>>
        -Dictionary~Type, object~ _sets
        -Dictionary~string, Type~ _typeMap
        -IStorageProvider _storageProvider
        -QuiverDbOptions _options
        +Set~TEntity~() QuiverSet~TEntity~
        +LoadAsync(path?) Task
        +SaveAsync(path?) Task
        +AppendAsync(path?) Task
        +FlushTombstonesAsync(path?) Task
        +ExportAsync(path, format) Task
        +ImportAsync(path, format) Task
        +Dispose()
        +DisposeAsync() ValueTask
        #ConfigureMigration~TEntity~(configure) void
        -InitializeSets()
    }

    class MigrationBuilder~TEntity~ {
        +RenameProperty(oldName, newName) MigrationBuilder
        +TransformValue(propName, transform) MigrationBuilder
    }

    class SchemaMigrationRule {
        +Dictionary PropertyRenames
        +Dictionary ValueTransforms
        +Dictionary ReverseRenames
    }

    class QuiverSet~TEntity~ {
        <<IEnumerable~TEntity~>>
        -Dictionary~int, TEntity~ _entities
        -Dictionary~object, int~ _keyToId
        -FrozenDictionary~string, QuiverFieldInfo~ _vectorFields
        -FrozenDictionary~string, Func~ _vectorGetters
        -FrozenDictionary~string, IVectorIndex~ _indices
        -List _changeLog
        -ReaderWriterLockSlim _lock
        -int _nextId
        +int Count
        +IReadOnlyDictionary VectorFields
        +GetEnumerator() IEnumerator~TEntity~
        +Add(entity)
        +AddRange(entities)
        +Upsert(entity)
        +Remove(entity) bool
        +RemoveByKey(key) bool
        +Find(key) TEntity?
        +Exists(key) bool
        +Exists(predicate) bool
        +Clear()
        +Search(...) List~QuiverSearchResult~
        +SearchTop1(...) QuiverSearchResult?
        +SearchByThreshold(...) List~QuiverSearchResult~
    }

    class IVectorIndex {
        <<interface>>
        +int Count
        +Add(id, vector)
        +Remove(id)
        +Clear()
        +Search(query, topK) List
        +SearchByThreshold(query, threshold) List
    }

    class FlatIndex {
        -Dictionary~int, float[]~ _vectors
        -SimilarityFunc similarityFunc
        -SequentialSearchCore()
        -ParallelSearchCore()
    }

    class HnswIndex {
        -Dictionary~int, HnswNode~ _nodes
        -int _entryPointId
        -int _maxLevel
        -int _m, _mMax0
        -int _efConstruction, _efSearch
        +int EfSearch
    }

    class IvfIndex {
        -Dictionary~int, float[]~ _vectors
        -float[][] _centroids
        -List~int~[] _invertedLists
        -int _numClusters, _numProbes
        -Build()
        -KMeansPlusPlusInit()
    }

    class KDTreeIndex {
        -Dictionary~int, float[]~ _vectors
        -KDNode? _root
        -BuildTree()
        -SearchNode()
    }

    class IStorageProvider {
        <<interface>>
        +SaveAsync(filePath, sets) Task
        +LoadAsync(filePath, typeMap) Task
    }

    class QuiverDbOptions {
        +string? DatabasePath
        +DistanceMetric DefaultMetric
        +VectorMemoryMode VectorMemoryMode
        +long Vectors.MemoryMapThresholdBytes
        +LargeFieldMemoryMode LargeFieldMemoryMode
        +int LargeFields.MaxCachedPayloads
        +bool EnableBackgroundMerge
        +int AutoMergeMaxSegments
        +double AutoMergeTombstoneRatio
    }

    class QuiverDbFile {
        <<static>>
        +InspectAsync(path, verifyCrc) Task~FileInfo~
        +MergeAsync(sources, dest, options?, typeMap?) Task
    }

    class QuiverSearchResult~TEntity~ {
        <<record>>
        +TEntity Entity
        +float Similarity
    }

    QuiverDbContext o-- QuiverSet~TEntity~ : contains N
    QuiverDbContext --> IStorageProvider : uses
    QuiverDbContext --> QuiverDbOptions : configured by
    QuiverDbContext ..> QuiverDbFile : file-level utilities
    QuiverSet~TEntity~ --> IVectorIndex : one per vector field
    IVectorIndex <|.. FlatIndex
    IVectorIndex <|.. HnswIndex
    IVectorIndex <|.. IvfIndex
    IVectorIndex <|.. KDTreeIndex
    IStorageProvider <|.. JsonExportProvider
    IStorageProvider <|.. XmlExportProvider
    QuiverSet~TEntity~ ..> QuiverSearchResult~TEntity~ : returns
```

---

