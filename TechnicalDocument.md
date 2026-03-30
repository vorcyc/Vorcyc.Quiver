# Vorcyc.Quiver 技术方案文档

> **版本**：1.2.2  
> **目标框架**：.NET 10  
> **许可证**：MIT  
> **NuGet**：https://www.nuget.org/packages/Vorcyc.Quiver  
> **源码仓库**：https://github.com/vorcyc/Vorcyc.Quiver

---

## 1. 项目概述

**Vorcyc.Quiver** 是一款纯 .NET 实现的嵌入式向量数据库。它以 NuGet 包的形式引入项目，进程内运行，无需部署独立的数据库服务器，也无任何原生（Native）依赖。

项目借鉴 EF Core 的 `DbContext` 设计模式，开发者通过声明式特性标注（`[QuiverKey]`、`[QuiverVector]`、`[QuiverIndex]`）定义实体与索引策略，框架在运行时自动完成模型发现、索引构建和持久化管理。

### 1.1 核心特性

| 特性 | 说明 |
|------|------|
| **Code-First 声明式建模** | 类似 EF Core，通过 Attribute 标注实体类，框架自动发现并注册 `QuiverSet<T>` 集合 |
| **多种 ANN 索引** | 内置 Flat（暴力搜索）、HNSW、IVF、KDTree 四种索引算法 |
| **灵活持久化** | 支持 JSON / XML / Binary 三种存储格式，以及 WAL 增量持久化 |
| **并发安全** | `QuiverSet<T>` 内部通过 `ReaderWriterLockSlim` 实现读写分离，开箱即用 |
| **SIMD 硬件加速** | 基于 `TensorPrimitives` 的 SIMD 指令加速向量相似度计算和 L2 归一化 |
| **Schema Migration** | 支持属性重命名和值转换（`ConfigureMigration<T>()`），新增/删除字段自动处理 |

### 1.2 典型应用场景

语义搜索、RAG（检索增强生成）、人脸识别、以图搜图、推荐系统、多模态检索等。

---

## 2. 技术栈与依赖

| 项 | 技术 |
|----|------|
| 语言 | C# 13 / .NET 10 |
| 解决方案格式 | `.slnx`（VS 2026 SDK 风格） |
| 外部 NuGet 依赖 | `System.IO.Hashing` ≥ 10.0.5（CRC32 校验）<br>`System.Numerics.Tensors` ≥ 10.0.5（SIMD 加速） |
| 单元测试 | 控制台测试项目 `all_basic_tests` |

### 2.1 项目结构

```
Vorcyc.Quiver/
├── Vorcyc.Quiver/                    # 主项目
│   ├── Attributes.cs                 # 特性定义 ([QuiverKey], [QuiverVector], [QuiverIndex]) + 枚举
│   ├── QuiverDbOptions.cs            # 全局配置选项
│   ├── QuiverDbContext.cs            # 数据库上下文基类（自动发现、持久化、生命周期）
│   ├── QuiverSet.cs                  # 向量集合 — 字段/构造/枚举/Dispose/工具方法
│   ├── QuiverSet.Crud.cs             # 向量集合 — CRUD 操作
│   ├── QuiverSet.Search.cs           # 向量集合 — 搜索（同步 + 异步 + 默认字段 + 过滤）
│   ├── QuiverSet.Persistence.cs      # 向量集合 — 变更追踪 & WAL 回放
│   ├── Indexing/
│   │   ├── IVectorIndex.cs           # 索引统一接口 + SimilarityFunc 委托
│   │   ├── FlatIndex.cs              # 暴力搜索索引（>10K 自动并行）
│   │   ├── HnswIndex.cs             # HNSW 分层图索引
│   │   ├── IvfIndex.cs              # IVF 倒排文件索引（K-Means 聚类）
│   │   └── KDTreeIndex.cs           # KD-Tree 空间二叉树索引
│   └── Storage/
│       ├── IStorageProvider.cs       # 存储接口 + 工厂
│       ├── JsonStorageProvider.cs    # JSON 存储（System.Text.Json）
│       ├── XmlStorageProvider.cs     # XML 存储（XDocument + Base64）
│       ├── BinaryStorageProvider.cs  # 二进制存储（自定义协议 + MemoryMarshal 零拷贝）
│       └── Wal/
│           ├── WalEntry.cs           # WAL 条目记录
│           └── WriteAheadLog.cs      # WAL 读写引擎（CRC32 校验 + 崩溃恢复）
└── TESTS/all_basic_tests/            # 测试项目
    └── Program.cs
```

