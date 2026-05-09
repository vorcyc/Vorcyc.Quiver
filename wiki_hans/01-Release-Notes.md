# 发行说明（Release Notes）— Vorcyc Quiver 4.0.1

![Vorcyc Quiver 4.0.1](logo.jpg "Vorcyc Quiver 4.0.1")

> **产品定位**：纯 .NET 实现的嵌入式向量数据库 —— 零原生依赖，进程内运行，无需独立部署数据库服务器  
> **框架版本**：.NET 10  
> **命名空间**：`Vorcyc.Quiver`  
> **设计理念**：类似 EF Core 的 `DbContext` 模式，通过声明式属性标记实现向量数据库的自动发现、索引构建和持久化  
> **核心特性**：Code-First 声明式实体定义 · 多种 ANN 索引（Flat / HNSW / IVF / KDTree） · 9 种内置距离度量 + 自定义相似度 · 二进制主存储 + JSON/XML 导出/导入 · Schema Migration（属性重命名 / 值转换） · 读写分离锁并发安全 · SIMD 加速相似度计算 · 向量内存模式（InMemory / MemoryMapped / Auto / PerField）
> **关键字**：`嵌入式向量数据库` `纯 .NET` `ANN` `近似最近邻搜索` `相似度检索` `HNSW` `IVF` `KDTree` `Code-First` `EF Core 风格` `Embedding` `语义搜索` `人脸识别` `以图搜图` `RAG` `SIMD` `Schema Migration` `ISimilarity` `自定义度量`
> **释名**：Quiver —— 箭袋，装箭（Arrow）的容器，向量的数学本质就是箭头

---

### 4.0.1 变更说明：内存模型与实体缓存 API 重构

本次变更不保留旧 API 兼容层。内存管理语义从“实体分页缓存”改为“按载荷类型管理”：实体始终直接保存在托管内存中；需要控制内存占用的是向量载荷和大字段载荷。

#### 已移除

| 移除项 | 说明 |
|---|---|
| `EntityCacheMode` / 实体分页缓存 | 删除实体级 LazyPaging / LRU page-cache 抽象，实体集合改为直接内存字典存储。 |
| `QuiverDbOptions.MaxCachedPages` / `PageSize` | 实体分页缓存已删除，这两个配置项不再存在。 |
| `QuiverSet<TEntity>.IsLazyLoading` | 不再存在实体懒加载模式标志。 |
| `QuiverSet<TEntity>.CompactMemory()` / `CompactMemoryAsync()` | 实体页逐出能力已删除。 |
| `QuiverDbContext.CompactAllMemoryAsync()` | 上下文级实体页压缩 API 已删除。 |
| `QuiverDbContext.RewriteAsync()` / `CompactAsync()` | 快照重写/碎片整理别名已删除；请直接调用 `SaveAsync(path?)` 完成全量原子快照和周期性多段整理。 |
| `QuiverBlobAttribute` | 更名为 `QuiverLargeFieldAttribute`。 |
| `VectorStoreMode` 及 `VectorStore` 配置命名 | 更名为 `VectorMemoryMode` / `VectorMemoryMode` 配置，语义聚焦“向量载荷内存策略”。 |
| `Optional` 属性命名 | 更名为 `Nullable`，用于明确字段是否允许空载荷。 |
| `[QuiverVector(..., Lazy = true)]` | 删除 Lazy 开关，改由全局或字段级 `VectorMemoryMode` / `VectorMemoryMode` 决定访问方式。 |

#### 新 API

| 新 API | 用途 |
|---|---|
| `LargeFieldMemoryMode` | 全局大字段载荷内存策略，支持 `InMemory`、`LazyLoad`、`PagedCache`、`PerField`。`LazyLoad` / `PagedCache` 需要有效 `DatabasePath`，且对应属性需声明为 partial 以便源生成器接入访问器。 |
| `LargeFieldMemoryMode` | 字段级大字段内存策略覆盖。 |
| `QuiverDbOptions.LargeFields.MaxCachedPayloads` | `PagedCache` 模式下每个 `QuiverSet` 最多缓存的大字段 payload 数量，默认 `128`，必须大于 `0`。 |
| `VectorMemoryMode` | 全局向量载荷内存策略，支持 `InMemory`、`MemoryMapped`、`Auto`、`PerField`。 |
| `VectorMemoryMode` | 字段级向量内存策略覆盖。 |
| `QuiverLargeFieldAttribute.Nullable` / `QuiverVectorAttribute.Nullable` | 显式声明大字段或向量字段是否允许 `null`。 |

#### 内部实现：统一 Payload 管线

