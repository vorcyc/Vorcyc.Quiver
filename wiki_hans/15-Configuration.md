## 13. 配置选项

`QuiverDbOptions` 提供以下配置：

```csharp
var options = new QuiverDbOptions
{
    // 数据库文件路径。null 时使用内存模式（不持久化）
    DatabasePath = @"C:\Data\MyQuiverDb.vdb",

    // 默认距离度量（实体级 [QuiverVector] 特性可覆盖）
    DefaultMetric = DistanceMetric.Cosine,

    // ── 大字段负载内存模式 ──
    LargeFields =
    {
        MemoryMode = GlobalLargeFieldMemoryMode.InMemory,
        MaxCachedPayloads = 128,
    },

    // ── v4 向量内存模式 ──
    Vectors =
    {
        MemoryMode = GlobalVectorMemoryMode.Auto,     // InMemory / LazyLoad / MemoryMapped / Auto / PerField
        MemoryMapThresholdBytes = 256L * 1024 * 1024,  // 文件超过阈值时 Auto 选择 MemoryMapped
    },

    // ── 后台自动 Merge（重写）──
    EnableBackgroundMerge = true,
    AutoMergeMaxSegments = 32,        // 段数超过阈值时触发
    AutoMergeTombstoneRatio = 0.25    // 墓碑占比超过阈值时触发
};
```

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `DatabasePath` | `string?` | `null` | 存储路径，`null` 为内存模式（`SaveAsync` 需显式传 `path`） |
| `DefaultMetric` | `DistanceMetric` | `Cosine` | 默认距离度量 |
| `LargeFields.MemoryMode` | `GlobalLargeFieldMemoryMode` | `InMemory` | 大字段内存模式：`InMemory` / `LazyLoad` / `PagedCache` / `PerField` |
| `LargeFields.MaxCachedPayloads` | `int` | `128` | `PagedCache` 模式下每个 set 的大字段缓存数量上限 |
| `Vectors.MemoryMode` | `GlobalVectorMemoryMode` | `InMemory` | 向量内存模式：`InMemory` / `LazyLoad` / `MemoryMapped` / `Auto` / `PerField` |
| `Vectors.MemoryMapThresholdBytes` | `long` | `256 MiB` | 打开数据库时用于 `GlobalVectorMemoryMode.Auto` 的文件大小阈值：现有数据库文件达到该大小时以 `MemoryMapped` 打开向量 |
| `Vectors.MaxInMemoryBytes` | `long` | `1 GiB` | 运行时 heap 向量字节预算，配合 `Vectors.AutoPromoteToMemoryMapped` 使用 |
| `Vectors.AutoPromoteToMemoryMapped` | `bool` | `false` | heap 向量超过 `Vectors.MaxInMemoryBytes` 后，是否允许运行时从 `InMemory` 自动提升到 `MemoryMapped` |
| `EnableBackgroundMerge` | `bool` | `false` | `AppendAsync` 后是否自动检查并触发 `SaveAsync` |
| `AutoMergeMaxSegments` | `int` | `32` | 段数阈值 |
| `AutoMergeTombstoneRatio` | `double` | `0.25` | 墓碑占比阈值 |
| `SaveOnDispose` | `bool` | `false` | 为 `true` 时，`DisposeAsync()` 在释放资源前自动调用 `SaveAsync()` |

### PerField 字段级内存模式

当 `Vectors.MemoryMode = GlobalVectorMemoryMode.PerField` 时，每个 `[QuiverVector]` 字段使用自身的 `MemoryMode`。如果字段未显式设置，不会报错；属性默认值是 `VectorMemoryMode.InMemory`，所以该字段保持内存模式。

当 `LargeFields.MemoryMode = GlobalLargeFieldMemoryMode.PerField` 时，每个 `[QuiverLargeField]` 字段使用自身的 `MemoryMode`。如果字段未显式设置，不会报错；属性默认值是 `LargeFieldMemoryMode.InMemory`，所以该字段按内存模式加载。

`Auto` / `PerField` 只存在于全局枚举（`GlobalVectorMemoryMode` / `GlobalLargeFieldMemoryMode`）；字段级枚举只支持具体策略。

### 向量阈值区别

`Vectors.MemoryMapThresholdBytes` 是打开数据库时的阈值，只影响 `GlobalVectorMemoryMode.Auto`，判断依据是现有数据库文件大小。`Vectors.MaxInMemoryBytes` 是运行过程中的内存预算，判断依据是当前进程里托管堆向量占用；只有启用 `Vectors.AutoPromoteToMemoryMapped` 时，超过该预算才会触发保存并提升到 mmap。

> ⚠️ **Native AOT 兼容性**：`QuiverDbOptions` 本身是 AOT 安全的（普通 POCO）。但使用它的 `QuiverDbContext` **不兼容 Native AOT**——它在启动时通过运行时反射发现 `QuiverSet<T>` 属性，并编译表达式树属性访问器。请勿以 `PublishAot=true` 发布 Quiver 应用程序。

---