---

## 3. 架构设计

### 3.1 分层架构

```
┌─────────────────────────────────────────────┐
│              用户代码层                       │
│  实体类 + [QuiverKey/Vector/Index] 标注      │
│  MyDbContext : QuiverDbContext               │
├─────────────────────────────────────────────┤
│              框架层                           │
│  QuiverDbContext    —— 上下文管理/持久化      │
│  QuiverSet<T>      —— 集合/CRUD/搜索/并发    │
├─────────────────────────────────────────────┤
│              索引层                           │
│  IVectorIndex → Flat / HNSW / IVF / KDTree  │
├─────────────────────────────────────────────┤
│              存储层                           │
│  IStorageProvider → Json / Xml / Binary      │
│  WriteAheadLog (WAL 增量持久化)              │
└─────────────────────────────────────────────┘
```

### 3.2 核心组件关系

| 组件 | 类型 | 职责 |
|------|------|------|
| `QuiverDbContext` | 抽象类 | 数据库上下文基类；反射发现 `QuiverSet` 属性、管理持久化读写和生命周期 |
| `QuiverSet<TEntity>` | partial 类 | 向量集合；提供 CRUD + 多种搜索模式 + 变更追踪；内部 `ReaderWriterLockSlim` 读写锁 |
| `IVectorIndex` | 内部接口 | 统一索引契约（Add / Remove / Clear / Search / SearchByThreshold） |
| `IStorageProvider` | 内部接口 | 统一存储契约（SaveAsync / LoadAsync） |
| `WriteAheadLog` | 内部密封类 | WAL 引擎；自定义二进制格式 + CRC32 校验 + 崩溃恢复 |
| `QuiverDbOptions` | 配置类 | 全局选项：存储路径、默认度量、格式、WAL 配置等 |
| `QuiverSearchResult<T>` | record | 搜索结果 DTO，包含 `Entity` 和 `Similarity` |

### 3.3 关键设计决策

1. **表达式树编译属性访问器**：使用 `Expression.Lambda<Func<T,R>>(...).Compile()` 替代反射的 `PropertyInfo.GetValue`，性能提升约 100 倍（纳秒级 vs 微秒级）。
2. **FrozenDictionary 元数据存储**：向量字段元信息和索引映射使用 `FrozenDictionary` 冻结，零堆分配查找。
3. **Cosine 预归一化优化**：写入时自动 L2 归一化向量，搜索时以 `Dot` 替代 `CosineSimilarity`（从 3 次向量遍历降为 1 次）。
4. **SimilarityFunc 委托直接绑定**：相似度委托直接绑定 `TensorPrimitives` 方法组（如 `TensorPrimitives.Dot`），零 lambda 包装开销。
5. **原子写入策略**：`SaveAsync` 先写临时文件，成功后 `File.Move` 替换，防止写入中途崩溃导致数据损坏。

---

## 4. 核心概念

### 4.1 实体定义与特性标注

```csharp
using Vorcyc.Quiver;

public class Document
{
    [QuiverKey]                                       // 主键（必须，唯一）
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    [QuiverVector(384, DistanceMetric.Cosine)]        // 向量字段（必须 ≥ 1 个）
    public float[] Embedding { get; set; } = [];
}
```

**三个核心特性：**

| 特性 | 作用 | 必须 |
|------|------|------|
| `[QuiverKey]` | 标记实体主键，每个实体有且仅有一个 | ✅ |
| `[QuiverVector(dim, metric)]` | 标记向量字段，指定维度和距离度量。`Optional = true` 允许 null | ✅（≥1 个） |
| `[QuiverIndex(type, ...)]` | 配置索引类型及参数，未标记时默认 Flat | ❌ |

### 4.2 数据库上下文

```csharp
public class MyDocumentDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;  // 自动注入

    public MyDocumentDb() : base(new QuiverDbOptions
    {
        DatabasePath = "documents.json",
        StorageFormat = StorageFormat.Json,
        DefaultMetric = DistanceMetric.Cosine
    }) { }
}
```

