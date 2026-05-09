## 11a. Schema 迁移（架构迁徙细节）

本页只说明**架构迁徙（Schema Migration）**：当实体结构演进时（增/删/重命名字段、更改值类型），Quiver 在 `LoadAsync` 或离线迁徙解码阶段按规则处理差异——无需手动编辑数据文件。

如果你需要区分“版本迁徙”和“架构迁徙”，先阅读：[迁徙体系：版本迁徙 vs 架构迁徙](11-Migration-System.md)。

### 9.1 自动处理（增 / 删字段）

**新增或删除字段无需任何配置**——Quiver 自动处理：

| 场景 | 行为 | 是否需要配置 |
|------|------|-------------|
| 实体**新增字段** | 新字段取 CLR 默认值（`null`、`0`、`""` 等） | ❌ 无需 |
| 实体**删除旧字段** | 文件中的旧字段在加载时静默跳过 | ❌ 无需 |

```csharp
// V1 实体
public class Document
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}

// V2 实体 —— 新增 Category，未删除任何字段
public class Document
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;  // 新字段 → 默认 ""

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}
// 使用 V2 实体加载 V1 数据：Category = ""（默认值），其余字段正常加载。
```

### 9.2 属性重命名

当属性被重命名时（如 `OldTitle` → `Title`），在上下文构造函数中通过 `ConfigureMigration<T>()` 声明映射：

```csharp
public class MyDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;

    public MyDb() : base(new QuiverDbOptions { DatabasePath = "my.db" })
    {
        ConfigureMigration<Document>(m => m
            .RenameProperty("OldTitle", "Title"));
    }
}
```

`LoadAsync` 时，存储提供者会将文件中的旧属性名映射到当前 CLR 类型的新属性名。Binary 主存储与 JSON / XML 导入路径同样生效。

### 9.2.1 Schema 指纹保护

`QuiverSchemaMismatchException` 位于 `Vorcyc.Quiver.Migration` 命名空间。它表示 v4 文件中保存的实体 schema 指纹与当前 CLR 实体声明不一致，并且没有为该实体注册 schema 迁移规则。

```csharp
using Vorcyc.Quiver.Migration;

try
{
    await db.LoadAsync();
}
catch (QuiverSchemaMismatchException ex)
{
    Console.WriteLine($"Schema mismatch: {ex.TypeName}");
}
```

推荐处理方式是为受影响实体注册 `ConfigureMigration<T>()` / `SchemaMigrationRule`。如果你明确接受新增字段取默认值、删除字段被忽略等默认行为，可以设置：

```csharp
new QuiverDbOptions
{
    DatabasePath = "data.vdb",
    IgnoreSchemaFingerprintMismatch = true
};
```

### 9.3 值转换

当属性的类型或格式发生变化时（如 `int` → `double`、字符串格式迁移），声明值转换规则：

```csharp
public class MyDb : QuiverDbContext
{
    public QuiverSet<Document> Documents { get; set; } = null!;

    public MyDb() : base(new QuiverDbOptions { DatabasePath = "my.db" })
    {
        ConfigureMigration<Document>(m => m
            .TransformValue("Score", v => v is int i ? (double)i : v));
    }
}
```

值转换在**反序列化之后**应用——转换函数接收加载的值并返回转换后的值。

### 9.4 组合使用

重命名和转换可链式组合：

```csharp
ConfigureMigration<Document>(m => m
    .RenameProperty("OldTitle", "Title")
    .RenameProperty("OldScore", "Score")
    .TransformValue("Score", v => v is int i ? (double)i : v));
```

**处理顺序**：
1. **属性重命名** —— 在反序列化阶段应用（存储提供者将旧名映射为新名）
2. **值转换** —— 在反序列化完成后应用（上下文遍历实体并执行转换）

> ⚠️ 重命名映射使用 **CLR 属性名**（非序列化后的名称）。例如即使 JSON 使用驼峰 `"oldTitle"`，`RenameProperty` 调用仍使用 `"OldTitle"`（PascalCase CLR 名称）。

### 9.5 离线版本迁徙中的 Schema 规则

`ConfigureMigration<T>()` 的规则由 `QuiverDbContext.LoadAsync()` 在读取当前运行时支持的格式时应用。如果使用 `QuiverMigrator.MigrateAsync` 把旧 v1/v2/v3 文件升级到 v4，需要把同样的规则通过迁移器的 `migrationRules` 参数传入。否则旧文件解码阶段遇到已重命名字段时会跳过旧值，升级后的 v4 文件也就无法再恢复这些值。

```csharp
using Vorcyc.Quiver.Migration;

var rule = MigrationBuilder<Document>.Build(m => m
    .RenameProperty("OldTitle", "Title"));

await QuiverMigrator.MigrateAsync(
    sourceFile: "old.vdb",
    destinationFile: "data.vdb",
    typeMap: new Dictionary<string, Type>
    {
        [typeof(Document).FullName!] = typeof(Document)
    },
    migrationRules: new Dictionary<string, SchemaMigrationRule>
    {
        [typeof(Document).FullName!] = rule
    });
```

### 9.6 与版本迁徙组合

Schema 迁移解决的是“实体属性如何演进”；版本迁徙解决的是“旧版 `.vdb` 容器如何升级到当前 v4 段式格式”。两者是正交能力，可以单独使用，也可以在同一次离线升级中组合使用。

`QuiverMigrator.MigrateAsync` 位于 `Vorcyc.Quiver.Migration` 命名空间，用于把旧版 `QDB\x01` / `QDB\x02` / `QDB\x03` 文件离线升级为当前 `QDB\x04` 文件。运行时 `QuiverDbContext.LoadAsync()` 遇到旧格式时会抛 `QuiverFormatVersionException`，因此生产升级流程建议显式调用迁徙工具完成一次性转换。

```csharp
using Vorcyc.Quiver;
using Vorcyc.Quiver.Migration;

var typeMap = new Dictionary<string, Type>
{
    [typeof(Document).FullName!] = typeof(Document)
};

await QuiverMigrator.MigrateAsync(
    sourceFile: "old-v3.vdb",
    destinationFile: "data-v4.vdb",
    typeMap: typeMap,
    options: new MigrateOptions
    {
        Overwrite = true,
        DeleteSourceOnSuccess = false,
        AllowNoop = true
    });
```

#### 与 Schema 规则组合

如果旧文件中的属性名已经和当前 CLR 类型不一致（例如 `OldTitle` → `Title`），必须在 `MigrateAsync` 解码旧文件时传入 `migrationRules`。否则旧字段在升级阶段就会被跳过，写出的 v4 文件中也不会再包含该值。

```csharp
var rule = MigrationBuilder<Document>.Build(m => m
    .RenameProperty("OldTitle", "Title"));

await QuiverMigrator.MigrateAsync(
    sourceFile: "old-v3.vdb",
    destinationFile: "data-v4.vdb",
    typeMap: typeMap,
    migrationRules: new Dictionary<string, SchemaMigrationRule>
    {
        [typeof(Document).FullName!] = rule
    },
    options: new MigrateOptions { Overwrite = true });
```

#### `MigrateOptions`

| 选项 | 说明 |
|------|------|
| `Overwrite` | 目标文件已存在时是否允许覆盖。 |
| `DeleteSourceOnSuccess` | 迁徙成功后是否删除源文件；源/目标相同时无意义。 |
| `AllowNoop` | 当源文件已经是 v4 且源/目标相同时，是否允许直接返回。 |

> 建议：迁徙前先备份旧文件；迁徙后可用 `QuiverDbFile.InspectAsync(path, verifyCrc: true)` 校验 v4 文件结构和 CRC。

---

