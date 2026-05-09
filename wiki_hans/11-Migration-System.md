**已修复！** 以下是正确的文档内容（已还原为正常 UTF-8 中文）：

```markdown
# 11. 迁移系统：版本迁移 vs 架构迁移

Quiver 将“迁移”拆分为两个互补但独立的层面，并额外提供稳定实体身份机制来解决命名空间/类名重构问题。

```text
版本迁移：旧文件格式 → 当前文件格式
架构迁移：旧字段结构 → 当前字段结构
实体身份稳定：旧/稳定 TypeName → 当前 CLR 实体类型
```

## 11.1 一览对比

| 维度       | 目标                     | 入口 API                                      | 典型场景                          | 不负责                     |
|------------|--------------------------|-----------------------------------------------|-----------------------------------|----------------------------|
| **版本迁移** (Format Migration) | 升级磁盘文件容器格式     | `Vorcyc.Quiver.Migration.QuiverMigrator.MigrateAsync(...)` | v1/v2/v3 `.vdb` → v4 `.vdb`      | 自动推断字段重命名、自动映射旧类型 |
| **架构迁移** (Schema Migration) | 演进实体字段结构         | `ConfigureMigration<T>()` / `MigrationBuilder<T>` / `SchemaMigrationRule` / `QuiverSchemaMismatchException` | 新增/删除字段、属性重命名、值转换、schema 指纹保护 | 文件格式升级、无法匹配的 `TypeName` |
| **实体身份稳定**                | 让文件中的 `TypeName` 稳定 | `[QuiverEntity("...")]` + `typeMap` + `UnknownTypeHandling` | 命名空间/类名重构、跨项目复用数据 | 字段结构演进、二进制格式升级 |

## 11.2 版本迁移 (Format Migration)

版本迁移负责将旧版本 `.vdb` 容器升级到当前 v4 二进制格式。入口位于独立命名空间：

```csharp
using Vorcyc.Quiver.Migration;

await QuiverMigrator.MigrateAsync(
    sourceFile: "old-v3.vdb",
    destinationFile: "data-v4.vdb",
    typeMap: new Dictionary<string, Type>
    {
        ["Old.Namespace.Document"] = typeof(Document)
    },
    options: new MigrateOptions { Overwrite = true });
```

v4 runtime 的 `QuiverDbContext.LoadAsync()` 不再直接打开 v1/v2/v3 文件，遇到旧格式会抛出 `QuiverFormatVersionException`，提示先运行离线版本迁移工具。

## 11.3 架构迁移 (Schema Migration)

架构迁移负责实体字段结构的变更。它运行在反序列化过程中或反序列化之后，解决属性重命名和值转换问题：

```csharp
public class MyDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;

    public MyDb() : base(new QuiverDbOptions { DatabasePath = "data.vdb" })
    {
        ConfigureMigration<Document>(m => m
            .RenameProperty("OldTitle", "Title")
            .TransformValue("Score", v => v is int i ? (double)i : v));
    }
}
```

新增字段和删除字段无需配置：
- 新增字段取 CLR 默认值
- 已删除字段在加载时跳过

如果 v4 文件中的 schema 指纹与当前 CLR 实体声明不一致，且没有为该类型注册迁移规则，运行时会抛出 `Vorcyc.Quiver.Migration.QuiverSchemaMismatchException`。可通过注册 `ConfigureMigration<T>()` 处理差异；如果明确接受默认兼容行为，也可以设置 `QuiverDbOptions.IgnoreSchemaFingerprintMismatch = true`。

详细规则见：[Schema 迁移（架构迁移细节）](11-Schema-Migration.md)

## 11.4 组合使用

如果旧文件既是旧文件格式，又存在字段重命名或值转换，需要在离线版本迁移时同时传入：

- `typeMap`：旧文件 `TypeName` → 当前 CLR 类型
- `migrationRules`：旧字段结构 → 当前字段结构

```csharp
var rule = MigrationBuilder<Document>.Build(m => m
    .RenameProperty("OldTitle", "Title"));

await QuiverMigrator.MigrateAsync(
    sourceFile: "old-v3.vdb",
    destinationFile: "data-v4.vdb",
    typeMap: new Dictionary<string, Type>
    {
        ["Old.Namespace.Document"] = typeof(Document)
    },
    migrationRules: new Dictionary<string, SchemaMigrationRule>
    {
        ["Old.Namespace.Document"] = rule
    });
```

> **关键点**：`migrationRules` 的 key 应与 `typeMap` 的旧类型 key 对齐，因为规则是在旧文件解码阶段应用的。

## 11.5 与 `[QuiverEntity]` 的关系

`[QuiverEntity("...")]` 解决的是**实体身份稳定性**问题，不是版本迁移，也不是架构迁移。

```csharp
[QuiverEntity("Document/v1")]
public class Document
{
    [QuiverKey] public string Id { get; set; } = "";

    [QuiverVector(384)]
    public float[] Embedding { get; set; } = [];
}
```

默认情况下，Quiver 使用 `Type.FullName` 作为文件中的 `TypeName`。这意味着命名空间或类名变化会导致磁盘身份改变。使用 `[QuiverEntity("Document/v1")]` 后，新文件会写入稳定名称，运行时也会接受旧 `FullName` 作为兼容别名。

开发或迁移验证阶段可开启严格检查：

```csharp
new QuiverDbOptions
{
    DatabasePath = "data.vdb",
    UnknownTypeHandling = UnknownTypeHandling.Throw
};
```

## 11.6 推荐升级流程

1. **备份旧文件**。
2. 如果旧应用仍在使用 WAL，先在 3.2.x 应用中执行 `LoadAsync()` + `SaveAsync()`，把 `.wal` 合并进 `.vdb`。
3. 确认旧文件中所有的 `TypeName`，为每个旧类型配置 `typeMap`。
4. 如果字段结构有变化，构建对应的 `SchemaMigrationRule`。
5. 调用 `QuiverMigrator.MigrateAsync(...)` 生成 v4 文件。
6. 使用 `QuiverDbFile.InspectAsync(path, verifyCrc: true)` 校验 v4 文件。
7. 用当前 v4 runtime 正常 `LoadAsync()`。

---

文档已完全修复为可读的中文。如果你需要我帮你进一步调整格式、添加内容或生成其他文档，请随时告诉我！