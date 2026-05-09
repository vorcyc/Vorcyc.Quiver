## 16. API 参考速查表

### QuiverDbContext

| 方法 / 属性 | 返回类型 | 说明 |
|------------|---------|------|
| `Set<TEntity>()` | `QuiverSet<TEntity>` | 按类型获取向量集合（未注册抛异常） |
| `SaveAsync(path?)` | `Task` | 异步全量原子快照（临时文件 + File.Move） |
| `AppendAsync(path?)` | `Task` | 将当前内存实体作为新段追加到现有 v4 文件，仅重写 footer。O(Δ) |
| `FlushTombstonesAsync(path?)` | `Task` | 仅将待删除主键作为 `Tombstone` 段追加，不重写存活实体 |
| `LoadAsync(path?)` | `Task` | 异步按顺序回放段 + 应用墓碑 + （可选）重绑 mmap region；文件不存在静默返回 |
| `ExportAsync(path, format)` | `Task` | 导出为 JSON / XML 旁路 |
| `ImportAsync(path, format)` | `Task` | 从 JSON / XML 导入到当前内存 |
| `Dispose()` | `void` | 同步释放（不保存） |
| `DisposeAsync()` | `ValueTask` | 异步释放资源；仅当 `SaveOnDispose = true` 时才会先调用 `SaveAsync()` |

### 文件格式迁徙（Vorcyc.Quiver.Migration）

| 类型 / 方法 | 说明 |
|-------------|------|
| `QuiverMigrator.MigrateAsync(sourceFile, destinationFile, typeMap, migrationRules?, options?)` | 将 v1/v2/v3 旧 `.vdb` 文件离线升级为当前 v4 `QDB\x04` 格式。可同时应用 Schema 迁移规则。 |
| `MigrateOptions.Overwrite` | 目标文件已存在时是否允许覆盖。 |
| `MigrateOptions.DeleteSourceOnSuccess` | 迁徙成功后是否删除源文件。 |
| `MigrateOptions.AllowNoop` | 源文件已经是 v4 且源/目标相同时是否允许直接返回。 |

迁徙时必须提供 `typeMap`（文件中的类型全名 → 当前 CLR 类型）。如果旧文件存在属性重命名，应通过 `migrationRules` 传入 `SchemaMigrationRule`，否则旧字段可能在解码阶段被跳过。

### QuiverSet\<TEntity\>

#### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `Count` | `int` | 实体数量（读锁保护，线程安全） |
| `VectorFields` | `IReadOnlyDictionary<string, int>` | 向量字段名 → 维度的只读映射（惰性缓存） |

#### 枚举

| 方法 | 返回类型 | 锁 | 说明 |
|------|---------|-----|------|
| `GetEnumerator()` | `IEnumerator<TEntity>` | 读锁（快照） | 支持 `foreach` 和 LINQ。读锁内拍快照，释放后枚举 |

#### CRUD 方法

| 方法 | 返回类型 | 锁 | 说明 |
|------|---------|-----|------|
| `Add(entity)` | `void` | 写锁 | 添加单个实体（主键重复抛异常） |
| `AddRange(entities)` | `void` | 写锁 | 批量添加（原子，两阶段提交） |
| `AddRangeAsync(entities, ct)` | `Task` | 写锁 | 异步批量添加（`Task.Run`） |
| `Upsert(entity)` | `void` | 写锁 | 插入或更新（单次写锁内完成） |
| `Remove(entity)` | `bool` | 写锁 | 按实体主键删除 |
| `RemoveByKey(key)` | `bool` | 写锁 | 按主键值删除 |
| `Find(key)` | `TEntity?` | 读锁 | 按主键查找，O(1) |
| `Exists(key)` | `bool` | 读锁 | 按主键判断存在性，O(1)（仅查 `_keyToId`） |
| `Exists(predicate)` | `bool` | 读锁 | 按条件判断存在性，O(n) 短路（命中即返回，无快照分配） |
| `Clear()` | `void` | 写锁 | 清空全部数据 + 索引 |

