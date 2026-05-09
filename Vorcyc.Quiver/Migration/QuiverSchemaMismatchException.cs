namespace Vorcyc.Quiver.Migration;

/// <summary>
/// Thrown when the entity schema fingerprint stored in a v4 file header does not match the current CLR
/// entity declaration and no <see cref="SchemaMigrationRule"/> has been registered for that type.
/// <para>
/// This is a safety guard that prevents fields from being silently defaulted or skipped when the user
/// renames properties, changes types, or alters vector dimensions without calling
/// <c>ConfigureMigration&lt;T&gt;(...)</c>.
/// </para>
/// <para>
/// To resolve: ① register a migration rule for the affected type inside <c>OnConfiguring</c>;
/// or ② accept the default behavior (new fields default, removed fields ignored) by setting
/// <see cref="QuiverDbOptions.IgnoreSchemaFingerprintMismatch"/> = <see langword="true"/> to suppress the check.
/// </para>
/// </summary>
public sealed class QuiverSchemaMismatchException : InvalidOperationException
{
    /// <summary>The fully qualified name of the entity type whose fingerprint did not match.</summary>
    public string TypeName { get; }

    /// <summary>The schema fingerprint (FNV-1a 64-bit) stored in the file.</summary>
    public ulong FileFingerprint { get; }

    /// <summary>The schema fingerprint (FNV-1a 64-bit) computed from the current entity declaration.</summary>
    public ulong CurrentFingerprint { get; }

    /// <summary>Initializes a new instance of <see cref="QuiverSchemaMismatchException"/>.</summary>
    public QuiverSchemaMismatchException(string typeName, ulong fileFingerprint, ulong currentFingerprint)
        : base(BuildMessage(typeName, fileFingerprint, currentFingerprint))
    {
        TypeName = typeName;
        FileFingerprint = fileFingerprint;
        CurrentFingerprint = currentFingerprint;
    }

    private static string BuildMessage(string typeName, ulong file, ulong current) =>
        $"Schema fingerprint mismatch for entity '{typeName}': " +
        $"file=0x{file:X16}, current=0x{current:X16}. " +
        $"Register a SchemaMigrationRule via ConfigureMigration<T>(...) " +
        $"or set QuiverDbOptions.IgnoreSchemaFingerprintMismatch=true to suppress this check.";
}