向量载荷（`VectorBlob`）和大字段载荷（`Blob`）现在先进入统一的内部 payload descriptor / validation 管线，再分发到各自的二进制编码器。这样 `Nullable`、字段名、载荷类型、内存模式等元数据不再由两套路径各自解释。

大字段 `LazyLoad` / `PagedCache` 已接入该管线：加载 `Blob` 段时不再立即把命中字段写入实体，而是登记文件切片；用户首次读取 source-generated partial `byte[]` 属性时按需从文件读取。`PagedCache` 在此基础上增加内部 LRU 缓存，重复读取同一 payload 时复用缓存副本。

保存优化：如果 LazyLoad / PagedCache 大字段在加载后未被用户读取或重新赋值，`SaveAsync` 会直接从原 `.vdb` 的 Blob slice 复制字节到新快照，避免先物化成托管 `byte[]`。如果属性已被赋新值，则优先写入新值，不复用旧 slice。

#### 迁移示例

```csharp
// 旧写法（已删除）
new QuiverDbOptions
{
    VectorStore = VectorStoreMode.Mmap,
    MaxCachedPages = 16,
    PageSize = 512
};

[QuiverBlob(Optional = true)]
public byte[]? Payload { get; set; }

[QuiverVector(768, Lazy = true, Optional = true)]
public partial float[]? Embedding { get; set; }

// 4.0.1 新写法
new QuiverDbOptions
{
    Vectors.MemoryMode = GlobalVectorMemoryMode.MemoryMapped,
    LargeFields.MemoryMode = GlobalLargeFieldMemoryMode.PagedCache,
    LargeFields.MaxCachedPayloads = 256
};

[QuiverLargeField(Nullable = true)]
public byte[]? Payload { get; set; }

[QuiverVector(768, Nullable = true, MemoryMode = VectorMemoryMode.MemoryMapped)]
public partial float[]? Embedding { get; set; }
```

---

### 4.0.1 版本说明：移除 WAL

> 快照文件（`.vdb`）依然完全向后兼容 v1.x、v2.x、v3.0.x、v3.1.x、v3.2.x、v3.3.x；但 `.wal` 旁路文件不再被读取或写入。

**升级前先做这一步**：在 3.2.x 应用上跑一次 `LoadAsync()` + `SaveAsync()`，把所有挂起的 `.wal` 增量回放并合并进主快照。否则升级到 4.0.1 后，首次加载会**静默丢弃**这些未压缩的 WAL 增量。

**移除内容**：`QuiverDbOptions.EnableWal` / `WalCompactionThreshold` / `WalFlushToDisk` 三个配置项、`SaveChangesAsync()` 方法、内部 `WriteAheadLog` / `WalEntry` 类型，以及 `QuiverSet<T>` 内部用于追踪增量变更的 `_changeLog` 队列。

**原因**：WAL 增量持久化路径在大规模写入时会使内存峰值翻倍 —— change-log 队列对所有排队的实体保留强引用，同时索引层向量副本仍然存在，两者叠加导致 200 万条写入下出现 20+ GB 峰值。

**4.0.1 推荐用法**：

```csharp
// 3.2.x 旧写法
new QuiverDbOptions { DatabasePath = "x.vdb", EnableWal = true, WalCompactionThreshold = 10_000, WalFlushToDisk = true };
await db.SaveChangesAsync();

// 4.0.1 新写法
new QuiverDbOptions { DatabasePath = "x.vdb" };
await db.SaveAsync();              // 全量原子快照保存
```

> **离线格式升级时的 Schema Migration**：`ConfigureMigration<T>()` 只会在 `QuiverDbContext.LoadAsync()` 读取当前运行时支持的格式时生效。如果使用 `QuiverMigrator.MigrateAsync` 把 v1/v2/v3 文件离线升级到 v4，需要把同样的规则通过 `migrationRules` 参数显式传入；否则旧文件解码阶段遇到已重命名字段时会跳过旧值。

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

#### 新增：v4 文件格式（`QDB\x04`）— Segment + Footer + 段级 CRC32

4.0.1 使用 v4 段式二进制磁盘格式。v1/v2/v3 文件可通过迁移路径升级；新写入一律使用 v4。

```
[Magic "QDB\x04"][HeaderLen u32][Header bytes]
[Segment 1] [Segment 2] ... [Segment N]
[FooterTopMagic "QDBF"][SegmentCount u32]
  每段: [TypeName][Offset u64][Length u64][EntityCount u32][CRC32 u32]
[FooterOffset u64][TrailerMagic "QDBE"]
```

由此带来三个文件层面的新能力，**不**需要重新引入 WAL：

