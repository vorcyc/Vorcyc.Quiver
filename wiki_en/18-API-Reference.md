## 16. API Reference Cheat Sheet

### QuiverDbContext

| Method / Property | Return Type | Description |
|-------------------|-------------|-------------|
| `Set<TEntity>()` | `QuiverSet<TEntity>` | Get vector collection by type (throws if not registered) |
| `SaveAsync(path?)` | `Task` | Full snapshot — atomic temp-file + `File.Move` replace |
| `AppendAsync(path?)` | `Task` | Append a new segment (EntityMeta + VectorBlob [+ Blob] + optional Tombstone) and rewrite footer. O(Δ) |
| `FlushTombstonesAsync(path?)` | `Task` | Append a Tombstone-only segment, rewrite footer |
| `LoadAsync(path?)` | `Task` | Read header + segment table, replay segments in order, apply tombstones last (silently returns if file missing) |
| `ExportAsync(path, format)` | `Task` | Export to JSON or XML side-channel |
| `ImportAsync(path, format)` | `Task` | Import from JSON or XML side-channel |
| `Dispose()` | `void` | Synchronous disposal (no save) |
| `DisposeAsync()` | `ValueTask` | Async disposal — releases resources; calls `SaveAsync()` only if `SaveOnDispose = true` |

### File Format Migration (Vorcyc.Quiver.Migration)

| Type / Method | Description |
|---------------|-------------|
| `QuiverMigrator.MigrateAsync(sourceFile, destinationFile, typeMap, migrationRules?, options?)` | Offline-upgrade old v1/v2/v3 `.vdb` files to the current v4 `QDB\x04` format. Schema migration rules can be applied at the same time. |
| `MigrateOptions.Overwrite` | Allow replacing an existing destination file. |
| `MigrateOptions.DeleteSourceOnSuccess` | Delete the source file after successful migration. |
| `MigrateOptions.AllowNoop` | Allow returning when the source is already v4 and source equals destination. |

Migration requires a `typeMap` (type full name stored in the file → current CLR type). If old files contain renamed properties, pass `SchemaMigrationRule` through `migrationRules`; otherwise old fields may be skipped during decoding.

#### Static file utilities (Vorcyc.Quiver.Files.QuiverDbFile)

| Method | Description |
|--------|-------------|
| `InspectAsync(path, verifyCrc)` | Returns header version + segment table + per-segment CRC status without modifying the file |
| `MergeAsync(sources, destination, options?, typeMap?)` | Multi-file merge with `Append` / `FirstWriterWins` / `LastWriterWins` conflict policies |

