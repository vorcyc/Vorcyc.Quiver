# Vorcyc Quiver 4.0.1 中文 Wiki

![Vorcyc Quiver 4.0.1](../logo.jpg "Vorcyc Quiver 4.0.1")

> **产品定位**：纯 .NET 实现的嵌入式向量数据库 —— 零依赖，进程内运行，无需独立数据库服务器  
> **框架版本**：.NET 10  
> **命名空间**：`Vorcyc.Quiver`  
> **设计理念**：类似 EF Core 的 `DbContext` 模式，通过声明式属性标记实现面向向量数据库的自动发现、索引构建和持久化

本 Wiki 由 [`README_zh_cn.md`](../README_zh_cn.md) 按章节拆分而成，便于按主题快速查阅。

---

## 目录

### 发布 & 概览

- [01. 发布说明（v4.0.1 / Wave 2 / Wave 3 及历史版本）](01-Release-Notes.md)
- [02. 产品简介](02-Product-Overview.md)

### 入门

- [03. 架构概览](03-Architecture.md)
- [04. 快速开始](04-Quick-Start.md)
- [05. 核心概念（实体 / 上下文 / QuiverSet）](05-Core-Concepts.md)

### 检索基础

- [06. 距离度量（9 种内置 + 自定义 ISimilarity）](06-Distance-Metrics.md)
- [07. 索引类型（Flat / HNSW / IVF / KDTree）](07-Index-Types.md)
- [08. CRUD 操作](08-CRUD.md)
- [09. 向量搜索（Top-K / 阈值 / 过滤 / 异步）](09-Vector-Search.md)

### 存储与演进

- [10. 持久化存储（QDB 二进制格式）](10-Persistence.md)
- [11. 迁移系统（版本迁移 vs 架构迁移）](11-Migration-System.md)
- [11a. Schema 迁移（架构迁移细节）](11-Schema-Migration.md)
- [12. 多向量字段支持](12-Multi-Vector-Fields.md)

### 运行时

- [13. 线程安全与并发](13-Thread-Safety.md)
- [14. 生命周期管理](14-Lifecycle.md)
- [15. 配置选项](15-Configuration.md)

### 深入

- [16. 内部实现细节](16-Internal-Implementation.md)
- [17. 完整示例](17-Examples.md)
- [18. API 参考速查表](18-API-Reference.md)- 
- [19. 使用建议（懒加载 / mmap / Blob / HNSW Snapshot）](19-Usage-Recommendations.md)

---

## 关键词

`嵌入式向量数据库` • `.NET` • `ANN` • `近似最近邻搜索` • `HNSW` • `IVF` • `KDTree` • `Code-First` • `Embedding` • `语义搜索` • `人脸识别` • `以图搜图` • `RAG` • `SIMD` • `Schema Migration` • `ISimilarity` • `Mmap` • `SQ8 量化` • `Matryoshka 截断`

> **释名**：Quiver —— 箭袋（Arrow 的容器），而向量的数学本质就是箭头。