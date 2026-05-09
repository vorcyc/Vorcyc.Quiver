## 14. Internal Implementation Details

### 14.1 Expression Tree Compiled Property Accessors

The framework uses expression trees to compile high-performance accessors for each primary key and vector property, replacing runtime reflection calls:

```csharp
// Before compilation (reflection): ~200ns / call
var value = propertyInfo.GetValue(entity);

// After compilation (expression tree): ~2ns / call, ~100x improvement
private static Func<TEntity, TResult> CompileGetter<TResult>(PropertyInfo prop)
{
    var param = Expression.Parameter(typeof(TEntity), "e");
    Expression body = Expression.Property(param, prop);
    // Value types automatically get boxing node inserted (e.g., int -> object)
    if (prop.PropertyType != typeof(TResult))
        body = Expression.Convert(body, typeof(TResult));
    return Expression.Lambda<Func<TEntity, TResult>>(body, param).Compile();
}
```

### 14.2 ISimilarity\<T\> Static Abstract Interface Design

Uses C# `static abstract` interface members on `readonly struct` types. The JIT generates specialized machine code for each concrete type, enabling direct inlining at call sites with zero virtual dispatch or delegate indirection:

```csharp
// Interface definition
public interface ISimilarity<T> where T : unmanaged, INumber<T>, IRootFunctions<T>
{
    static abstract T Compute(ReadOnlySpan<T> x, ReadOnlySpan<T> y);
}

// Built-in implementations (readonly struct, zero-size, JIT-inlined)
public readonly struct DotProductSimilarity : ISimilarity<float>
{
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => VectorMath.Dot(x, y);
}

public readonly struct ManhattanSimilarity : ISimilarity<float>
{
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        // SIMD-accelerated via Vector<float>
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

**Advantages over the v1 delegate approach**:

| Dimension | v1 `SimilarityFunc` delegate | v2 `ISimilarity<T>` static abstract |
|-----------|------------------------------|--------------------------------------|
| Dispatch | Indirect call (~2ns overhead) | JIT-inlined, zero overhead |
| Generics | `float` only | Generic over `T : INumber<T>` (float, double, Half) |
| Extensibility | Framework-internal only | Users implement `ISimilarity<float>` + `[QuiverVector(CustomSimilarity=...)]` |

### 14.3 HNSW Level Random Generation

Levels follow an exponential decay distribution, ensuring upper layers are sparse and lower layers are dense:

```
level = floor(-ln(uniform(0, 1)) × ml)
where ml = 1 / ln(M)
```

Most nodes (~93.75% when M=16) exist only on layer 0, while a few nodes exist on higher layers serving as "highway" entry points.

### 14.4 IVF K-Means++ Initialization

Converges faster and produces higher-quality clusters than random initialization:

1. Randomly select the first centroid
2. For each vector not yet selected as a centroid, compute its distance D(x) to the nearest centroid
3. Select the next centroid with probability proportional to D(x)²
4. Repeat until K centroids are selected

### 14.5 KDTree Pruning Optimization

Uses split hyperplane distance for pruning during search:

- `diff = query[splitDim] - node.splitValue`
- Prioritize searching the side containing the query point
- For the other side: explore only when the heap is not full **or** `|diff| < current search radius`
- Can skip large numbers of subtrees in low dimensions; pruning fails in high dimensions

### 14.6 ExportStorageProviderFactory

Simple factory pattern, invoked only during `ExportAsync` / `ImportAsync`. Primary storage always uses `BinaryStorageProvider` directly:

```csharp
// Primary storage — always binary, created directly in QuiverDbContext
var storageProvider = new BinaryStorageProvider();

// Export/Import factory — creates export-only providers on demand
internal static IStorageProvider Create(ExportFormat format, JsonSerializerOptions? jsonOptions = null) =>
    format switch
    {
        ExportFormat.Json => new JsonExportProvider(jsonOptions ?? DefaultJsonOptions),
        ExportFormat.Xml  => new XmlExportProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
```

### 14.7 Tombstone Tracking and Segment Replay

In v4, `QuiverSet<T>` no longer maintains a WAL-style change log. Instead, it tracks pending deletions in a tombstone buffer that is drained by `AppendAsync` / `FlushTombstonesAsync`:

- `Add` / `Upsert` mutate the in-memory dictionary and indexes directly; the entity becomes part of the next appended `EntityMeta` segment.
- `Remove` / `RemoveByKey` / `Clear` register tombstone entries (type + key) on the parent `QuiverDbContext` so they can be written as a `Tombstone` segment.
- During `LoadAsync`, segments are replayed in file order; tombstones are applied last so they consistently shadow earlier `Add` records, regardless of segment layout.
- `ReplayAdd` silently skips when the primary key already exists, matching the V3 semantics for forward-compatible merges.

### 14.8 Atomic Write (SaveAsync)

Full-snapshot writes still use the temp-file-then-rename pattern, which prevents mid-write corruption:

```csharp
var tempPath = filePath + ".tmp";
await _storageProvider.SaveAsync(tempPath, setsData);
File.Move(tempPath, filePath, overwrite: true); // Atomic replace
```

`AppendAsync` and `FlushTombstonesAsync` open the existing file in append mode and only rewrite the footer at the end of the operation, so a crash mid-append leaves the previous footer (and all previously committed segments) intact.

### 14.9 Per-segment CRC32 Verification

Each segment payload is checksummed with Quiver's internal IEEE CRC32 helper; the segment table in the footer also stores the per-segment CRC. `QuiverDbFile.InspectAsync(path, verifyCrc: true)` walks the segment table and recomputes every CRC, surfacing corrupt or truncated segments without modifying the file. The CRC result remains compatible with files written by the previous `System.IO.Hashing.Crc32` implementation.

---

