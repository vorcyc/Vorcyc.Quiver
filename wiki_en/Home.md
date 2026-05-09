# Vorcyc Quiver 4.0.1 — English Wiki

![Vorcyc Quiver 4.0.1](../logo.jpg "Vorcyc Quiver 4.0.1")

> **Product Positioning**: A pure .NET embedded vector database — zero native dependencies, runs in-process, no standalone database server deployment required.
> **Framework Version**: .NET 10
> **Namespace**: `Vorcyc.Quiver`
> **Design Philosophy**: Similar to EF Core's `DbContext` pattern — automatic discovery, index construction, and persistence via declarative attribute annotations.

This wiki is a chapter-by-chapter split of [`README.md`](../README.md), intended for easier topic-oriented navigation.

---

## Table of Contents

### Release & Overview

- [01. Release Notes (v4.0.1 / Wave 2 / Wave 3 and historical versions)](01-Release-Notes.md)
- [02. Product Overview](02-Product-Overview.md)

### Getting Started

- [03. Architecture Overview](03-Architecture.md)
- [04. Quick Start](04-Quick-Start.md)
- [05. Core Concepts (Entities / Context / QuiverSet)](05-Core-Concepts.md)

### Retrieval Basics

- [06. Distance Metrics (9 built-in + custom `ISimilarity`)](06-Distance-Metrics.md)
- [07. Index Types (Flat / HNSW / IVF / KDTree)](07-Index-Types.md)
- [08. CRUD Operations](08-CRUD.md)
- [09. Vector Search (Top-K / Threshold / Filtered / Async)](09-Vector-Search.md)

### Storage & Evolution

- [10. Persistent Storage (`QDB\x04` segment format)](10-Persistence.md)
- [11. Migration System (format vs schema migration)](11-Migration-System.md)
- [11a. Schema Migration (schema evolution details)](11-Schema-Migration.md)
- [12. Multi-Vector Field Support](12-Multi-Vector-Fields.md)

### Runtime

- [13. Thread Safety and Concurrency](13-Thread-Safety.md)
- [14. Lifecycle Management](14-Lifecycle.md)
- [15. Configuration Options](15-Configuration.md)

### Deep Dive

- [16. Internal Implementation Details](16-Internal-Implementation.md)
- [17. Complete Examples](17-Examples.md)
- [18. API Reference Cheat Sheet](18-API-Reference.md)- 
- [19. Usage Recommendations (lazy loading / mmap / Blob / HNSW Snapshot)](19-Usage-Recommendations.md)

---

## Keywords

`Embedded Vector Database` · `Pure .NET` · `ANN` · `HNSW` · `IVF` · `KDTree` · `Code-First` · `Embedding` · `Semantic Search` · `Face Recognition` · `RAG` · `SIMD` · `Schema Migration` · `ISimilarity` · `Mmap` · `SQ8 Quantization` · `Matryoshka Truncation`

> **Name Origin**: Quiver — a container for arrows; a vector is mathematically an arrow.