| API | 行为 | 代价 |
|---|---|---|
| `QuiverDbContext.AppendAsync()` | 把当前内存中的实体作为**新段**追加到已有 v4 文件，仅重写 footer。 | O(Δ) 字节。真正意义上的增量写入——取代 WAL 的使用场景，但没有 WAL 的内存翻倍问题。 |
| `QuiverDbContext.SaveAsync()` | 写入全量快照，并将多段文件碎片整理为单段。 | O(N)。建议周期性调用。 |
| `QuiverDbFile.MergeAsync(sources, dest, options, typeMap?)` | 合并多个 v4 文件。`MergeConflictPolicy.Append` 是纯字节拷贝段；`LastWriterWins` / `FirstWriterWins` 按 `[QuiverKey]` 去重。 | Append：O(I/O)，不解码。LWW/FWW：解码-重写。 |
| `QuiverDbFile.InspectAsync(path, verifyCrc)` | 返回 `QuiverFileInfo`（版本、段列表、每段 CRC 校验结果、每类型实体计数）。 | 校验 CRC 时为 O(file size)。 |

```csharp
// 增量批量入库 — 取代 4.0 之前的 SaveChangesAsync 工作流
await using var db = new MyDb("data.vdb");
await db.LoadAsync();
db.Faces.AddRange(batch);
await db.AppendAsync();              // 仅写入本批，无全量重写

// 定期碎片整理
await db.SaveAsync();

// 合并多个归档文件，按 [QuiverKey] 去重，靠后者覆盖
var typeMap = new Dictionary<string, Type>
{
    [typeof(FaceFeature).FullName!] = typeof(FaceFeature)
};
await QuiverDbFile.MergeAsync(
    sourceFiles: ["a.vdb", "b.vdb", "c.vdb"],
    destinationFile: "merged.vdb",
    options: new MergeOptions { ConflictPolicy = MergeConflictPolicy.LastWriterWins },
    typeMap: typeMap);

// 诊断
var info = await QuiverDbFile.InspectAsync("merged.vdb");
Console.WriteLine($"v{info.FormatVersion}, {info.Segments.Count} 段, crcValid={info.CrcValid}");
```

> 下方涉及 WAL、`SaveChangesAsync`、`EnableWal`、`WriteAheadLog`、`WalEntry`、`.wal` 文件的章节描述的是 4.0 之前的架构，仅作为历史参考保留。

---

### 项目背景

#### 创作梗概

Quiver 的创作灵感，最早可追溯到我编写 Vorcyc.AwesomeAI.Ash 类，该类用以提供简单的向量存储和检索功能，满足一些轻量级的语义搜索需求。虽然 Ash 在设计上追求极简和易用，但随着应用场景的升级，设计瓶颈也愈发明显：

- **表结构不可自定义** —— `Ash` 的存储架构由框架内部固定，使用者只能按照预设的字段布局存取数据，无法根据业务需求自由定义实体的属性和结构。当需要为不同场景（如人脸识别、文档检索、多模态搜索）设计差异化的数据模型时，这一限制显得尤为突出。
- **仅支持暴力搜索** —— `Ash` 的检索方式为逐条遍历、逐一计算相似度的暴力搜索（Brute-Force），时间复杂度为 O(n×d)。在数据量较小时尚可接受，但当向量规模增长到数万乃至数十万条时，搜索延迟急剧上升，缺乏近似最近邻（ANN）索引的支持使其难以胜任对响应速度有要求的生产场景。
- **不支持并发操作** —— `Ash` 的内部数据结构未做任何线程同步保护，在多线程环境下同时执行读写操作会导致数据竞争和不可预期的异常。对于需要并发查询的服务端场景（如 ASP.NET Web API 同时处理多个搜索请求），使用者必须在外部自行加锁，既增加了使用复杂度，又容易因锁粒度不当引发性能瓶颈或死锁风险。

在反思这些痛点的过程中，EF Core 的设计哲学带来了关键启发——尤其是其"通过代码定义架构（Code-First）"的理念：开发者只需用特性（Attribute）标记实体类的属性，框架便自动完成模型发现、关系映射和数据持久化，整个过程声明式且无侵入。
同时，Python 中的名为 Annoy（全称 Approximate Nearest Neighbors Oh Yeah） 的轮子也给了我启发，但是它的 .NET 包装 HNSWSharp 又不支持类似于结构化数据库的设计，且仅提供 HNSW 一种索引类型，缺乏灵活性和多样性。

于是，我决定设计一个全新的向量数据库框架，既要保持 EF Core 式的易用性和声明式建模，又要支持多种 ANN 索引算法以适应不同规模和性能需求的场景，同时还要内置并发安全机制和高效的持久化方案。

---

### 历史版本

#### 3.2.1 更新说明

