# Vorcyc.Quiver 技术方案文档

> **版本**：3.2.1
> **目标框架**：.NET 10  
> **许可证**：MIT  
> **NuGet**：https://www.nuget.org/packages/Vorcyc.Quiver  
> **源码仓库**：https://github.com/vorcyc/Vorcyc.Quiver

---

## 2.0.0 更新说明（v2.x 历史变更，已整合进当前版本）

> **文件格式兼容性**：v3.1.0 完全向后兼容 v1.x、v2.x 和 v3.0.0 的二进制数据文件（Magic `QDB\x01` / `QDB\x02`）。WAL 文件均可直接加载，无需任何迁移。持久化层仅存储实体属性数据，不涉及度量/索引/相似度算法的元数据，因此架构重构对文件格式零影响。

---

---

## 3.2.1 更新说明

> **文件格式兼容性**：v3.2.1 完全向后兼容 v1.x、v2.x、v3.0.0、v3.1.0 和 v3.2.0 的所有数据文件。

### 缺陷修复：`EntityPageCache` 并发竞态

**问题**：在 `LazyPaging` 模式下，若多线程（如 `Parallel.ForEach`）同时调用 `Find` / `Search`，会在 `QuiverSet` 读锁内并发进入 `GetOrLoadPage()`，而该路径会变更内部 LRU 结构（`_loadedPages`、`_lru`、`_lruNodes`）。`ReaderWriterLockSlim` 的读锁允许多线程共享，因此并不阻止 LRU 的并发写，进而导致数据竞争和不可预期的异常（如 `KeyNotFoundException`、`InvalidOperationException`）。

**修复方案**：在 `EntityPageCache<TEntity>` 中引入独立的 `private readonly Lock _pageLock`，对所有变更 LRU 状态的路径加以保护：

| 保护路径 | 说明 |
|---------|------|
| `GetOrLoadPage()` | 命中时 `TouchLru()`、缺页时 `EvictColdest()` + `AddToLru()` + 页文件加载，全部在 `_pageLock` 内执行 |
| `FlushDirty()` | 遍历并写回脏页 |
| `CompactMemory()` | 刷写后清空整个 LRU |
| `Clear()` | 清空页目录与 LRU 结构 |

`FullMemory` 模式无任何 LRU 结构，不受影响，零开销。

---

## 3.2.0 更新说明

> **文件格式兼容性**：v3.2.0 完全向后兼容 v1.x、v2.x、v3.0.0 和 v3.1.0 的所有数据文件。

### 新增：`CompactMemory` 按需内存压缩

**背景**：`LazyPaging` 模式下，LRU 缓存在批量导入完成后仍会占用 `MaxCachedPages` 页的内存。用户希望在导入批次结束后主动释放这部分内存，同时保持向量索引常驻。

**新增 API**：

| 位置 | 方法 | 说明 |
|------|------|------|
| `EntityPageCache<T>` | `CompactMemory()` | 内部实现：刷写所有脏页 → 清空 `_loadedPages` / `_lru` / `_lruNodes` → 分配新写入页。`FullMemory` 模式为空操作。 |
| `QuiverSet<T>` | `CompactMemory()` | 在写锁内呼叫 `_entities.CompactMemory()`。 |
| `QuiverSet<T>` | `CompactMemoryAsync()` | `Task.Run(CompactMemory)` 异步版本。 |
| `QuiverDbContext` | `CompactAllMemoryAsync()` | 并行调用所有 `QuiverSet` 的 `CompactMemoryAsync()`，`Task.WhenAll` 等待全部完成。 |

**使用示例**：

```csharp
// 单个集合压缩
await db.Documents.CompactMemoryAsync();

// 整个上下文一次性压缩
await db.CompactAllMemoryAsync();
```

**设计决策**：
- `_idToPage` 和 `_pageFiles` 目录索引保留，压缩后的读取会触发页面按需重新加载，行为完全正确。
- 向量索引结构（HNSW/IVF/KDTree 等）始终常驻内存，搜索性能不受影响。
- `FullMemory` 模式调用时静默忽略（no-op），不抛异常。

