# 11. Migration System: Format Migration vs Schema Migration

Quiver separates "migration" into two complementary but independent layers, with a separate stable entity identity mechanism for namespace or class-name refactors.

```text
Format migration: old file format → current file format
Schema migration: old field structure → current field structure
Entity identity: legacy/stable TypeName → current CLR entity type
```

## 11.1 One-page comparison

| Layer | Goal | Entry API | Typical scenario | Does not handle |
|---|---|---|---|---|
| **Format Migration** | Upgrade the on-disk container format | `Vorcyc.Quiver.Migration.QuiverMigrator.MigrateAsync(...)` | v1/v2/v3 `.vdb` → v4 `.vdb` | Inferring renamed fields, guessing legacy type names |
| **Schema Migration** | Evolve entity field structure | `ConfigureMigration<T>()` / `MigrationBuilder<T>` / `SchemaMigrationRule` | Added/removed fields, property renames, value transforms | File-format upgrades, unmatched `TypeName` values |
| **Stable entity identity** | Keep the file `TypeName` stable | `[QuiverEntity("...")]` + `typeMap` + `UnknownTypeHandling` | Namespace/class-name refactors, sharing data across projects | Field-structure changes, binary-format upgrades |

## 11.2 Format Migration

Format migration upgrades an old `.vdb` container to the current v4 segmented format. The entry point lives in a separate namespace:

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

The v4 runtime `QuiverDbContext.LoadAsync()` no longer opens v1/v2/v3 containers directly. It throws `QuiverFormatVersionException` and asks the caller to run the offline migrator first.

## 11.3 Schema Migration

Schema migration handles entity field evolution. It runs during or after deserialization and addresses property renames and value transforms:

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

Adding or removing fields requires no configuration: new fields use CLR defaults; removed fields are skipped while loading.

If the schema fingerprint stored in a v4 file does not match the current CLR entity declaration and no migration rule was registered for that type, the runtime throws `Vorcyc.Quiver.Migration.QuiverSchemaMismatchException`. Register `ConfigureMigration<T>()` to handle the difference, or set `QuiverDbOptions.IgnoreSchemaFingerprintMismatch = true` if you explicitly accept the default compatibility behavior.

For detailed rules, see [Schema Migration (schema evolution details)](11-Schema-Migration.md).

## 11.4 Combining both layers

If an old file is both an old format and has renamed fields or value conversions, pass both:

- `typeMap`: legacy file `TypeName` → current CLR type
- `migrationRules`: old field structure → current field structure

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

> Key point: `migrationRules` keys should match the legacy type-name keys used by `typeMap`, because the rules are applied while decoding the old file.

## 11.5 Relationship to `[QuiverEntity]`

`[QuiverEntity("...")]` solves stable entity identity. It is not format migration and not schema migration.

```csharp
[QuiverEntity("Document/v1")]
public class Document
{
	[QuiverKey] public string Id { get; set; } = "";

	[QuiverVector(384)]
	public float[] Embedding { get; set; } = [];
}
```

By default, Quiver uses `Type.FullName` as the file `TypeName`. This means namespace or class-name changes alter the on-disk identity. With `[QuiverEntity("Document/v1")]`, new files are written with the stable name; the runtime also accepts the old `FullName` as a compatibility alias.

During development or migration validation, enable strict unknown-type checks:

```csharp
new QuiverDbOptions
{
	DatabasePath = "data.vdb",
	UnknownTypeHandling = UnknownTypeHandling.Throw
};
```

## 11.6 Recommended upgrade flow

1. **Back up the old file**.
2. If the old application used WAL, run `LoadAsync()` + `SaveAsync()` once on a 3.2.x app to merge `.wal` into `.vdb`.
3. Identify the legacy `TypeName` values stored in the file and configure `typeMap` for each one.
4. If field structure changed, build the corresponding `SchemaMigrationRule`.
5. Run `QuiverMigrator.MigrateAsync(...)` to generate a v4 file.
6. Validate the v4 file with `QuiverDbFile.InspectAsync(path, verifyCrc: true)`.
7. Load the result with the current v4 runtime using `LoadAsync()`.
