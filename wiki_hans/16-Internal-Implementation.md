## 14. 内部实现细节

### 14.1 表达式树编译属性访问器

框架使用表达式树为每个主键和向量属性编译高性能访问器，替代运行时反射调用：

```csharp
// 编译前（反射）：~200ns / 次
var value = propertyInfo.GetValue(entity);

// 编译后（表达式树）：~2ns / 次，约 100 倍提升
private static Func<TEntity, TResult> CompileGetter<TResult>(PropertyInfo prop)
{
    var param = Expression.Parameter(typeof(TEntity), "e");
    Expression body = Expression.Property(param, prop);
    // 值类型自动插入装箱节点（如 int → object）
    if (prop.PropertyType != typeof(TResult))
        body = Expression.Convert(body, typeof(TResult));
    return Expression.Lambda<Func<TEntity, TResult>>(body, param).Compile();
}
```

### 14.2 ISimilarity\<T\> 静态抽象接口设计

使用 C# `static abstract` 接口成员配合 `readonly struct` 类型。JIT 为每个具体类型生成特化机器码，在调用站点直接内联，无虚分派、无委托间接调用：

```csharp
// 接口定义
public interface ISimilarity<T> where T : unmanaged, INumber<T>, IRootFunctions<T>
{
    static abstract T Compute(ReadOnlySpan<T> x, ReadOnlySpan<T> y);
}

// 内置实现（readonly struct，零大小，JIT 内联）
public readonly struct DotProductSimilarity : ISimilarity<float>
{
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => VectorMath.Dot(x, y);
}

public readonly struct ManhattanSimilarity : ISimilarity<float>
{
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        // 通过 Vector<float> SIMD 加速
        int i = 0;
        float sum = 0f;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = x.Length - x.Length % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                vsum += Vector.Abs(new Vector<float>(x[i..]) - new Vector<float>(y[i..]));
            sum = Vector.Sum(vsum);
        }
        for (; i < x.Length; i++)
            sum += MathF.Abs(x[i] - y[i]);
        return 1f / (1f + sum);
    }
}
```

**相比 v1 委托方案的优势**：

| 维度 | v1 `SimilarityFunc` 委托 | v2 `ISimilarity<T>` 静态抽象 |
|------|-------------------------|------------------------------|
| 分派 | 间接调用（~2ns 开销） | JIT 内联，零开销 |
| 泛型 | 仅 `float` | 泛型 `T : INumber<T>`（float、double、Half） |
| 可扩展性 | 仅框架内部 | 用户实现 `ISimilarity<float>` + `[QuiverVector(CustomSimilarity=...)]` |

### 14.3 HNSW 层级随机生成

层级服从指数衰减分布，保证高层稀疏、低层稠密：

```
level = floor(-ln(uniform(0, 1)) × ml)
其中 ml = 1 / ln(M)
```

大多数节点（~93.75% 当 M=16）只在第 0 层，少数节点存在于高层充当"高速公路"入口。

### 14.4 IVF K-Means++ 初始化

比随机初始化收敛更快、聚类质量更高：

1. 随机选择第一个质心
2. 对每个未选为质心的向量，计算其与最近质心的距离 D(x)
3. 以概率正比于 D(x)² 选择下一个质心
4. 重复直到选出 K 个质心

### 14.5 KDTree 剪枝优化

搜索时利用切分超平面距离进行剪枝：

- `diff = query[splitDim] - node.splitValue`
- 优先搜索查询点所在侧
- 对另一侧：仅当堆未满 **或** `|diff| < 当前搜索半径` 时才探索
- 低维下可跳过大量子树；高维下剪枝失效

### 14.6 ExportStorageProviderFactory

简单工厂模式，由 `QuiverDbContext.ExportAsync` / `ImportAsync` 调用：

```csharp
internal static IStorageProvider Create(ExportFormat format, JsonSerializerOptions? jsonOptions = null) => format switch
{
    ExportFormat.Json => new JsonExportProvider(jsonOptions),
    ExportFormat.Xml  => new XmlExportProvider(),
    _ => throw new ArgumentOutOfRangeException(nameof(format))
};
```

### 14.7 Tombstone 墓碑缓冲与段回放

v4 不再维护 WAL 式的变更日志；`QuiverSet<T>` 仅在内部缓冲待删除主键，由 `AppendAsync` / `FlushTombstonesAsync` 抽取后写为 `Tombstone` 段：

```csharp
// 待删除主键缓冲区
private readonly List<object> _pendingTombstones = [];

// RemoveCore 在写锁内追加主键
if (recordTombstone)
    _pendingTombstones.Add(key);
```

**`DrainTombstones()` 快照 + 清空语义**：

```csharp
internal IReadOnlyList<object> DrainTombstones()
{
    ThrowIfDisposed();
    _lock.EnterWriteLock();
    try
    {
        if (_pendingTombstones.Count == 0)
            return Array.Empty<object>();
        var snapshot = _pendingTombstones.ToArray();
        _pendingTombstones.Clear();
        return snapshot;
    }
    finally { _lock.ExitWriteLock(); }
}
```

**LoadAsync 段回放顺序**：

- Header → 按顺序读取每个段（`Mixed` / `EntityMeta` / `VectorBlob` / `Blob`）重建实体与索引。
- 最后应用所有 `Tombstone` 段：从实体 / 索引 / `Blob` 中删除对应主键。
- `Vectors.MemoryMode = MemoryMapped / Auto` 时，最后在 `VectorBlob` 范围上打开 `MmapVectorRegion` 供检索使用。

### 14.8 原子写入（SaveAsync）

`SaveAsync` 使用先写临时文件、再原子替换的策略，防止写入中途崩溃导致数据损坏：

```csharp
var tempPath = filePath + ".tmp";
await _storageProvider.SaveAsync(tempPath, setsData);
File.Move(tempPath, filePath, overwrite: true); // 原子替换
```

`AppendAsync` / `FlushTombstonesAsync` 则以追加模式打开文件，仅重写 footer，不走临时文件路径（依赖段级 CRC32 保障完整性）。

### 14.9 段级 CRC32 校验

v4 每个段在 footer 中附带 `Crc32 u32`，覆盖该段的完整负载区。`QuiverDbFile.InspectAsync(path, verifyCrc: true)` 可不加载数据的情况下逐段重算并对比 footer 中记录的 CRC，返回每段的 `CrcOk` 状态。加载时也会在读完每段后校验 CRC，不匹配则报错。

---