---

## 3.1.0 更新说明

### 重大变更（Breaking Changes）

#### 1. 移除 `VectorStorageMode` 与 `MmapVectorStore`

**背景**：v3.0.0 引入了 `EntityCacheMode.LazyPaging`，实体对象按页按需加载。实体对象本身已包含向量字段，因此实体分页已内在地控制了向量的内存占用。在此背景下，`VectorStorageMode.MemoryMapped`（热路径上将向量单独存储到 OS mmap 区域）与 LazyPaging 实为重叠设计，并引入了额外复杂度。

**变更内容**：

| 层 | 移除内容 |
|----|----------|
| `Vorcyc.Quiver.Attributes.cs` | `VectorStorageMode` 枚举（`Heap` / `MemoryMapped`） |
| `Vorcyc.Quiver.QuiverDbOptions.cs` | `VectorStorage` 属性 |
| `Vorcyc.Quiver.Indexing.MmapVectorStore.cs` | 整个文件 |
| `QuiverSet` 构造函数 | `defaultMetric` 参数 |

**向量存储层现在统一使用 `HeapVectorStore`**（GC 堆，`Dictionary<int, float[]>`），与 `LazyPaging` 分页缓存协同控制总内存占用。

**对现有代码的影响**：

```csharp
// 删除以下行（如有），其余无需修改。数据文件完全兼容。
// VectorStorage = VectorStorageMode.MemoryMapped
```

#### 2. `QuiverSet` 构造函数移除 `defaultMetric` 参数

**变更前**：`internal QuiverSet(string? databasePath, EntityCacheMode entityCache, int maxCachedPages, int pageSize, DistanceMetric defaultMetric)`  
**变更后**：`internal QuiverSet(string? databasePath, EntityCacheMode entityCache, int maxCachedPages, int pageSize)`

每个向量字段已通过 `[QuiverVector(dim, metric)]` 独立声明度量，不再依赖构造时传入的全局默认度量。

---

## 3.0.0 更新说明

### 新增：`EntityPageCache<T>` 懒加载分页缓存

**背景与动机**

在 v2.x 中，`QuiverSet<TEntity>` 内部使用 `Dictionary<int, TEntity>` 存储全量实体对象。当实体数量达到百万级，实体对象本身占用内存较大时，内存压力极为显著。

**v3.0.0 方案**：插入 `EntityPageCache<TEntity>` 分页缓存层，替代原有的全量 `Dictionary<int, TEntity>`。实体对象按页组织，页内数据序列化到二进制页文件，仅将活跃工作集页驻留内存。

**核心组件**：

```csharp
internal sealed class EntityPageCache<TEntity>
{
    // FullMemory 模式：直接使用 Dictionary，附加层纶薄包装。与 v2.x 行为完全一致
    // LazyLoading 模式：维护 LRU 双联链表，页文件按需加载，冗余页驱逐后写回磁盘
}
```

**内存布局（LazyLoading 模式）**：

```
內存工作集（最多 MaxCachedPages 页）
  ├─ Page 7  实体 3584..4095  ← LRU head（最近访问）
  ├─ Page 3  实体 1536..2047
  └─ Page 0  实体    0..511   ← LRU tail（最久未访问，下一个被驱递）

磁盘页文件（.qvpg）
  ├─ page_00000000.qvpg  实体 0..511
  ├─ page_00000001.qvpg  实体 512..1023
  └─ ...
```

**页文件格式**：

```
[4B] Magic = 0x51565047 ("QVPG")     ← 页文件标识
[1B] Version = 0x01                  ← 格式版本（v1 = 紧凑二进制）
[4B] PropCount (int32)               ← 属性描述符数量
└─ PropDescriptor × PropCount:
   [string] PropName                 ← BinaryWriter 长度前缀 UTF-8
[4B] EntityCount (int32)             ← 本页实体数
└─ Entity × EntityCount:
   [4B] InternalId (int32)
   └─ 按描述符顺序逐字段写入：[1B null标志] + 字段值（同 BinaryStorageProvider 类型编码）
```

