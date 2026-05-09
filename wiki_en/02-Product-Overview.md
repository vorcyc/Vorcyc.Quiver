### Product Introduction

**Quiver** is a pure .NET embedded vector database with zero native dependencies, running as an in-process library without requiring standalone database server deployment. It draws on EF Core's `DbContext` design pattern, allowing developers to define entities, payload fields, and indexing strategies through declarative attributes such as `[QuiverKey]`, `[QuiverVector]`, `[QuiverLargeField]`, and `[QuiverIndex]`, with the framework automatically completing model discovery, index construction, and persistence management at runtime.

**Core Capabilities at a Glance**:

- **Code-First Declarative Modeling** — Like EF Core, annotate entity classes with attributes, and the framework automatically discovers and registers `QuiverSet<T>` collections via reflection — zero configuration required.
- **Multiple ANN Index Algorithms** — Built-in Flat (brute-force search), HNSW (Hierarchical Navigable Small World graph), IVF (Inverted File Index), and KDTree indexes, covering the full range from small-scale exact search to million-scale approximate search.
- **Binary-First Persistence (v4 segmented)** — Primary storage always uses the high-performance v4 binary format (`QDB\x04`). `SaveAsync` writes a full atomic snapshot, while `AppendAsync` / `FlushTombstonesAsync` write new segments and only rewrite the footer, giving O(Δ) disk cost without WAL-style memory doubling. JSON and XML remain as `ExportAsync` / `ImportAsync` side channels.
- **Mmap Vector Storage** — `VectorMemoryMode.MemoryMapped` / `Auto` backs vector arenas with a read-only `MemoryMappedFile` view over the `VectorBlob` segment, dropping resident memory for large vector sets while keeping search SIMD-friendly.
- **Non-InMemory Vector Fields & Large Fields** — `[QuiverVector(MemoryMode = ...)]` (partial property + source generator) loads vector payloads on demand; `[QuiverLargeField] byte[]` keeps large binary payloads in a dedicated `Blob` segment outside `EntityMeta`.
- **Background Auto-Merge & File Utilities** — `EnableBackgroundMerge` triggers `MaybeAutoMergeAsync` after appends; `QuiverDbFile.InspectAsync` / `MergeAsync` enable per-segment CRC verification and multi-file merge with `FirstWriterWins` / `LastWriterWins` policies.
- **Out-of-the-box Concurrency Safety** — `QuiverSet<T>` internally implements reader-writer separation locks via `ReaderWriterLockSlim`, making concurrent multi-threaded searching and writing inherently safe without external locking.
- **9 Distance Metrics + Custom Similarity** — Built-in Cosine, Euclidean, DotProduct, Manhattan, Chebyshev, Pearson, Hamming, Jaccard, Canberra. Also supports user-defined `ISimilarity<float>` implementations via `CustomSimilarity` attribute.
- **SIMD Hardware Acceleration** — All similarity implementations use internal `VectorMath` helpers backed by `Vector<float>` SIMD instructions, auto-adapting to SSE4 / AVX2 / AVX-512 register widths without `System.Numerics.Tensors`.
- **Schema Migration**

**Typical Use Cases**: Semantic search, RAG (Retrieval-Augmented Generation), face recognition, image-to-image search, recommendation systems, multimodal retrieval, etc.

> ⚠️ **Native AOT Compatibility**: Quiver is **not compatible with Native AOT publishing**. The framework relies on runtime reflection to discover `QuiverSet<T>` properties and scan `[QuiverKey]` / `[QuiverVector]` / `[QuiverIndex]` / `[QuiverLargeField]` attributes, and compiles expression-tree accessors (`Expression.Lambda(...).Compile()`) at startup — both of which are unsupported under Native AOT. Quiver targets standard JIT / .NET 10 runtimes only.

---
