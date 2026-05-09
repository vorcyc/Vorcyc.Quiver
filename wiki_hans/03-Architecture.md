## 1. 架构概览

### 1.1 分层架构

```mermaid
graph TB
    subgraph 用户代码层
        Entity["Entity 实体<br/>[QuiverKey] [QuiverVector] [QuiverIndex]"]
        Context["MyDbContext : QuiverDbContext<br/>QuiverSet&lt;Entity&gt; Entities"]
    end

    subgraph QuiverDb 框架层
        VDC["QuiverDbContext<br/>• 自动发现 QuiverSet<br/>• SaveAsync / LoadAsync 全量持久化<br/>• AppendAsync / FlushTombstonesAsync 段追加<br/>• IDisposable / IAsyncDisposable"]
        VS["QuiverSet&lt;TEntity&gt;<br/>• CRUD（Add / Upsert / Remove）<br/>• Search（Top-K / 阈值 / 过滤）<br/>• ReaderWriterLockSlim 并发<br/>• Tombstone 缓冲区"]
    end

    subgraph 索引层 Indexing
        IVI["IVectorIndex 接口"]
        Flat["FlatIndex<br/>暴力搜索 O(n×d)<br/>100% 精确"]
        HNSW["HnswIndex<br/>分层图 O(log n)<br/>近似搜索首选"]
        IVF["IvfIndex<br/>K-Means 聚类 O(n/k)<br/>大数据量"]
        KDT["KDTreeIndex<br/>空间二分 O(log n)<br/>低维精确"]
    end

    subgraph 存储层 Storage
        BSP["BinaryStorageProvider<br/>v4 QDB\x04 段式格式<br/>主存储路径<br/>MemoryMarshal 零拷贝"]
        QF["QuiverDbFile<br/>InspectAsync / MergeAsync<br/>文件级工具"]
        MMR["MmapVectorRegion<br/>VectorBlob 上的只读 mmap 视图"]
        ISP["IStorageProvider 接口<br/>仅用于导出/导入"]
        JSON["JsonExportProvider<br/>ExportAsync / ImportAsync"]
        XML["XmlExportProvider<br/>ExportAsync / ImportAsync"]
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
    VDC --> QF
    VDC --> ISP
    BSP --> MMR
    ISP --> JSON
    ISP --> XML
```

### 1.2 核心组件总览