**与 v2.x 对比**：

| 维度 | v2.x 全量内存 | v3.0 懒加载分页 |
|------|--------------|------------------|
| 内存占用 | O(N × 实体大小) | O(MaxCachedPages × PageSize × 实体大小) |
| 实体访问延迟 | ~ns（Dict） | 热页 ~ns；冷页 ~微秒（磁盘 I/O） |
| 向量索引 | 常驻 | 常驻（不受影响） |
| 持久化兼容 | — | 完全兼容 |
| 外部 API | — | 完全不变 |

**适用场景**：实体对象本身内存占用大、数据集超大（百万级）、实体访问模式具有局部性（局部性越好缓存命中率越高）。

**配置方式**：

```csharp
var options = new QuiverDbOptions
{
    DatabasePath = "mydata.vdb",
    EntityCache = EntityCacheMode.LazyPaging,  // 页文件存储在 mydata.vdb.pages/ 目录下
    MaxCachedPages = 32,
    PageSize = 512
};
```

---

## 2.0.0 更新说明（历史，供参考）

> **文件格式兼容性**：v2.0.0 完全向后兼容 v1.x 的数据文件。二进制格式（Magic `QDB\x01`）和 WAL 文件均可直接加载，无需任何迁移。持久化层仅存储实体属性数据，不涉及度量/索引/相似度算法的元数据，因此架构重构对文件格式零影响。

---

### 变更一

**v1.x 方案**：使用 `delegate float SimilarityFunc(ReadOnlySpan<float> a, ReadOnlySpan<float> b)` 委托，绑定 `TensorPrimitives` 方法组。

**v1.x 存在的问题**：
- 委托调用存在 ~2ns 间接调用开销（函数指针跳转），热路径上累积显著
- 仅支持 `float`，无法泛化到 `double` / `Half` 等多精度场景
- 用户无法注入自定义度量——度量选择完全由框架内部的 `DistanceMetric` 枚举决定

**v2.0.0 方案**：

```csharp
// 接口定义
public interface ISimilarity<T> where T : unmanaged, INumber<T>, IRootFunctions<T>
{
    static abstract T Compute(ReadOnlySpan<T> x, ReadOnlySpan<T> y);
}

// 内置实现示例（零大小 readonly struct）
public readonly struct DotProductSimilarity : ISimilarity<float>
{
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => TensorPrimitives.Dot(x, y);
}
```

**原理**：`static abstract` + `readonly struct` 是 .NET 7+ 引入的泛型特化模式。当索引代码以 `TSim.Compute(x, y)` 调用时，JIT 为每个具体的 `TSim` 类型（如 `DotProductSimilarity`）生成独立的机器码副本，`Compute()` 在调用站点被直接内联为 SIMD 指令序列——无虚方法表查找、无委托函数指针跳转。

**对比**：

| 维度 | v1 `SimilarityFunc` 委托 | v2 `ISimilarity<T>` 静态抽象 |
|------|-------------------------|------------------------------|
| 分派机制 | 间接调用（函数指针跳转，~2ns） | JIT 内联，零开销 |
| 泛型能力 | 仅 `float` | 泛型 `T : unmanaged, INumber<T>, IRootFunctions<T>`（float / double / Half） |
| 用户可扩展性 | ❌ 框架内部封闭 | ✅ 用户实现 `ISimilarity<float>` + `[QuiverVector(CustomSimilarity = typeof(...))]` |
| 结构体大小 | N/A（委托是引用类型，24 字节堆对象） | 0 字节（`readonly struct` 无字段，JIT 完全消除） |

**用户自定义度量用法**：

```csharp
// 1. 实现接口
public readonly struct WeightedL1Similarity : ISimilarity<float>
{
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        float sum = 0f;
        for (int i = 0; i < x.Length; i++)
            sum += MathF.Abs(x[i] - y[i]) * (i < 128 ? 2f : 1f);
        return 1f / (1f + sum);
    }
}

// 2. 在实体上使用（设置 CustomSimilarity 后忽略 metric 参数）
[QuiverVector(256, CustomSimilarity = typeof(WeightedL1Similarity))]
public float[] Embedding { get; set; } = [];
```