上下文构造时自动反射发现所有 `QuiverSet<T>` 属性，通过 `Activator.CreateInstance` 创建并注入实例。

### 4.3 距离度量

| 度量 | 公式 | 值域 | 适用场景 | 预归一化 |
|------|------|------|----------|----------|
| `Cosine` | cos(θ) = a·b / (\|a\| × \|b\|) | [-1, 1] | 文本嵌入、语义搜索 | ✅ 自动启用 |
| `Euclidean` | 1 / (1 + \|a - b\|₂) | (0, 1] | 空间坐标、物理距离 | ❌ |
| `DotProduct` | a · b | (-∞, +∞) | 已归一化向量、MIPS | ❌ |

### 4.4 索引类型

| 索引 | 搜索复杂度 | 精确度 | 适用数据量 | 适用维度 |
|------|-----------|--------|-----------|----------|
| **Flat** | O(n×d) | 100% | < 10K | 任意 |
| **HNSW** | O(log n) | ~95-99%+ | 10K ~ 10M | 任意 |
| **IVF** | O(n/k×d) | ~90-99% | 100K+ | 任意 |
| **KDTree** | O(log n) ~ O(n) | 100% | < 10K | < 20 |

**索引选择建议**：数据量 < 10K → Flat；维度 < 20 → KDTree；100K+ 批量查询 → IVF；其余情况 → **HNSW（通用首选）**。

配置示例：
```csharp
// HNSW 索引
[QuiverVector(768, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
public float[] Embedding { get; set; } = [];

// IVF 索引
[QuiverVector(128, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.IVF, NumClusters = 100, NumProbes = 15)]
public float[] Feature { get; set; } = [];
```

---

## 5. 快速上手

### 5.1 安装

```
dotnet add package Vorcyc.Quiver --version 1.1.2
```

### 5.2 基本用法

```csharp
await using var db = new MyDocumentDb();
await db.LoadAsync();                        // 加载（文件不存在时静默返回）

// 添加实体
db.Documents.Add(new Document
{
    Id = "doc-001",
    Title = "向量数据库入门",
    Category = "教程",
    Embedding = new float[384]               // 应为模型输出的嵌入向量
});

// 搜索 Top-5
float[] queryVector = new float[384];
var results = db.Documents.Search(e => e.Embedding, queryVector, topK: 5);

foreach (var r in results)
    Console.WriteLine($"{r.Entity.Title} — 相似度: {r.Similarity:F4}");

// 作用域结束 → DisposeAsync → 自动保存到磁盘
```

### 5.3 WAL 增量持久化模式

```csharp
public class MyWalDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;

    public MyWalDb() : base(new QuiverDbOptions
    {
        DatabasePath = "documents.vdb",
        StorageFormat = StorageFormat.Binary,
        EnableWal = true,
        WalCompactionThreshold = 10_000,
        WalFlushToDisk = true
    }) { }
}

await using var db = new MyWalDb();
await db.LoadAsync();                        // 加载快照 + 回放 WAL

db.Documents.Add(new Document { ... });
await db.SaveChangesAsync();                 // 仅追加变更到 WAL，O(Δ)
await db.CompactAsync();                     // 手动压缩：全量快照 + 清空 WAL
```

---

## 6. API 速览

### 6.1 QuiverDbContext

| 方法 | 说明 |
|------|------|
| `Set<TEntity>()` | 按类型获取向量集合 |
| `SaveAsync(path?)` | 全量保存到磁盘（WAL 启用时同时清空 WAL） |
| `SaveChangesAsync()` | WAL 增量保存（未启用 WAL 时回退到全量） |
| `CompactAsync()` | 全量快照 + 清空 WAL |
| `LoadAsync(path?)` | 加载快照 + 回放 WAL（文件不存在时静默返回） |
| `DisposeAsync()` | 自动保存后释放资源（推荐配合 `await using`） |

### 6.2 QuiverSet&lt;TEntity&gt;

**CRUD：**