| 组件 | 类型 | 职责 |
|------|------|------|
| `QuiverDbContext` | `abstract class` | 数据库上下文基类，管理 QuiverSet 集合的反射自动发现、持久化读写、生命周期 |
| `QuiverSet<TEntity>` | `partial class` | 向量集合，实现 `IEnumerable<TEntity>`，提供完整 CRUD + 多种搜索模式 + `foreach` / LINQ 枚举，内部 `ReaderWriterLockSlim` 读写锁 |
| `IVectorIndex` | `internal interface` | 向量索引统一契约，定义 `Add` / `Remove` / `Clear` / `Search` / `SearchByThreshold` |
| `IStorageProvider` | `internal interface` | 导出/导入统一契约，支持 `SaveAsync` / `LoadAsync` |
| `ExportStorageProviderFactory` | `internal static class` | 工厂方法，根据 `ExportFormat` 枚举创建对应的 `IStorageProvider` 实例 |
| `QuiverVectorAttribute` | `Attribute` | 标记向量字段，指定维度 (`dimensions`)、距离度量 (`metric`)、是否可空 (`Nullable`) 和字段级内存模式 (`MemoryMode`) |
| `QuiverKeyAttribute` | `Attribute` | 标记实体主键（每个实体有且仅有一个） |
| `QuiverIndexAttribute` | `Attribute` | 配置索引类型及调优参数（可选，默认 Flat） |
| `QuiverDbOptions` | `class` | 全局配置：存储路径、默认度量、大字段内存模式、向量内存模式、后台合并阈值等 |
| `QuiverSearchResult<T>` | `record` | 搜索结果 DTO，包含 `Entity` 和 `Similarity` |
| `QuiverDbFile` | `public static class` | v4 文件级工具：`InspectAsync`（版本/段表/CRC 校验）、`MergeAsync`（多文件合并，支持 Append / FWW / LWW 三种冲突策略） |
| `MmapVectorRegion` | `internal sealed class` | 只读 `MemoryMappedFile` 视图，覆盖单个 `VectorBlob` 段；`MmapVectorStore` 的底层 |
| `MmapVectorStore` | `internal sealed class` | 通过 `MmapVectorRegion` 暴露向量数据的 `IVectorStore` 实现，`Vectors.MemoryMode = MemoryMapped / Auto` 时启用 |
| `LazyVectorAccessor` | `internal static class` | 源生成器生成的 `partial` 向量 getter 的运行时桥；通过 `ConditionalWeakTable` 把实体绑定到所属 `QuiverSet` 与内部 ID |
| `MigrationBuilder<T>` | `class` | Schema 迁移的流式 API 构建器（属性重命名 + 值转换） |
| `SchemaMigrationRule` | `internal class` | 存储单个实体类型的迁移规则：属性重命名映射 + 值转换函数 |
| `ISimilarity<T>` | `public interface` | 静态抽象相似度计算契约。JIT 为每个具体类型内联，零虚分派 |
| `IVectorStore` | `internal interface` | 向量数据存储抽象。将向量所有权从索引拓扑中剥离 |
| `HeapVectorStore` | `internal sealed class` | GC 堆向量存储（`Dictionary<int, float[]>`），唯一的向量存储后端 |
| `EntityPageCache<TEntity>` | `internal sealed class` | 懒加载 LRU 分页缓存。实体按需加载，冷页驱逐后序列化为 `.qvpg` 二进制页文件 |

### 1.3 类关系图

```mermaid
classDiagram
    class QuiverDbContext {
        <<abstract>>
        -Dictionary~Type, object~ _sets
        -Dictionary~string, Type~ _typeMap
        -IStorageProvider _storageProvider
        -QuiverDbOptions _options
        +Set~TEntity~() QuiverSet~TEntity~
        +SaveAsync(path?) Task
        +AppendAsync(path?) Task
        +FlushTombstonesAsync(path?) Task
        +LoadAsync(path?) Task
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
        +LargeFieldOptions LargeFields
        +VectorOptions Vectors
        +long Vectors.MemoryMapThresholdBytes
        +long Vectors.MaxInMemoryBytes
        +bool Vectors.AutoPromoteToMemoryMapped
        +bool EnableBackgroundMerge
        +int AutoMergeMaxSegments
        +double AutoMergeTombstoneRatio
    }

    class QuiverDbFile {
        <<static>>
        +MergeAsync(sources, destination, options?, typeMap?)$
        +InspectAsync(path, verifyCrc)$ QuiverFileInfo
    }

    class QuiverSearchResult~TEntity~ {
        <<record>>
        +TEntity Entity
        +float Similarity
    }

    QuiverDbContext o-- QuiverSet~TEntity~ : 包含 N 个
    QuiverDbContext --> IStorageProvider : 使用
    QuiverDbContext --> QuiverDbOptions : 配置
    QuiverDbContext ..> QuiverDbFile : 委托段合并/检视
    QuiverSet~TEntity~ --> IVectorIndex : 每个向量字段一个
    IVectorIndex <|.. FlatIndex
    IVectorIndex <|.. HnswIndex
    IVectorIndex <|.. IvfIndex
    IVectorIndex <|.. KDTreeIndex
    IStorageProvider <|.. JsonExportProvider
    IStorageProvider <|.. XmlExportProvider
    IStorageProvider <|.. BinaryStorageProvider
    QuiverSet~TEntity~ ..> QuiverSearchResult~TEntity~ : 返回
```

---