> **文件格式兼容性**：v3.2.1 完全向后兼容 v1.x、v2.x、v3.0.0、v3.1.0 和 v3.2.0 的所有数据文件，无需任何迁移。

#### 缺陷修复

| 修复项 | 说明 |
|--------|------|
| **`EntityPageCache` 线程安全修复** | 修复了 `LazyPaging` 模式下的数据竞争问题：当多线程同时通过 `Parallel.ForEach` 调用 `Find` / `Search` 时，内部 LRU 结构（`_loadedPages`、`_lru`、`_lruNodes`）会发生并发写冲突。现已在 `GetOrLoadPage()`、`FlushDirty()`、`CompactMemory()`、`Clear()` 中通过 `_pageLock` 对所有 LRU 状态变更进行保护。`FullMemory` 模式不受影响（零开销）。 |

---

#### 内存映射向量 / 懒加载 Embedding / Tombstone / 后台 Merge

> 基于 v4（`QDB\x04`）段 + Footer 格式之上扩展。已有 v4 文件可被透明读取；新写入会把 footer 升级到 schema v2（每段附带 `Kind` / `FieldName` / `Dim` / `FirstId`）。

本次更新把向量和大字段从实体元数据中物理拆开，并用 "tombstone + merge" 模型替代原地删除。目标是即便在百万级高维向量场景下也保持托管堆平稳。

#### 新增：`VectorStoreMode.Mmap` — 零拷贝向量访问

磁盘上的 `VectorBlob` 段通过 `MemoryMappedFile` 映射进进程，向量直接从 OS 页缓存读取，不再进入托管堆。

```csharp
new QuiverDbOptions
{
    DatabasePath = "data.vdb",
    VectorStore = VectorStoreMode.Mmap,                  // Heap（默认）/ Mmap / Auto
    VectorStoreMmapThresholdBytes = 64L * 1024 * 1024,   // 仅 Auto 模式：超过此大小切换为 mmap
};
```

| 模式 | 后端 | 适用 |
|---|---|---|
| `Heap`（默认） | `HeapVectorStore`（`Dictionary<int, float[]>`） | 小规模 / 写多读少；无需 `DatabasePath` |
| `Mmap` | `MmapVectorStore`（对 `VectorBlob` 段的只读视图） | 大规模读多场景（人脸库、RAG 向量库），稳定的托管堆占用 |
| `Auto` | 小于阈值用 Heap，大于切到 Mmap | 混合工作负载 |

`SaveAsync` / `AppendAsync` 会在替换文件前自动释放 mmap 视图，写完后再次绑定到新的 `VectorBlob` 区域 —— 对调用方完全透明。

#### 新增：懒加载 `[QuiverVector]` 属性（源生成器）

只有当用户读取实体属性时才把向量从 mmap 物化出来。把属性声明为 `partial`，`Vorcyc.Quiver.SourceGenerators` 会生成调用 `LazyVectorAccessor.Materialize(this, "PropertyName")` 的 getter。

```csharp
public partial class AudioEntity
{
    [QuiverKey] public string Id { get; set; } = "";

    [QuiverVector(1024, DistanceMetric.Cosine, Lazy = true)]
    public partial float[]? Embedding { get; set; }   // backing field + getter 由源生成器产生
}
```

搜索热路径仍直接从 mmap 区域读向量（零分配）；用户访问 `entity.Embedding` 时才触发一次性的拷贝出列。

懒向量源生成要求向量属性以及它所在的整条嵌套类型链都声明为 `partial`，属性类型必须是 `float[]` 或 `float[]?`。无效声明会产生 analyzer 诊断：`QVR001`（属性不是 partial）、`QVR002`（包含类型链并非全部 partial）、`QVR003`（属性类型无效）。

> 使用方项目需引用 analyzer：
> ```xml
> <ProjectReference Include="..\Vorcyc.Quiver.SourceGenerators\Vorcyc.Quiver.SourceGenerators.csproj"
>                   OutputItemType="Analyzer"
>                   ReferenceOutputAssembly="false" />
> ```

#### 新增：`[QuiverBlob]` —— 大 `byte[]` 字段独立成段

inline `byte[]`（缩略图、原始音频、序列化特征）以往会撑大 `EntityMeta` 段并在 load 时膨胀工作集。加上 `[QuiverBlob]` 后，这类字段会写到独立的 `SegmentKind.Blob` 段。

```csharp
public class Photo
{
    [QuiverKey] public string Id { get; set; } = "";
    [QuiverVector(512, DistanceMetric.Cosine, Lazy = true)] public partial float[]? Embedding { get; set; }
    [QuiverBlob] public byte[]? Thumbnail { get; set; }   // ← 写入独立的 Blob 段
}
```

