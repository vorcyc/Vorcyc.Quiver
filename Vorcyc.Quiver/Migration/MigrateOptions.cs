namespace Vorcyc.Quiver.Migration;

/// <summary>
/// Options that control the behavior of <see cref="QuiverMigrator.MigrateAsync"/>.
/// </summary>
public sealed class MigrateOptions
{
    /// <summary>
    /// Whether to produce an output file when the source is already a v4 file.
    /// <see langword="true"/> (default): copy the source to the destination as-is when the paths differ; do nothing when they are the same.
    /// <see langword="false"/>: throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool AllowNoop { get; set; } = true;

    /// <summary>
    /// Whether to overwrite the destination file if it already exists.
    /// Default is <see langword="false"/>; an existing destination throws <see cref="IOException"/>.
    /// </summary>
    public bool Overwrite { get; set; } = false;

    /// <summary>
    /// Whether to delete the source file after a successful migration.
    /// Only takes effect when the source and destination paths differ. Default is <see langword="false"/>.
    /// </summary>
    public bool DeleteSourceOnSuccess { get; set; } = false;
}