### QuiverSet\<TEntity\>

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Count` | `int` | Entity count (read lock protected, thread-safe) |
| `VectorFields` | `IReadOnlyDictionary<string, int>` | Read-only mapping of vector field name → dimensions (lazily cached) |

#### Enumeration

| Method | Return Type | Lock | Description |
|--------|-------------|------|-------------|
| `GetEnumerator()` | `IEnumerator<TEntity>` | Read (snapshot) | Supports `foreach` and LINQ. Takes snapshot within read lock, then enumerates |

#### CRUD Methods

| Method | Return Type | Lock | Description |
|--------|-------------|------|-------------|
| `Add(entity)` | `void` | Write | Add single entity (throws on duplicate key) |
| `AddRange(entities)` | `void` | Write | Batch add (atomic, two-phase commit) |
| `AddRangeAsync(entities, ct)` | `Task` | Write | Async batch add (`Task.Run`) |
| `Upsert(entity)` | `void` | Write | Insert or update (completed within single write lock) |
| `Remove(entity)` | `bool` | Write | Remove by entity primary key |
| `RemoveByKey(key)` | `bool` | Write | Remove by key value |
| `Find(key)` | `TEntity?` | Read | Find by primary key, O(1) |
| `Exists(key)` | `bool` | Read | Check existence by primary key, O(1) (only checks `_keyToId`) |
| `Exists(predicate)` | `bool` | Read | Check existence by condition, O(n) short-circuit (returns on match, no snapshot allocation) |
| `Clear()` | `void` | Write | Clear all data + indices |

#### Search Methods (Synchronous)

| Method | Return Type | Description |
|--------|-------------|-------------|
| `Search(selector, query, topK)` | `List<QuiverSearchResult<T>>` | Top-K search |
| `Search(selector, query, topK, Expression filter)` | `List<QuiverSearchResult<T>>` | With expression filter |
| `Search(selector, query, topK, Func filter, overFetchMultiplier)` | `List<QuiverSearchResult<T>>` | With delegate filter + over-fetch |
| `SearchByThreshold(selector, query, threshold)` | `List<QuiverSearchResult<T>>` | Threshold search |
| `SearchTop1(selector, query)` | `QuiverSearchResult<T>?` | Most similar single entity |
| `Search(query, topK)` | `List<QuiverSearchResult<T>>` | Default field Top-K |
| `SearchTop1(query)` | `QuiverSearchResult<T>?` | Default field Top-1 |

Search argument rules: `query` must not be `null`; query length must match the field's declared `Dimensions` or `EffectiveDimensions`; `topK` and `overFetchMultiplier` must be greater than zero; threshold search rejects `float.NaN`.

#### Search Methods (Asynchronous)

All synchronous search methods have corresponding `Async` suffix versions with an additional `CancellationToken` parameter, offloaded to the thread pool via `Task.Run`. These are CPU-bound convenience wrappers rather than true I/O async operations.

### Attribute Annotations

| Attribute | Target | Required | Description |
|-----------|--------|----------|-------------|
| `[QuiverKey]` | Property | ✅ | Marks primary key (exactly one required) |
| `[QuiverVector(dim, metric)]` | Property | ✅ | Marks vector field (at least one, type must be `float[]`). `Nullable = true` allows `null`; `MemoryMode` can select per-field payload memory behavior |
| `[QuiverLargeField]` | Property | ❌ | Marks large `byte[]` payload fields stored in `Blob` segments; supports `InMemory`, `LazyLoad`, and `PagedCache` behavior |
| `[QuiverIndex(type, ...)]` | Property | ❌ | Configures index type and parameters (defaults to Flat) |

For non-InMemory vector or large-field payload properties, the property and every containing type in its nesting chain must be `partial`, and the property type must be `float[]` / `float[]?` or `byte[]` / `byte[]?` respectively. The source generator reports diagnostics for invalid declarations.

### Enums

#### DistanceMetric

| Value | Description |
|-------|-------------|
| `Cosine` | Cosine similarity (pre-normalization optimized) |
| `Euclidean` | Euclidean distance (converted to similarity) |
| `DotProduct` | Dot product |
| `Manhattan` | Manhattan distance / L1 norm (converted to similarity) |
| `Chebyshev` | Chebyshev distance / L∞ norm (converted to similarity) |
| `Pearson` | Pearson correlation coefficient (de-meaned cosine) |
| `Hamming` | Hamming similarity (match ratio) |
| `Jaccard` | Generalized Jaccard similarity (Σmin/Σmax) |
| `Canberra` | Canberra distance (converted to similarity) |

#### VectorIndexType

| Value | Description |
|-------|-------------|
| `Flat` | Brute-force search, 100% exact |
| `HNSW` | Hierarchical Navigable Small World graph |
| `IVF` | Inverted File Index |
| `KDTree` | KD Tree |

#### ExportFormat

| Value | Description |
|-------|-------------|
| `Json` | JSON export/import format |
| `Xml` | XML export/import format |

### Search Result

```csharp
public record QuiverSearchResult<TEntity>(TEntity Entity, float Similarity);
```

| Property | Type | Description |
|----------|------|-------------|
| `Entity` | `TEntity` | The matched entity instance |
| `Similarity` | `float` | Similarity score (higher is more similar) |