`[QuiverBlob]` 仅可用于 `byte[]`，且与 `[QuiverVector]` 互斥。

#### 新增：Tombstone 段 + `FlushTombstonesAsync()`

之前 `RemoveByKey` 后再 `AppendAsync` 没有磁盘表达。Wave 2 增加了 `SegmentKind.Tombstone` 段，记录死亡的内部行 id，加载时由读取层先行过滤。

```csharp
await using var db = new MyDb("data.vdb");
await db.LoadAsync();

db.Faces.RemoveByKey("F0001");
db.Faces.RemoveByKey("F0002");

// 仅写出 Tombstone 段；不会把当前内存里的活实体重新作为新段追加。
await db.FlushTombstonesAsync();
```

| API | 写入内容 | 适用场景 |
|---|---|---|
| `AppendAsync()` | 为**所有**当前内存实体写出新的 `EntityMeta` / `VectorBlob` / `Blob` 段，同时附带一个 Tombstone 段（如有待删除项）。 | 批量入库 + 顺便刷掉挂起删除。 |
| `FlushTombstonesAsync()` | **仅**写出 Tombstone 段。 | 加载 → 原地修改 → 只刷删除，不重写活实体。 |
| `SaveAsync()` | 单段原子快照，所有历史 tombstone 在物理上被丢弃。 | 周期性碎片整理。 |

#### 新增：后台自动 Merge

`QuiverDbOptions` 新增三个阈值，每次 `AppendAsync` / `FlushTombstonesAsync` 之后做尽力而为的自动 Rewrite：

| 选项 | 默认 | 用途 |
|---|---|---|
| `EnableBackgroundMerge` | `false` | 总开关 |
| `AutoMergeMaxSegments` | `32` | footer 段数达到该值后触发 `SaveAsync()` |
| `AutoMergeTombstoneRatio` | `0.25` | `tombstone / live ≥ ratio` 时触发 |

自动 merge 内部的任何异常都会被吞掉，绝不会冒泡到用户的 `AppendAsync` 调用。

#### `QuiverDbFile.InspectAsync()` 新增 `Kind` / `FieldName` / `Dim`

`SegmentInfo` 暴露新增列：`Kind`（Mixed / EntityMeta / VectorBlob / Blob / Tombstone）、`FieldName`、`Dim`。同类型跨多个 `VectorBlob` / `Blob` / `Tombstone` 段时实体数不再重复计数。

#### Footer schema v2

```
[FooterTopMagic "QDB2"][SegmentCount u32]
  每段:
    [TypeName s][Offset u64][Length u64][EntityCount u32][CRC32 u32]
    [Kind u8][FieldName s][Dim i32][FirstId i32]
[FooterOffset u64][TrailerMagic "QDBE"]
```

`"QDBF"`（v1 footer）仍然向后兼容读取，新写入一律使用 `"QDB2"`。

---

#### 向量量化 / Matryoshka 截断 / 运行时堆→Mmap 自动提升

> 基于段式文件格式和 mmap 向量存储继续演进。已有 raw float32 `VectorBlob` 段可被透明读取；新写入会在每段头部加 `VectorBlobEncoding` 与可选 SQ8 scale 表。索引拓扑结构与公共 API 保持不变。

本次更新聚焦于"在源端 embedding 模型未知的前提下"也能稳定控制磁盘体积、托管堆体积与运行期内存：通过字段级量化与 Matryoshka 截断在 I/O 路径上压缩向量，通过堆字节预算在运行期自动从 Heap 升级到 Mmap。

#### 新增：字段级向量量化（`[QuiverVector(..., Quantization = ...)]`）

`QuiverVectorAttribute` 新增 `Quantization` 属性，目前支持两种编码：

| 取值 | 磁盘体积 | 说明 |
|---|---|---|
| `VectorQuantization.None`（默认） | `dim × 4B` | Raw float32，与既有 v4 原始向量段行为一致 |
| `VectorQuantization.Sq8` | `dim × 1B + 4B scale` | 按行 SQ8 标量量化（int8 + 单 scale），磁盘体积约 1/4，搜索时通过 `Sq8Codec.DecodeRow` 解码到 thread-local 缓冲，零分配 |

```csharp
public partial class FaceFeature
{
    [QuiverKey] public string Id { get; set; } = "";

    // SQ8 + Matryoshka：1024 维 embedding 仅用前 512 维参与索引/搜索，磁盘体积 ≈ 1024×1B + 4B
    [QuiverVector(1024, DistanceMetric.Cosine,
                  Lazy = true,
                  Quantization = VectorQuantization.Sq8,
                  EffectiveDimensions = 512)]
    public partial float[]? Embedding { get; set; }
}
```