---

### 变更二：IVectorStore 向量存储抽象（索引与数据解耦）

**v1.x 方案**：每个索引实现（FlatIndex / HnswIndex / IvfIndex / KDTreeIndex）内部各自持有 `Dictionary<int, float[]>`，向量数据分散在各索引中。

**v1.x 存在的问题**：
- 多向量字段实体的向量数据在每个索引中独立存储，无法统一管理
- 无法在不修改索引代码的情况下切换向量的物理存放位置（堆 vs mmap）
- 索引实现中混杂了数据存储逻辑（Store / Get / Remove）和拓扑管理逻辑（图/树/倒排），职责不单一

**v2.0.0 方案**：

```csharp
// 向量存储抽象接口
internal interface IVectorStore : IDisposable
{
    int Count { get; }
    IEnumerable<int> Ids { get; }
    void Store(int id, ReadOnlySpan<float> vector);
    ReadOnlySpan<float> Get(int id);
    bool Contains(int id);
    void Remove(int id);
    void Clear();
}
```

索引实现不再持有向量数据，仅管理拓扑结构（HNSW 的多层邻居图、IVF 的倒排列表、KDTree 的空间二叉树），通过构造注入的 `IVectorStore` 读写向量。

**实现**：

| 实现 | 向量存储位置 | GC 压力 | 访问延迟 | 适用场景 |
|------|-------------|---------|---------|----------|
| `HeapVectorStore` | GC 托管堆（`Dictionary<int, float[]>`） | 有 | 最低 | 默认模式，全局唯一实现 |

> **注**：`MmapVectorStore`（mmap 实现）在 v2.0.0 引入，已在 v3.1.0 移除。详见 3.1.0 更新说明。

---

### 变更三：6 种新距离度量（3 → 9 种内置）

v1.x 仅支持 Cosine / Euclidean / DotProduct 共 3 种度量。v2.0.0 新增以下 6 种，加上原有 3 种共 9 种内置度量，全部使用 `Vector<float>` SIMD 加速：

| 新增度量 | 公式 | 值域 | SIMD 策略 | 适用场景 |
|---------|------|------|-----------|----------|
| **Manhattan** | $\frac{1}{1+\sum\|x_i-y_i\|}$ | (0, 1] | `Vector.Abs(vx - vy)` 累加 | 稀疏特征、推荐系统 |
| **Chebyshev** | $\frac{1}{1+\max\|x_i-y_i\|}$ | (0, 1] | `Vector.Max(vmax, Vector.Abs(...))` 追踪 | 特征偏差检测、棋盘距离 |
| **Pearson** | $\frac{\sum(x_i-\bar{x})(y_i-\bar{y})}{\sqrt{\sum(x_i-\bar{x})^2\sum(y_i-\bar{y})^2}}$ | [-1, 1] | Pass 1 `TensorPrimitives.Sum` 求均值 + Pass 2 三路 `Vector<float>` 累加 | 去偏置文本嵌入、TF-IDF |
| **Hamming** | $\frac{\text{matchCount}}{n}$ | [0, 1] | `Vector.Equals` + `Vector.ConditionalSelect` 掩码计数 | 二值哈希码、LSH 指纹 |
| **Jaccard** | $\frac{\sum\min(x_i,y_i)}{\sum\max(x_i,y_i)}$ | [0, 1] | `Vector.Min` / `Vector.Max` 并行累加 | BoW/TF-IDF 稀疏特征 |
| **Canberra** | $1-\frac{1}{n}\sum\frac{\|x_i-y_i\|}{\|x_i\|+\|y_i\|}$ | [0, 1] | `Vector.ConditionalSelect` 安全除法（零分母 → 1） | 稀疏数据、化学指纹 |

**SIMD 实现模式**（所有 6 种新度量共享）：

