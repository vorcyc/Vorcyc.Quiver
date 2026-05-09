### 产品简介

**Quiver** 是一款纯 .NET 实现的嵌入式向量数据库，无任何原生依赖，以进程内库的形式运行，无需独立部署数据库服务器。它借鉴 EF Core 的 `DbContext` 设计模式，让开发者通过 `[QuiverKey]`、`[QuiverVector]`、`[QuiverIndex]` 等声明式特性定义实体与索引策略，框架在运行时自动完成模型发现、索引构建和持久化管理。

**核心能力一览**：

- **Code-First 声明式建模** —— 像 EF Core 一样用特性标记实体类，框架自动反射发现并注册 `QuiverSet<T>` 集合，零配置即可使用。
- **多种 ANN 索引算法** —— 内置 Flat（暴力搜索）、HNSW（分层可导航小世界图）、IVF（倒排文件索引）、KDTree（KD 树）四种索引，覆盖从小数据量精确搜索到百万级近似搜索的全场景需求。
- **二进制主存储 + 增量追加** —— v4 采用段式二进制格式（`QDB\x04`）作为唯一主存储路径。`SaveAsync()` 原子全量快照，`AppendAsync()` / `FlushTombstonesAsync()` 只追加新段 + 重写 footer，磁盘开销 O(Δ)，不会出现 WAL 那种内存翻倍。`QuiverDbFile.MergeAsync` / `InspectAsync` 提供文件级合并与诊断。JSON / XML 仅作为 `ExportAsync` / `ImportAsync` 旁路。
- **开箱即用的并发安全** —— `QuiverSet<T>` 内部通过 `ReaderWriterLockSlim` 实现读写分离锁，多线程并发搜索与写入天然安全，无需外部加锁。
- **9 种距离度量 + 自定义相似度** —— 内置 Cosine、Euclidean、DotProduct、Manhattan、Chebyshev、Pearson、Hamming、Jaccard、Canberra。还支持用户通过 `CustomSimilarity` 属性接入自定义 `ISimilarity<float>` 实现。
- **SIMD 硬件加速** —— 全部相似度实现均基于内部 `VectorMath` helper 与 `Vector<float>` SIMD 指令，自动适配 SSE4 / AVX2 / AVX-512 寄存器宽度，无需 `System.Numerics.Tensors`。
- **Schema Migration** —— 支持加载时通过 `ConfigureMigration<T>()` 声明属性重命名和值转换规则。新增/删除字段无需配置——新字段取默认值，删除字段静默跳过。

**典型应用场景**：语义搜索、RAG（检索增强生成）、人脸识别、以图搜图、推荐系统、多模态检索等。

> ⚠️ **Native AOT 兼容性**：Quiver **不兼容 Native AOT 发布**。框架在启动时通过运行时反射扫描 `QuiverSet<T>` 属性及 `[QuiverKey]` / `[QuiverVector]` / `[QuiverIndex]` / `[QuiverLargeField]` 特性，并编译表达式树访问器（`Expression.Lambda(...).Compile()`）——这两项机制均不被 Native AOT 支持。Quiver 仅面向标准 JIT / .NET 10 运行时。

---
