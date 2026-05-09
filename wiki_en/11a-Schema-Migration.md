## 11a. Schema Migration (schema evolution details)

This page only covers **schema migration**: when entity structures evolve (adding/removing/renaming fields, changing value types), Quiver applies rules during `LoadAsync` or while the offline migrator decodes old files — no manual data file editing required.

If you need the distinction between format migration and schema migration, start with [Migration System: Format Migration vs Schema Migration](11-Migration-System.md).

### 9.1 Automatic Handling (Add / Remove Fields)

**Adding or removing fields requires zero configuration** — Quiver handles them automatically:

| Scenario | Behavior | Configuration Required |
|----------|----------|----------------------|
| **New field added** to entity | New field gets its CLR default value (`null`, `0`, `""`, etc.) | ❌ None |
| **Old field removed** from entity | Old field in the file is silently skipped during loading | ❌ None |

```csharp
// V1 entity
public class Document
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}

// V2 entity — added Category, removed nothing
public class Document
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;  // New field → default ""

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] Embedding { get; set; } = [];
}
// Loading V1 data into V2 entity: Category = "" (default), everything else loads normally.
```

### 9.2 Property Renaming

When a property is renamed (e.g., `OldTitle` → `Title`), declare the mapping in the context constructor via `ConfigureMigration<T>()`:

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

During `LoadAsync`, the storage provider maps the old property name in the file to the new property name in the current CLR type. Works with the v4 binary segmented format (`EntityMeta` / `VectorBlob` / `Blob` / `Tombstone`) as well as the JSON and XML export/import side channels.

### 9.2.1 Schema Fingerprint Protection

`QuiverSchemaMismatchException` lives in the `Vorcyc.Quiver.Migration` namespace. It indicates that the entity schema fingerprint stored in a v4 file does not match the current CLR entity declaration, and no schema migration rule was registered for that entity.

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

The recommended fix is to register `ConfigureMigration<T>()` / `SchemaMigrationRule` for the affected entity. If you explicitly accept the default behavior for added and removed fields, you can disable the guard:

```csharp
new QuiverDbOptions
{
    DatabasePath = "data.vdb",
    IgnoreSchemaFingerprintMismatch = true
};
```

### 9.3 Value Transformation

When a property's type or format changes (e.g., `int` → `double`, string format migration), declare a value transform:

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

Value transforms are applied **after** deserialization — the transform function receives the loaded value and returns the converted value.

### 9.4 Combined Usage

Rename and transform can be chained together:

```csharp
ConfigureMigration<Document>(m => m
    .RenameProperty("OldTitle", "Title")
    .RenameProperty("OldScore", "Score")
    .TransformValue("Score", v => v is int i ? (double)i : v));
```

**Processing Order**:
1. **Property renaming** — applied during deserialization (storage provider maps old names to new names)
2. **Value transformation** — applied after deserialization (context iterates entities and transforms values)

> ⚠️ Rename mapping uses the **CLR property name** (not the serialized name). For example, even if JSON uses camelCase `"oldTitle"`, the `RenameProperty` call uses `"OldTitle"` (PascalCase CLR name).

### 9.5 Schema Rules During Offline Format Migration

`ConfigureMigration<T>()` is applied by `QuiverDbContext.LoadAsync()` when reading a format supported by the current runtime. If you use `QuiverMigrator.MigrateAsync` to upgrade an old v1/v2/v3 file to v4, pass the same schema rules through the migrator's `migrationRules` parameter. Otherwise, renamed fields may be skipped while the old file is decoded, and the upgraded v4 file will no longer contain those values.

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

### 9.6 Combining with Format Migration

Schema migration handles how entity properties evolve. Format migration handles how an old `.vdb` container is upgraded to the current v4 segmented format. The two capabilities are orthogonal: use either one alone, or combine them in a single offline upgrade.

`QuiverMigrator.MigrateAsync` lives in the `Vorcyc.Quiver.Migration` namespace and upgrades old `QDB\x01` / `QDB\x02` / `QDB\x03` files to the current `QDB\x04` format. Runtime `QuiverDbContext.LoadAsync()` throws `QuiverFormatVersionException` for old containers, so production upgrade flows should explicitly run the migrator once.

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

#### Combining with Schema Rules

If old property names no longer match the current CLR type (for example `OldTitle` → `Title`), pass `migrationRules` while `MigrateAsync` decodes the old file. Otherwise, the old field is skipped during upgrade and the written v4 file can no longer recover that value.

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

| Option | Description |
|--------|-------------|
| `Overwrite` | Allow replacing an existing destination file. |
| `DeleteSourceOnSuccess` | Delete the source file after a successful migration; irrelevant when source and destination are the same file. |
| `AllowNoop` | Allow returning immediately when the source is already v4 and source equals destination. |

> Recommendation: back up the old file before migration, then validate the v4 result with `QuiverDbFile.InspectAsync(path, verifyCrc: true)`.

---