| 方法 | 说明 |
|------|------|
| `Add(entity)` | 添加单个实体（重复主键抛异常） |
| `AddRange(entities)` | 批量添加（原子两阶段提交） |
| `Upsert(entity)` | 插入或更新（单次写锁内完成） |
| `Remove(entity)` / `RemoveByKey(key)` | 按实体或主键删除 |
| `Find(key)` | 按主键查找，O(1) |
| `Exists(key)` / `Exists(predicate)` | 存在性检查 |
| `Clear()` | 清空所有数据与索引 |

**搜索：**

| 方法 | 说明 |
|------|------|
| `Search(selector, query, topK)` | Top-K 搜索 |
| `Search(selector, query, topK, filter)` | 带过滤的 Top-K |
| `SearchByThreshold(selector, query, threshold)` | 阈值搜索 |
| `SearchTop1(selector, query)` | 最相似的单个实体 |
| `Search(query, topK)` | 默认字段搜索（单向量字段实体） |
| 所有方法均有 `Async` 版本 | 通过 `Task.Run` 卸载到线程池 |

### 6.3 配置选项 (QuiverDbOptions)

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `DatabasePath` | `string?` | `null` | 存储路径；null 为内存模式 |
| `DefaultMetric` | `DistanceMetric` | `Cosine` | 默认距离度量 |
| `StorageFormat` | `StorageFormat` | `Json` | 持久化格式：Json / Xml / Binary |
| `EnableWal` | `bool` | `false` | 是否启用 WAL |
| `WalCompactionThreshold` | `int` | `10,000` | WAL 自动压缩阈值 |
| `WalFlushToDisk` | `bool` | `true` | WAL 写入后是否 fsync |

---

## 7. 存储方案

### 7.1 三种格式对比

| 格式 | 实现 | 可读性 | 文件体积 | 读写速度 | 适用场景 |
|------|------|--------|---------|----------|----------|
| **Json** | `JsonStorageProvider` (System.Text.Json) | ✅ 优 | 最大 | 一般 | 开发调试 |
| **Xml** | `XmlStorageProvider` (XDocument + Base64) | ✅ 良 | 较大 | 一般 | 兼容性需求 |
| **Binary** | `BinaryStorageProvider` (自定义协议 + MemoryMarshal) | ❌ | 最小 | 最快 | 生产环境 |

### 7.2 WAL 增量持久化

WAL（Write-Ahead Log）通过追加日志的方式记录写操作，将每次保存的复杂度从 O(N) 降为 O(Δ)。

**工作流程：**
1. 用户调用 `Add` / `Upsert` / `Remove` / `Clear` → 内存更新 + 变更记录到 `_changeLog`
2. 调用 `SaveChangesAsync()` → 变更序列化为 `WalEntry` → 批量追加到 `.wal` 文件（CRC32 校验）
3. WAL 记录数超过阈值 → 自动触发 `CompactAsync()`（全量快照 + 清空 WAL）
4. 调用 `LoadAsync()` → 先加载全量快照 → 再按序回放 WAL 增量变更

**WAL 记录格式（每条）：**
```
[4B DataLength] [8B SeqNo] [1B OpCode] [string TypeName] [string PayloadJson] [4B CRC32]
```

**崩溃恢复**：加载时逐条校验 CRC32，校验失败即停止读取，不完整的尾部记录被安全丢弃。

---

## 8. 并发模型

`QuiverSet<T>` 内部使用 `ReaderWriterLockSlim` 实现读写分离：

| 操作类型 | 锁类型 | 示例 |
|----------|--------|------|
| **读操作** | 共享读锁（可并行） | `Search` / `Find` / `Exists` / `Count` / `foreach` |
| **写操作** | 独占写锁（互斥） | `Add` / `Upsert` / `Remove` / `Clear` |

- 多线程并发搜索无需外部加锁
- 枚举（`foreach` / LINQ）在读锁内拍摄快照，释放锁后再逐一迭代，避免持锁执行用户代码
- `Dispose` 使用 `Interlocked.Exchange` 保证并发安全

---

## 9. Schema 迁移

当实体结构演进时（增/删/重命名字段、更改值类型），Quiver 提供透明的 Schema 迁移机制，在 `LoadAsync` 时自动处理差异。

**自动处理（无需配置）**：
- 新增字段 → 取 CLR 默认值
- 删除字段 → 静默跳过

**属性重命名与值转换**：

