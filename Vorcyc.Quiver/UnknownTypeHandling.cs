namespace Vorcyc.Quiver;

/// <summary>
/// Behavior of <see cref="QuiverDbContext.LoadAsync"/> when the on-disk file contains
/// entity segments whose stored <c>TypeName</c> is not registered on the current
/// <see cref="QuiverDbContext"/> (i.e. has no matching <c>QuiverSet&lt;T&gt;</c> property).
/// <para>
/// Historically such segments were <b>silently discarded</b>, leading to the most common pitfall:
/// after renaming or moving an entity class the user would open the old file and get a database that
/// appeared normal but contained no data, with no error or warning of any kind.
/// This enum lets you choose the desired behavior explicitly.
/// </para>
/// </summary>
public enum UnknownTypeHandling
{
    /// <summary>
    /// Silently ignore unknown segments — preserves the legacy compatible behavior.
    /// Recommended only when intentionally loading a subset of the entity types stored in the file.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Default: emits a warning via <see cref="System.Diagnostics.Trace.TraceWarning(string, object[])"/>
    /// listing the unregistered <c>TypeName</c> values and their entity counts found in the file,
    /// then continues loading the known segments.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// Throws <see cref="System.InvalidOperationException"/> with a message listing the unknown types
    /// and suggested remedies (register a matching <c>QuiverSet&lt;T&gt;</c>, or use
    /// <c>QuiverMigrator.MigrateAsync</c> with a <c>typeMap</c> to upgrade the legacy file).
    /// Recommended during development to prevent silent data loss.
    /// </summary>
    Throw = 2,
}