#### 搜索方法（同步）

| 方法 | 返回类型 | 说明 |
|------|---------|------|
| `Search(selector, query, topK)` | `List<QuiverSearchResult<T>>` | Top-K 搜索 |
| `Search(selector, query, topK, Expression filter)` | `List<QuiverSearchResult<T>>` | 带表达式过滤 |
| `Search(selector, query, topK, Func filter, overFetchMultiplier)` | `List<QuiverSearchResult<T>>` | 带委托过滤 + 过采样 |
| `SearchByThreshold(selector, query, threshold)` | `List<QuiverSearchResult<T>>` | 阈值搜索 |
| `SearchTop1(selector, query)` | `QuiverSearchResult<T>?` | 最相似单个实体 |
| `Search(query, topK)` | `List<QuiverSearchResult<T>>` | 默认字段 Top-K |
| `SearchTop1(query)` | `QuiverSearchResult<T>?` | 默认字段 Top-1 |

搜索参数规则：`query` 不能为 `null`；查询向量长度必须等于字段的 `Dimensions` 或 `EffectiveDimensions`；`topK` 和 `overFetchMultiplier` 必须大于 0；阈值搜索拒绝 `float.NaN`。

#### 搜索方法（异步）

所有同步搜索方法均有对应的 `Async` 后缀版本，附加 `CancellationToken` 参数，通过 `Task.Run` 卸载到线程池。这些方法是 CPU-bound 便利封装，不是真正的 I/O 异步。

### 特性标记

| 特性 | 目标 | 必需 | 说明 |
|------|------|------|------|
| `[QuiverKey]` | 属性 | ? | 标记主键（有且仅有一个） |
| `[QuiverVector(dim, metric)]` | 属性 | ? | 标记向量字段（至少一个，类型须为 `float[]`）。`Nullable = true` 允许 `null` |
| `[QuiverIndex(type, ...)]` | 属性 | ? | 配置索引类型及参数（默认 Flat） |
| `[QuiverLargeField]` | 属性 | ? | 标记大 `byte[]` 字段，写入独立 Blob 段。当前仅支持 `InMemory` |

对于非 `InMemory` 的向量字段（例如 `VectorMemoryMode.MemoryMapped` 或字段级 `VectorMemoryMode.MemoryMapped`），属性及其所在的整条嵌套类型链都必须是 `partial`，属性类型必须是 `float[]` 或 `float[]?`。源生成器会针对无效声明报告 `QVR001`、`QVR002` 或 `QVR003`。

### 枚举

#### DistanceMetric

| 值 | 说明 |
|----|------|
| `Cosine` | 余弦相似度（预归一化优化） |
| `Euclidean` | 欧几里得距离（转换为相似度） |
| `DotProduct` | 内积 |
| `Manhattan` | 曼哈顿距离 / L1 范数（转换为相似度） |
| `Chebyshev` | 切比雪夫距离 / L∞ 范数（转换为相似度） |
| `Pearson` | 皮尔逊相关系数（去均值余弦） |
| `Hamming` | 汉明相似度（匹配比例） |
| `Jaccard` | 广义 Jaccard 相似度（Σmin/Σmax） |
| `Canberra` | 堪培拉距离（转换为相似度） |

#### VectorIndexType

| 值 | 说明 |
|----|------|
| `Flat` | 暴力搜索，100% 精确 |
| `HNSW` | 分层可导航小世界图 |
| `IVF` | 倒排文件索引 |
| `KDTree` | KD 树 |

#### ExportFormat

| 值 | 说明 |
|----|------|
| `Json` | JSON 格式（导出/导入） |
| `Xml` | XML 格式（导出/导入） |

### 搜索结果

```csharp
public record QuiverSearchResult<TEntity>(TEntity Entity, float Similarity);
```

| 属性 | 类型 | 说明 |
|------|------|------|
| `Entity` | `TEntity` | 匹配的实体实例 |
| `Similarity` | `float` | 相似度分数（值越大越相似） |