```
if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
    SIMD 主循环：一次处理 Vector<float>.Count 个 float
                 （SSE4=4, AVX2=8, AVX-512=16）
标量尾部循环：处理剩余 n % Vector<float>.Count 个元素
```

零堆分配，纯寄存器操作。`Vector<float>.Count` 在运行时自动匹配最宽可用寄存器。

**原有 3 种度量的 SIMD 后端**（未变）：

| 度量 | SIMD 后端 |
|------|-----------|
| Cosine | `TensorPrimitives.CosineSimilarity`（预归一化时用 `TensorPrimitives.Dot`） |
| DotProduct | `TensorPrimitives.Dot` |
| Euclidean | `TensorPrimitives.Distance` |

---

### 变更四：QuiverVectorAttribute 新增 CustomSimilarity 属性

```csharp
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class QuiverVectorAttribute(int dimensions, DistanceMetric metric = DistanceMetric.Cosine) : Attribute
{
    public int Dimensions { get; } = dimensions;
    public DistanceMetric Metric { get; } = metric;
    public bool Optional { get; set; }
    public Type? CustomSimilarity { get; set; }  // ← v2.0.0 新增
}
```

当 `CustomSimilarity` 不为 `null` 时，`Metric` 参数被忽略。框架通过反射验证类型须为实现 `ISimilarity<float>` 的 `struct`，然后通过泛型特化路径调度。

---

### 变更五：QuiverDbOptions 新增 EntityCache 配置

```csharp
public class QuiverDbOptions
{
    // ... 原有属性不变 ...
    public EntityCacheMode EntityCache { get; set; } = EntityCacheMode.FullMemory;  // ← v3.0.0 新增
}

public enum EntityCacheMode
{
    FullMemory,  // 全量常驻内存（默认，与 v2.x 行为一致）
    LazyPaging   // LRU 分页缓存（按需加载，须设置 DatabasePath）
}
```

启动时 `QuiverDbOptions.Validate()` 校验：`EntityCacheMode.LazyPaging` 模式必须设置 `DatabasePath`。

---

### 文件格式兼容性分析

| 层 | 是否持久化 | 说明 |
|----|----------|------|
| 实体数据（属性名、属性值、`float[]` 向量） | ✅ | 不变，二进制格式和 WAL 均如此 |
| `DistanceMetric` 枚举 | ❌ | 在 `[QuiverVector]` 特性上声明，仅影响运行时索引构建 |
| `VectorIndexType` / HNSW 参数 | ❌ | 在 `[QuiverIndex]` 特性上声明，索引在 `LoadAsync()` 后重建 |
| `ISimilarity<T>` 实例 | ❌ | 构造时由泛型特化创建，是纯内存对象 |
| `IVectorStore` 实例 | ❌ | 运行时创建（始终为 `HeapVectorStore`），不持久化 |

**结论**：v1.x、v2.x 及 v3.0.0 的 JSON / XML / Binary / WAL 文件均可被 v3.1.0 直接加载，无需任何数据迁移。

---

## 1. 项目概述

**Vorcyc.Quiver** 是一款纯 .NET 实现的嵌入式向量数据库。它以 NuGet 包的形式引入项目，进程内运行，无需部署独立的数据库服务器，也无任何原生（Native）依赖。

项目借鉴 EF Core 的 `DbContext` 设计模式，开发者通过声明式特性标注（`[QuiverKey]`、`[QuiverVector]`、`[QuiverIndex]`）定义实体与索引策略，框架在运行时自动完成模型发现、索引构建和持久化管理。

### 1.1 核心特性