编码信息按段持久化在 v4 `VectorBlob` 段头中（`VectorBlobEncoding` enum + 版本号），加载时由 `MmapVectorStore` 与 `BinaryStorageProvider` 自动按段解码。源端 embedding 模型可未知。

#### 新增：Matryoshka 截断（`EffectiveDimensions`）

`QuiverVectorAttribute.EffectiveDimensions` 允许在不修改源 embedding 的前提下，只取向量前 N 维参与索引与搜索：

- 写入路径：`PrepareVectors` 在 `EffectiveDimensions < Dimensions` 时复制前 N 维到新数组（避免修改实体本身），可选 L2 归一化后入库与建索引。
- 查询路径：`Search` / `SearchKnn` 自动对查询向量做同样截断与归一化，保证查询向量与底层 store 的几何对齐。
- 索引拓扑：所有索引（Flat / HNSW / IVF / KDTree）按 `EffectiveDimensions` 构建，距离计算成本随之线性下降。

适合 Matryoshka 系列 embedding 模型（如 OpenAI `text-embedding-3-large`、Nomic 等），也可用于"先用低维快速召回 + 再用全维精排"的两阶段管线。

#### 新增：堆字节预算 + 运行期 Heap → Mmap 自动提升

`QuiverDbOptions` 新增两个运行期内存控制项：

| 选项 | 默认 | 用途 |
|---|---|---|
| `MaxHeapVectorBytes` | `0` (禁用) | 单个 `QuiverSet` 中所有 `HeapVectorStore` 的字节合计上限 |
| `AutoPromoteToMmap` | `false` | 越限时是否自动把该 set 的 Heap 向量 store 升级为 Mmap |

工作流程：

1. `QuiverSet` 的 `Add` / `AddRange` / `Upsert` 写路径在写锁尾部调用 `NotifyHeapBytes()`，把当前 `IVectorStore.HeapByteSize` 合计上报给 `QuiverDbContext`。
2. `QuiverDbContext`（实现内部接口 `IPromotionCoordinator`）按 `(AutoPromoteToMmap && bytes ≥ MaxHeapVectorBytes && DatabasePath != null)` 判定，并对每个 entity type 用 CAS 做单飞排队。
3. 后台任务执行 `SaveAsync()`（保证磁盘内容与内存一致），随后用新 `VectorBlob` 段的 mmap 视图通过 `QuiverSet.PromoteFieldsToMmap(...)` 替换原 `HeapVectorStore`。
4. 替换通过新引入的 `VectorStoreSlot` 间接层完成 —— 索引持有的是稳定的 slot 引用，**索引拓扑无需重建**，搜索热路径不中断。

```csharp
new QuiverDbOptions
{
    DatabasePath = "audio.vdb",
    VectorStore = VectorStoreMode.Heap,        // 起步用 Heap
    MaxHeapVectorBytes = 512L * 1024 * 1024,   // 单集合堆向量超 512 MiB
    AutoPromoteToMmap = true,                  // 自动升级到 Mmap
};
```

升级失败（如磁盘不可写）会被 `Trace.TraceWarning` 记录并把 in-flight 标志复位，不会冒泡到用户写路径。

#### 公共 API 增量

| 新增 | 位置 | 说明 |
|---|---|---|
| `VectorQuantization` enum | `Vorcyc.Quiver.Quantization` | 字段级量化策略 |
| `VectorBlobEncoding` enum | `Vorcyc.Quiver.Storage` | `VectorBlob` 段编码版本 |
| `Sq8Codec` | `Vorcyc.Quiver.Storage` | SQ8 行编码/解码（thread-local 缓冲） |
| `IVectorStore.HeapByteSize` | `Vorcyc.Quiver.Indexing` | 当前 store 的托管堆字节合计 |
| `IVectorStore.EffectiveDim` | `Vorcyc.Quiver.Indexing` | 索引/搜索实际使用的维度 |
| `QuiverDbOptions.MaxHeapVectorBytes` | 同上 | 堆字节预算 |
| `QuiverDbOptions.AutoPromoteToMmap` | 同上 | 自动提升总开关 |
| `QuiverVectorAttribute.Quantization` | `Vorcyc.Quiver` | 字段量化策略 |
| `QuiverVectorAttribute.EffectiveDimensions` | 同上 | Matryoshka 截断目标维度 |

#### 兼容性

