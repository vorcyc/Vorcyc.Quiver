namespace Vorcyc.Quiver.Migration;

/// <summary>
/// Thrown when the runtime load path detects that the target file uses a legacy format (v1/v2/v3)
/// older than v4 (<c>QDB\x04</c>).
/// <para>
/// From version 4.0 onwards, the runtime load path <b>only</b> accepts v4 files.
/// To upgrade from a legacy format, call <see cref="QuiverMigrator.MigrateAsync"/> to perform a one-time offline upgrade.
/// </para>
/// </summary>
public sealed class QuiverFormatVersionException : IOException
{
    /// <summary>The file path that was being loaded.</summary>
    public string FilePath { get; }

    /// <summary>The format version detected in the file (1, 2, or 3).</summary>
    public int DetectedVersion { get; }

    /// <summary>The minimum format version required by the current runtime (always 4).</summary>
    public int RequiredVersion => 4;

    /// <summary>Initializes a new instance of <see cref="QuiverFormatVersionException"/>.</summary>
    public QuiverFormatVersionException(string filePath, int detectedVersion)
        : base(BuildMessage(filePath, detectedVersion))
    {
        FilePath = filePath;
        DetectedVersion = detectedVersion;
    }

    private static string BuildMessage(string filePath, int detectedVersion) =>
        $"File '{filePath}' uses legacy Quiver format v{detectedVersion}. " +
        $"The 4.0 runtime only loads v4 (QDB\\x04). " +
        $"Call Vorcyc.Quiver.Migration.QuiverMigrator.MigrateAsync(...) to upgrade the file to v4 first.";
}