| 特性 | 说明 |
|------|------|
| **Code-First 声明式建模** | 类似 EF Core，通过 Attribute 标注实体类，框架自动发现并注册 `QuiverSet<T>` 集合 |
| **多种 ANN 索引** | 内置 Flat（暴力搜索）、HNSW、IVF、KDTree 四种索引算法 |
| **9 种距离度量 + 自定义相似度** | 内置 Cosine / Euclidean / DotProduct / Manhattan / Chebyshev / Pearson / Hamming / Jaccard / Canberra；支持用户实现 `ISimilarity<float>` 接入自定义度量 |
| **二进制持久化** | 使用高性能二进制格式（`BinaryStorageProvider`）作为主存储路径，以及 WAL 增量持久化；JSON / XML 仅作为导出/导入格式（`ExportAsync` / `ImportAsync`） |
| **并发安全** | `QuiverSet<T>` 内部通过 `ReaderWriterLockSlim` 实现读写分离，开箱即用 |
| **SIMD 硬件加速** | 全部 9 种相似度实现基于 `TensorPrimitives` + `Vector<float>` SIMD 指令，自动适配 SSE4 / AVX2 / AVX-512 |
| **Schema Migration** |
| **懒加载分页缓存** | `EntityCache = EntityCacheMode.LazyPaging` — 实体对象按页按需加载，LRU 驱递冷页，内存上限可控；向量索引常驻内存 |

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
│   ├── Similarity/
│   │   ├── ISimilarity.cs            # 相似度静态抽象接口（static abstract + 泛型）
│   │   ├── CosineSimilarity.cs       # 余弦相似度
│   │   ├── DotProductSimilarity.cs   # 点积相似度
│   │   ├── EuclideanSimilarity.cs    # 欧几里得距离转相似度
│   │   ├── ManhattanSimilarity.cs    # 曼哈顿距离转相似度（SIMD 加速）
│   │   ├── ChebyshevSimilarity.cs    # 切比雪夫距离转相似度（SIMD 加速）
│   │   ├── PearsonCorrelationSimilarity.cs  # 皮尔逊相关系数（SIMD 加速）
│   │   ├── HammingSimilarity.cs      # 汉明相似度（SIMD 加速）
│   │   ├── JaccardSimilarity.cs      # 广义 Jaccard 相似度（SIMD 加速）
│   │   └── CanberraSimilarity.cs     # 堪培拉距离转相似度（SIMD 加速）
│   ├── Indexing/
│   │   ├── IVectorIndex.cs           # 索引统一接口
│   │   ├── IVectorStore.cs           # 向量存储抽象接口
│   │   ├── HeapVectorStore.cs        # GC 堆向量存储（默认，唯一实现）
│   │   ├── FlatIndex.cs              # 暴力搜索索引（>10K 自动并行）
│   │   ├── HnswIndex.cs             # HNSW 分层图索引
│   │   ├── IvfIndex.cs              # IVF 倒排文件索引（K-Means 聚类）
│   │   └── KDTreeIndex.cs           # KD-Tree 空间二叉树索引
│   ├── Paging/
│   │   └── EntityPageCache.cs        # 懒加载 LRU 分页缓存（v3 新增）
│   └── Storage/
│       ├── IStorageProvider.cs       # 导出/导入接口 + ExportStorageProviderFactory
│       ├── JsonExportProvider.cs     # JSON 导出/导入（System.Text.Json）
│       ├── XmlExportProvider.cs      # XML 导出/导入（XDocument + Base64）
│       ├── BinaryStorageProvider.cs  # 主持久化存储（自定义协议 + MemoryMarshal 零拷贝）
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
│  EntityPageCache<T> —— LRU 分页缓存（v3）    │
├─────────────────────────────────────────────┤
│              相似度层                         │
│  ISimilarity<T> → 9 种内置 + 用户自定义      │
├─────────────────────────────────────────────┤
│              索引层                           │
│  IVectorIndex → Flat / HNSW / IVF / KDTree  │
│  IVectorStore → HeapVectorStore              │
├─────────────────────────────────────────────┤
│              存储层                           │
│  BinaryStorageProvider (主持久化路径)        │
│  IStorageProvider → JsonExportProvider /     │
│                      XmlExportProvider       │
│  WriteAheadLog (WAL 增量持久化)              │
└─────────────────────────────────────────────┘
```

### 3.2 核心组件关系

| 组件 | 类型 | 职责 |
|------|------|------|
| `QuiverDbContext` | 抽象类 | 数据库上下文基类；反射发现 `QuiverSet` 属性、管理持久化读写和生命周期 |
| `QuiverSet<TEntity>` | partial 类 | 向量集合；提供 CRUD + 多种搜索模式 + 变更追踪；内部 `ReaderWriterLockSlim` 读写锁 |
| `ISimilarity<T>` | 公共接口 | 静态抽象相似度计算契约；JIT 为每个具体类型内联，零虚分派 |
| `IVectorIndex` | 内部接口 | 统一索引契约（Add / Remove / Clear / Search / SearchByThreshold） |
| `IVectorStore` | 内部接口 | 向量数据存储抽象；将向量所有权从索引拓扑中剥离 |
| `HeapVectorStore` | 内部密封类 | GC 堆向量存储（`Dictionary<int, float[]>`），唯一活跃实现 |
| `IStorageProvider` | 内部接口 | 导出/导入多态契约（`ExportAsync` / `ImportAsync` 使用）；由 `ExportStorageProviderFactory` 工厂创建 `JsonExportProvider` / `XmlExportProvider` |
| `WriteAheadLog` | 内部密封类 | WAL 引擎；自定义二进制格式 + CRC32 校验 + 崩溃恢复 |
| `QuiverDbOptions` | 配置类 | 全局选项：存储路径、默认度量、格式、内存模式、WAL 配置等 |
| `QuiverSearchResult<T>` | record | 搜索结果 DTO，包含 `Entity` 和 `Similarity` |
| `EntityPageCache<TEntity>` | 内部密封类 | LRU 分页缓存（v3 新增）；按页按需加载实体对象，冷页 LRU 驱逐后刷盘，向量索引不受影响 |

### 3.3 关键设计决策

1. **表达式树编译属性访问器**：使用 `Expression.Lambda<Func<T,R>>(...).Compile()` 替代反射的 `PropertyInfo.GetValue`，性能提升约 100 倍（纳秒级 vs 微秒级）。
2. **FrozenDictionary 元数据存储**：向量字段元信息和索引映射使用 `FrozenDictionary` 冻结，零堆分配查找。
3. **Cosine 预归一化优化**：写入时自动 L2 归一化向量，搜索时以 `Dot` 替代 `CosineSimilarity`（从 3 次向量遍历降为 1 次）。
4. **ISimilarity\<T\> 静态抽象接口**：使用 `static abstract` + `readonly struct` 替代 v1 的 `SimilarityFunc` 委托，JIT 为每个具体类型生成特化机器码，在调用站点直接内联 `TSim.Compute()`，零虚分派、零委托间接调用。用户可通过 `[QuiverVector(CustomSimilarity = typeof(...))]` 接入自定义度量。
5. **IVectorStore 向量存储抽象**：将向量数据所有权从索引实现中剥离，索引仅管理拓扑结构。v3.1.0 起仅保留 `HeapVectorStore`（GC 堆）一种实现；`MmapVectorStore` 已移除。
6. **EntityPageCache<T> 懒加载分页缓存**（v3 新增）：以 LRU 双链表 + 分页文件替代全量 `Dictionary<int, TEntity>`，内存占用固定上限（`MaxCachedPages × PageSize`），向量索引仍常驻内存保持搜索性能不变。`QuiverSet<T>` 的外部 API 完全不变。
6. **原子写入策略**：`SaveAsync` 先写临时文件，成功后 `File.Move` 替换，防止写入中途崩溃导致数据损坏。

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
        DatabasePath = "documents.vdb",
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
| `Manhattan` | 1 / (1 + Σ\|aᵢ - bᵢ\|) | (0, 1] | 稀疏特征、推荐系统 | ❌ |
| `Chebyshev` | 1 / (1 + max\|aᵢ - bᵢ\|) | (0, 1] | 特征偏差检测、棋盘距离 | ❌ |
| `Pearson` | Σ(aᵢ-ā)(bᵢ-b̄) / √(Σ(aᵢ-ā)²Σ(bᵢ-b̄)²) | [-1, 1] | 去偏置文本嵌入、TF-IDF、评分向量 | ❌ |
| `Hamming` | matchCount / n | [0, 1] | 二值哈希码、LSH、SimHash 指纹 | ❌ |
| `Jaccard` | Σmin(aᵢ,bᵢ) / Σmax(aᵢ,bᵢ) | [0, 1] | BoW/TF-IDF 稀疏特征、直方图 | ❌ |
| `Canberra` | 1 - (1/n)Σ\|aᵢ-bᵢ\|/(\|aᵢ\|+\|bᵢ\|) | [0, 1] | 稀疏数据、化学指纹 | ❌ |

此外可通过 `[QuiverVector(dim, CustomSimilarity = typeof(...))]` 接入用户自定义的 `ISimilarity<float>` 实现。

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
dotnet add package Vorcyc.Quiver --version 3.0.0
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
| `EntityCache` | `EntityCacheMode` | `FullMemory` | 实体缓存模式：`FullMemory`（全量常驻）/ `LazyPaging`（LRU 分页，须设置 `DatabasePath`） |
| `EnableWal` | `bool` | `false` | 是否启用 WAL |
| `WalCompactionThreshold` | `int` | `10,000` | WAL 自动压缩阈值 |
| `WalFlushToDisk` | `bool` | `true` | WAL 写入后是否 fsync |
| `MaxCachedPages` | `int` | `16` | 每个 `QuiverSet` 内存最多保留的页数 |
| `PageSize` | `int` | `512` | 每页最多容纳的实体数量 |

---

## 7. 存储方案

### 7.1 持久化路径

v3.0.0 采用**二进制优先**架构：`BinaryStorageProvider`（自定义协议 + `MemoryMarshal` 零拷贝）是唯一的主持久化路径，提供最小体积与最快读写速度。JSON / XML 格式不再作为主存储，仅用于数据的**导出与导入**：

```csharp
// 导出为 JSON
await db.ExportAsync("backup.json", ExportFormat.Json);