- 文件格式：v4 `VectorBlob` 段加入了 encoding 字节 + 可选 SQ8 scale 区，仍嵌入在 `QDB\x04` 容器与 footer schema v2 内；既有 raw float32 段会被透明读取。
- 公共 API：`QuiverDbOptions`、`QuiverVectorAttribute`、`IVectorStore`、`QuiverSet<T>` 仅做加法式扩展，已有调用方代码无需修改。
- 索引：`VectorStoreSlot` 是 `QuiverSet<T>` 内部包装，对索引实现完全透明。

---

#### HNSW 快照持久化 / Mmap 加载修复

本次更新解决大库加载时 HNSW 图需要逐点重建的问题。`SaveAsync()` 会为支持快照的索引写出独立的 `SegmentKind.IndexSnapshot` 段；`LoadAsync()` 会优先恢复索引拓扑，再只为快照未覆盖的新行补建索引。

#### 新增：HNSW 索引快照

HNSW 快照保存入口点、最大层级、节点层数、每层邻居列表以及快照覆盖的 `NextId`，避免每次加载都按 `Add(id)` 重跑 O(N log N) 的图构建流程。对于几十万级向量库，加载耗时主要从“重建图”转为“读取并反序列化拓扑”。

快照包含相似度类型、HNSW 参数、有效维度等指纹。若运行时模型、维度、量化/截断后的有效维度或索引参数不匹配，加载器会拒绝该快照并自动回退到旧的重建路径；旧文件没有 `IndexSnapshot` 段时也保持完全兼容。

#### 修复：Mmap 向量加载顺序与稳定类型名

在 `VectorStore = Mmap / Auto` 时，加载流程现在保证先把 `VectorBlob` 段绑定到 `MmapVectorStore`，再对快照未覆盖的 id 执行索引补建，避免 HNSW 在 mmap 尚未绑定时读取向量导致 `KeyNotFoundException: Vector id ... not found in mmap store.`。

同时，mmap 区域匹配会同时接受 `[QuiverEntity("稳定名称")]` 和旧的 `Type.FullName` 别名，防止给实体添加稳定名后旧 v4 文件的 mmap 段被静默跳过。

#### 兼容性

- 文件格式：新增可选 `SegmentKind.IndexSnapshot` 段，旧 v4 文件照常读取；不支持快照的索引类型继续按原逻辑重建。
- 懒加载：实体分页、懒向量、`[QuiverBlob]` 大对象懒加载均不受影响；快照只保存索引拓扑，不保存实体或向量副本。
- mmap：快照恢复与 mmap 绑定解耦，搜索热路径仍直接从 mmap 读取向量。

---

#### 3.2.0 更新说明

> **文件格式兼容性**：v3.2.0 完全向后兼容 v1.x、v2.x、v3.0.0 和 v3.1.0 的数据文件，无需任何迁移。

#### 新增功能

| 功能 | 说明 |
|------|------|
| **`CompactMemory()` / `CompactMemoryAsync()`** | 在 `QuiverSet<T>` 上调用，将所有脏页刷写到磁盘后驱逐全部内存页，按需最小化内存占用。在 `FullMemory` 模式下为空操作。向量索引始终驻留内存，不受影响。 |
| **`CompactAllMemoryAsync()`** | 在 `QuiverDbContext` 上调用，对上下文中所有 `QuiverSet` 一次性执行内存压缩。 |

---

#### 3.1.0 更新说明

> **文件格式兼容性**：v3.1.0 完全向后兼容 v1.x、v2.x 和 v3.0.0 的数据文件，无需任何迁移。

#### 重大变更（Breaking Changes）

| 变更项 | v3.0.0 | v3.1.0 |
|--------|--------|--------|
| **`VectorStorageMode` 已移除** | `QuiverDbOptions.VectorStorage = VectorStorageMode.MemoryMapped` —— 可选的内存映射向量存储（`MmapVectorStore`） | **已完全移除**。向量始终存储在 GC 堆（`HeapVectorStore`）。`LazyPaging` 分页缓存已能有效控制实体内存，独立的 mmap 层不再必要。 |
| **`QuiverSet` 构造函数简化** | 接受 `DistanceMetric defaultMetric` 参数 | 已移除该参数。每个向量字段通过 `[QuiverVector(dim, metric)]` 独立声明其度量。 |

#### 从 v3.0.0 迁移

如果你之前在 `QuiverDbOptions` 中设置了 `VectorStorage = VectorStorageMode.MemoryMapped`，直接删除该行即可，无需其他修改，数据文件完全兼容：

```csharp
// v3.0.0（删除 VectorStorage 行）
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    // VectorStorage = VectorStorageMode.MemoryMapped,  ← 删除此行
    EntityCache = EntityCacheMode.LazyPaging,
    MaxCachedPages = 32,
    PageSize = 512
};
```

---

#### 3.0.0 更新说明

