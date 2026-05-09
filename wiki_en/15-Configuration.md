## 13. Configuration Options

`QuiverDbOptions` provides the following configurations:

```csharp
var options = new QuiverDbOptions
{
    // Database file path. null for in-memory mode (no persistence)
    // Directory is auto-created by storage provider if it doesn't exist
    DatabasePath = @"C:\Data\MyQuiverDb.vdb",

    // Default distance metric (entity-level [QuiverVector] attribute can override)
    DefaultMetric = DistanceMetric.Cosine,

    // ── Vector payload memory ──
    Vectors =
    {
        MemoryMode = GlobalVectorMemoryMode.Auto,
        MemoryMapThresholdBytes = 256L * 1024 * 1024,
    },

    // ── Large field payload memory ──
    LargeFields =
    {
        MemoryMode = GlobalLargeFieldMemoryMode.PagedCache,
        MaxCachedPayloads = 128,
    },

    // ── Background auto-merge (used with AppendAsync / FlushTombstonesAsync) ──
    EnableBackgroundMerge = true,
    AutoMergeMaxSegments = 32,
    AutoMergeTombstoneRatio = 0.25
};
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DatabasePath` | `string?` | `null` | Storage path; `null` for in-memory mode (`SaveAsync` then requires an explicit `path`) |
| `DefaultMetric` | `DistanceMetric` | `Cosine` | Default distance metric |
| `Vectors.MemoryMode` | `GlobalVectorMemoryMode` | `InMemory` | `InMemory` / `LazyLoad` / `MemoryMapped` / `Auto` / `PerField` vector payload memory behavior |
| `Vectors.MemoryMapThresholdBytes` | `long` | `256 MiB` | Open-time threshold for `GlobalVectorMemoryMode.Auto`: existing database files at or above this size open vectors as `MemoryMapped` |
| `Vectors.MaxInMemoryBytes` | `long` | `1 GiB` | Runtime heap-vector budget used with `Vectors.AutoPromoteToMemoryMapped` |
| `Vectors.AutoPromoteToMemoryMapped` | `bool` | `false` | Enables runtime promotion from `InMemory` to `MemoryMapped` when `Vectors.MaxInMemoryBytes` is exceeded |
| `LargeFields.MemoryMode` | `GlobalLargeFieldMemoryMode` | `InMemory` | `InMemory` / `LazyLoad` / `PagedCache` / `PerField` large-field payload behavior |
| `LargeFields.MaxCachedPayloads` | `int` | `128` | Max cached large-field payloads when using `PagedCache` |
| `EnableBackgroundMerge` | `bool` | `false` | Run `MaybeAutoMergeAsync` after `AppendAsync` / `FlushTombstonesAsync` |
| `AutoMergeMaxSegments` | `int` | `32` | Trigger merge when live segment count exceeds this number |
| `AutoMergeTombstoneRatio` | `double` | `0.25` | Trigger merge when tombstone-to-live ratio exceeds this value |
| `SaveOnDispose` | `bool` | `false` | When `true`, `DisposeAsync()` calls `SaveAsync()` before releasing resources |

### Per-field memory mode

When `Vectors.MemoryMode = GlobalVectorMemoryMode.PerField`, each `[QuiverVector]` field uses its own `MemoryMode`. If the field does not explicitly set it, this is not an error; the attribute default is `VectorMemoryMode.InMemory`, so that field stays in memory.

When `LargeFields.MemoryMode = GlobalLargeFieldMemoryMode.PerField`, each `[QuiverLargeField]` field uses its own `MemoryMode`. If the field does not explicitly set it, this is not an error; the attribute default is `LargeFieldMemoryMode.InMemory`, so that field is loaded in memory.

`Auto` and `PerField` exist only on the global enums (`GlobalVectorMemoryMode` / `GlobalLargeFieldMemoryMode`). Field-level enums only support concrete strategies.

### Vector thresholds

`Vectors.MemoryMapThresholdBytes` is checked when the database is opened and only affects `GlobalVectorMemoryMode.Auto`; it uses the existing database file size. `Vectors.MaxInMemoryBytes` is checked while the process is running; it uses the current managed-heap vector bytes and only triggers promotion when `Vectors.AutoPromoteToMemoryMapped` is enabled.

> ⚠️ **Native AOT Compatibility**: `QuiverDbOptions` itself is AOT-safe (plain POCO). However, the `QuiverDbContext` that consumes it is **not Native AOT-compatible** — it uses runtime reflection for `QuiverSet<T>` discovery and compiles expression-tree property accessors at startup. Do not publish Quiver applications with `PublishAot=true`.

---

