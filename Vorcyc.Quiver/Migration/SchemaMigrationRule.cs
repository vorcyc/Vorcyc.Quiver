namespace Vorcyc.Quiver.Migration;

/// <summary>
/// Stores the schema migration rules for a single entity type: property rename mappings and value transform functions.
/// <para>
/// Built by <see cref="MigrationBuilder{TEntity}"/> and passed to the storage provider during
/// <see cref="QuiverDbContext.LoadAsync"/> to enable transparent schema migration.
/// </para>
/// <para>
/// Also accepted as optional input by <see cref="QuiverMigrator.MigrateAsync"/> so that an offline
/// format upgrade can apply property renames while decoding the legacy file.
/// </para>
/// </summary>
public class SchemaMigrationRule
{
    /// <summary>
    /// Property rename map: old property name → new property name.
    /// <para>
    /// During load, the storage provider uses this map to translate old property names found in the
    /// file to the corresponding current CLR property names.
    /// </para>
    /// </summary>
    public Dictionary<string, string> PropertyRenames { get; } = [];

    /// <summary>
    /// Value transform map: new property name → transform function.
    /// <para>
    /// After loading, the specified transform is applied to the property value
    /// (e.g. type conversion, format change).
    /// </para>
    /// </summary>
    public Dictionary<string, Func<object?, object?>> ValueTransforms { get; } = [];

    /// <summary>
    /// Lazily computed reverse rename map: new property name → old property name.
    /// <para>
    /// Used during XML load to look up the legacy element name by the current property name.
    /// </para>
    /// </summary>
    private Dictionary<string, string>? _reverseRenames;

    /// <summary>
    /// Gets the reverse rename map: new property name → old property name.
    /// </summary>
    public Dictionary<string, string> ReverseRenames =>
        _reverseRenames ??= PropertyRenames.ToDictionary(kv => kv.Value, kv => kv.Key);
}