> **文件格式兼容性**：v3.0.0 完全向后兼容 v1.x 和 v2.x 的数据文件。

#### 新增功能

| 功能 | 说明 |
|------|------|
| **懒加载分页缓存** | `EntityCache = EntityCacheMode.LazyPaging` —— 实体对象不再全量常驻内存，而是按固定大小的页（`PageSize` 条/页）按需从页文件加载，并通过 LRU 策略淘汰冷页。 |
| **可控内存上限** | 实体对象的工作集上限约为 `MaxCachedPages × PageSize × 单实体大小`，与数据集总大小无关。 |
| **向量索引仍常驻内存** | HNSW / IVF / KDTree 索引拓扑结构始终驻留内存，搜索性能不受懒加载影响。 |
| **`IsLazyLoading` 属性** | `QuiverSet<T>.IsLazyLoading` 可用于运行时诊断当前缓存模式。 |
| **透明 API** | `EntityPageCache<T>` 与旧版 `Dictionary<int, TEntity>` 接口对齐，调用方代码无需任何修改。 |
| **内存映射向量存储** | `VectorStorage = VectorStorageMode.MemoryMapped`（在 3.0.0 引入，**已在 3.1.0 移除**，见上方说明）。 |

#### 新增配置项（`QuiverDbOptions`）

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `EntityCache` | `EntityCacheMode` | `FullMemory` | 实体缓存模式：`FullMemory`（全量常驻）/ `LazyPaging`（LRU 分页），须设置 `DatabasePath` |
| `MaxCachedPages` | `int` | `16` | 每个 `QuiverSet` 最多在内存中保留的页数 |
| `PageSize` | `int` | `512` | 每页最多容纳的实体数量 |

#### 快速上手 —— 懒加载模式

```csharp
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    EntityCache = EntityCacheMode.LazyPaging,  // ← 启用懒加载分页
    MaxCachedPages = 32,                       // 内存中最多保留 32 页
    PageSize = 512                             // 每页 512 条实体
    // 内存上限 ≈ 32 × 512 × 单实体大小
};
```

> 页文件存储于 `{DatabasePath}.pages/{EntityTypeName}/page_XXXXXXXX.qvpg`（自定义二进制格式，无外部依赖）。
>
> **页文件二进制布局（v1）**：
> ```
> [4B uint32]  Magic = 0x51565047  （"QVPG" 文件标识）
> [1B byte]    Version = 0x01
> [4B int32]   PropCount            ← 属性描述符数量
> PropDescriptor × PropCount:
>   [string]   PropName             ← BinaryWriter 长度前缀 UTF-8
> [4B int32]   EntityCount          ← 本页实体数
> Entity × EntityCount:
>   [4B int32] InternalId
>   按描述符顺序逐字段：[1B bool null标志] + 字段值
>                       （类型编码同 BinaryStorageProvider）
> ```

---

#### 2.0.0 更新说明

> **文件格式兼容性**：v2.0.0 完全向后兼容 v1.x 的数据文件。三种存储格式（JSON / XML / Binary）和 WAL 文件均可直接加载，无需任何迁移。

#### 架构变更（Breaking Changes）

| 变更项 | v1.x | v2.0.0 |
|--------|------|--------|
| 相似度计算 | `SimilarityFunc` 委托 | `ISimilarity<T>` 静态抽象接口 —— JIT 为每个具体类型生成特化机器码，零虚分派 |
| 向量数据所有权 | 各索引内部各自存储向量 | `IVectorStore` 抽象 —— 索引仅管理拓扑结构（图/树/倒排），向量由存储层统一管理 |

#### 新增功能

| 功能 | 说明 |
|------|------|
| **6 种新距离度量** | Manhattan（L1）、Chebyshev（L∞）、Pearson 相关、Hamming、Jaccard、Canberra —— 加上原有 3 种（Cosine / Euclidean / DotProduct），共 9 种内置度量 |
| **自定义相似度** | `[QuiverVector(128, CustomSimilarity = typeof(MySimilarity))]` —— 接入任意 `ISimilarity<float>` 结构体 |
| **IVectorStore 抽象** | `HeapVectorStore`（GC 堆）—— 可插拔的向量存储后端 |

#### 性能优化

| 优化项 | 详情 |
|--------|------|
| **全度量 SIMD 加速** | 全部 9 种相似度实现均使用内部 `VectorMath` / `Vector<float>` 路径，自动适配 SSE4 / AVX2 / AVX-512 寄存器宽度，无额外 NuGet 依赖 |
| **零开销分派** | `ISimilarity<T>` 配合 `static abstract` + `readonly struct`，JIT 在调用站点直接内联 `TSim.Compute()` —— 无委托间接调用 |

---
