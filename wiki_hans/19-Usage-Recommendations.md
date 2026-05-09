## 19. 使用建议

### 19.1 推荐组合

大库、读多写少、以 Top-K 搜索为主时，推荐组合使用：

```csharp
new QuiverDbOptions
{
	DatabasePath = "data.vdb",

	Vectors =
	{
		MemoryMode = GlobalVectorMemoryMode.Auto,
		MemoryMapThresholdBytes = 64L * 1024 * 1024,
	},
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
};
```

实体侧建议：

```csharp
public partial class Document
{
	[QuiverKey]
	public string Id { get; set; } = "";

	public string Title { get; set; } = "";

	[QuiverVector(768, DistanceMetric.Cosine, MemoryMode = VectorMemoryMode.MemoryMapped)]
	[QuiverIndex(VectorIndexType.HNSW)]
	public partial float[]? Embedding { get; set; }

	[QuiverLargeField]
	public byte[]? RawContent { get; set; }
}
```

这个组合把三类数据分开处理：实体对象由 `InMemoryEntityStore` 管理，向量由 `InMemory` / `MemoryMapped` 存储管理，大 `byte[]` 进入独立 `Blob` 段，HNSW 图结构由 `IndexSnapshot` 恢复。大字段可以按需要选择 `InMemory`、`LazyLoad` 或 `PagedCache`。

### 19.2 什么时候使用懒加载

| 机制 | 适合场景 | 收益 |
|---|---|---|
| `VectorMemoryMode.MemoryMapped` / `Auto` | 大规模读多向量库，尤其是高维 embedding | 向量从 OS 页缓存读取，降低托管堆压力 |
| 字段级 `VectorMemoryMode.MemoryMapped` | 只希望部分向量字段使用 mmap | 精细控制单个向量字段的内存策略 |
| `LargeFieldMemoryMode.LazyLoad` / `PagedCache` | 缩略图、原始文件、音频片段等大 `byte[]` 字段 | 大对象拆到独立 Blob 段，并按需物化 |
| `[QuiverLargeField]` | 需要把大 `byte[]` 从 `EntityMeta` 中拆出的字段 | 降低加载实体元数据时的大对象内存压力 |
| `HNSW + IndexSnapshot` | HNSW 大索引，加载时不希望重建图 | 加载时恢复拓扑，只补建未覆盖 id |

懒加载最有意义的场景是：几十万 / 百万级实体、高维 embedding、搜索多、属性读取少、每次只消费 topK 命中结果。

### 19.3 什么时候没必要

| 场景 | 建议 |
|---|---|
| 数据量很小，例如几千条 | `VectorMemoryMode.InMemory` 和 `LargeFieldMemoryMode.InMemory` 更简单 |
| 搜索后总是读取所有命中向量内容 | `MemoryMapped` 的属性物化收益降低 |
| 每次操作都读取所有大字段 | `LazyLoad` / `PagedCache` 可能增加 I/O 和缓存管理开销 |
| 写多读少、频繁 `Add` / `Upsert` | 优先用 `VectorMemoryMode.InMemory`，定期 `SaveAsync` |
| 向量维度低、总向量字节数小 | `MemoryMapped` / `Auto` 收益有限 |
| `byte[]` 字段很小 | `[QuiverLargeField]` 可选，不一定需要 |

### 19.4 场景示例

#### 小库：简单优先

几千条以内、向量维度不高、经常直接遍历实体时，不需要复杂的懒加载组合：

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

#### 大库搜索：mmap 向量 + HNSW 快照

适合人脸库、RAG 文档库、图片向量库等搜索多、读取少的场景：

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "vectors.vdb",
	Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped },
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
};

public partial class FaceFeature
{
	[QuiverKey]
	public string Id { get; set; } = "";

	[QuiverVector(512, DistanceMetric.Cosine, MemoryMode = VectorMemoryMode.MemoryMapped)]
	[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
	public partial float[]? Embedding { get; set; }
}
```

首次 `SaveAsync()` 会写入 HNSW `IndexSnapshot`；后续 `LoadAsync()` 会恢复图结构，搜索仍从 mmap vector store 读取向量。

#### 只有大字段很大：只启用大字段懒加载

如果实体本身很轻，但 `byte[]` 负载很大，可以只让大字段按需读取：

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "catalog.vdb",
	Vectors = { MemoryMode = GlobalVectorMemoryMode.InMemory },
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.LazyLoad }
};
```

#### 只有向量很大：只用 `MemoryMapped` / `Auto`

如果实体很轻，但向量总字节数很大，重点是把向量移出托管堆：

```csharp
var options = new QuiverDbOptions
{
	DatabasePath = "embeddings.vdb",
	Vectors =
	{
		MemoryMode = GlobalVectorMemoryMode.Auto,
		MemoryMapThresholdBytes = 128L * 1024 * 1024
	},
	LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory }
};
```

#### 大二进制字段：使用 `[QuiverLargeField]`

当实体包含缩略图、原始文件或音频片段时，把 `byte[]` 拆到独立 `Blob` 段：

```csharp
public partial class Photo
{
	[QuiverKey]
	public string Id { get; set; } = "";

	[QuiverVector(768, DistanceMetric.Cosine, MemoryMode = VectorMemoryMode.MemoryMapped)]
	public partial float[]? Embedding { get; set; }

	[QuiverLargeField]
	public byte[]? Thumbnail { get; set; }
}
```

#### 写多读少：先用 `InMemory` 批量写，周期性整理

频繁写入时优先降低写路径复杂度，批次之间显式持久化和整理：

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

批量 `AppendAsync()` + `Clear()` 场景建议使用同步 `using`；`DisposeAsync()` 只有在 `SaveOnDispose = true` 时才会再执行一次全量 `SaveAsync()`。

### 19.5 组合关系

- `VectorMemoryMode.MemoryMapped` / `Auto` 管向量底层内存位置，是降低托管堆压力的关键。
- 字段级 `VectorMemoryMode` 只在全局 `Vectors.MemoryMode = GlobalVectorMemoryMode.PerField` 时生效。
- `[QuiverLargeField]` 管大 `byte[]` 是否拆到独立 Blob 段；大字段运行时内存模式由 `LargeFields.MemoryMode` 控制。
- `LargeFieldMemoryMode.LazyLoad` 适合偶尔访问的大字段；`PagedCache` 适合存在热点重复访问的大字段。
- `IndexSnapshot` 只保存 HNSW 拓扑，不保存实体或向量副本，因此不会破坏懒加载语义。

一句话：**大数据量 + 大向量 / 大对象 + 搜索多、读取少** 时，使用 `MemoryMapped/Auto Vector + QuiverLargeField + HNSW Snapshot` 最有意义。