```csharp
public class MyDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;

    public MyDb() : base(new QuiverDbOptions { DatabasePath = "my.db" })
    {
        ConfigureMigration<Document>(m => m
            .RenameProperty("OldTitle", "Title")
            .TransformValue("Score", v => v is int i ? (double)i : v));
    }
}
```

**处理顺序**：
1. **属性重命名** —— 反序列化阶段应用（存储提供者将旧名映射为新名）
2. **值转换** —— 反序列化完成后应用（上下文遍历实体并执行转换）

三种存储格式（JSON / XML / Binary）和 WAL 回放均支持。

---

## 10. 生命周期管理

```
[创建] new MyDb(options)
   │
   ├── InitializeSets()  反射发现 QuiverSet 属性
   ├── 初始化 WAL（如果启用）
   │
   ▼
[活跃] Add / Search / Save / Load ...
   │
   ▼
[释放]
   ├── DisposeAsync()  → 先自动保存 → 再释放资源  ← 推荐（await using）
   └── Dispose()       → 仅释放资源，不保存
```

推荐使用 `await using` 确保数据自动持久化：
```csharp
await using var db = new MyDocumentDb();
await db.LoadAsync();
// ... 操作 ...
// 作用域结束自动保存
```

---

## 11. 完整示例：人脸识别系统

```csharp
using Vorcyc.Quiver;

// 定义实体
public class FaceFeature
{
    [QuiverKey]
    public string PersonId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [QuiverVector(128, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}

// 定义上下文
public class FaceDb : QuiverDbContext
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;

    public FaceDb(string path) : base(new QuiverDbOptions
    {
        DatabasePath = path,
        StorageFormat = StorageFormat.Binary
    }) { }
}

// 使用
await using var db = new FaceDb("faces.vdb");
await db.LoadAsync();

// 注册人脸
db.Faces.Add(new FaceFeature
{
    PersonId = "P001",
    Name = "Alice",
    Embedding = GetFaceEmbedding(photo)     // 模型输出
});

// 实时识别
float[] probeVector = GetFaceEmbedding(cameraFrame);
var match = db.Faces.SearchTop1(probeVector);

if (match is { Similarity: > 0.9f })
    Console.WriteLine($"识别成功: {match.Entity.Name} (置信度: {match.Similarity:P1})");
else
    Console.WriteLine("未识别到匹配人脸");
```

---

## 12. 多向量字段（多模态）

一个实体可标注多个 `[QuiverVector]` 属性，每个字段独立维护索引，支持不同维度、度量和索引策略：

```csharp
public class MediaItem
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    [QuiverVector(384, DistanceMetric.Cosine)]
    [QuiverIndex(VectorIndexType.HNSW, M = 32)]
    public float[] TextEmbedding { get; set; } = [];

    [QuiverVector(512, DistanceMetric.Cosine)]
    [QuiverIndex(VectorIndexType.HNSW, M = 24)]
    public float[] ImageEmbedding { get; set; } = [];
}

// 按文本向量搜索
var textResults = db.Items.Search(e => e.TextEmbedding, textQuery, topK: 5);

// 按图像向量搜索
var imageResults = db.Items.Search(e => e.ImageEmbedding, imageQuery, topK: 5);
```

可选向量字段（`Optional = true`）允许向量为 null，适用于并非所有实体都具有某特征的场景（如图片中的人脸向量）。

---

## 13. 总结

| 维度 | 方案 |
|------|------|
| **定位** | 纯 .NET 嵌入式向量数据库，进程内运行，零原生依赖 |
| **建模** | EF Core 风格 Code-First，声明式 Attribute 标注 |
| **索引** | Flat / HNSW / IVF / KDTree 四种算法覆盖全场景 |
| **持久化** | JSON / XML / Binary + WAL 增量持久化（O(Δ)） |
| **迁移** | Schema Migration：属性重命名 + 值转换（ConfigureMigration），增/删字段自动处理 |
| **并发** | ReaderWriterLockSlim 读写分离锁，开箱即用 |
| **性能** | SIMD 加速（TensorPrimitives）、表达式树编译访问器、FrozenDictionary、Cosine 预归一化 |
| **安全** | WAL CRC32 校验 + 原子写入（临时文件替换）+ 崩溃恢复 |
| **框架** | .NET 10，无第三方运行时依赖 |