// 从 JSON 导入（合并到当前内存状态）
await db.ImportAsync("backup.json", ExportFormat.Json);

// 导出为 XML
await db.ExportAsync("backup.xml", ExportFormat.Xml);
```

| 导出格式 | 实现类 | 可读性 | 适用场景 |
|---------|--------|--------|----------|
| `ExportFormat.Json` | `JsonExportProvider` (System.Text.Json) | ✅ 优 | 调试、跨系统数据迁移 |
| `ExportFormat.Xml` | `XmlExportProvider` (XDocument + Base64) | ✅ 良 | 兼容性需求 |

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

二进制存储格式、JSON/XML 导出格式和 WAL 回放均支持迁移规则。

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
        DatabasePath = path
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
| **度量** | 9 种内置度量 + `ISimilarity<T>` 自定义扩展 |
| **存储** | `IVectorStore` 抽象：HeapVectorStore（GC 堆，唯一活跃实现） |
| **实体缓存** | `EntityPageCache<T>`：`FullMemory`（全量常驻，v2 行为）/ `LazyPaging`（LRU 分页，v3 新增） |
| **内存压缩** | `CompactMemory()` / `CompactMemoryAsync()`（`QuiverSet`）、`CompactAllMemoryAsync()`（`QuiverDbContext`）—— 按需刷盘并驱逐全部缓存页；向量索引不受影响 |
| **持久化** | Binary 主存储（`BinaryStorageProvider`）+ WAL 增量持久化（O(Δ)）；JSON / XML 仅作为导出/导入格式（`ExportAsync` / `ImportAsync`） |
| **迁移** | Schema Migration：属性重命名 + 值转换（ConfigureMigration），增/删字段自动处理 |
| **并发** | ReaderWriterLockSlim 读写分离锁，开箱即用 |
| **性能** | 全量 SIMD 加速（TensorPrimitives + Vector\<float\>）、ISimilarity JIT 内联、表达式树访问器、FrozenDictionary、Cosine 预归一化 |
| **安全** | WAL CRC32 校验 + 原子写入（临时文件替换）+ 崩溃恢复 |
| **框架** | .NET 10，无第三方运行时依赖 |
## 2.0.0 更新说明（历史，供参考）