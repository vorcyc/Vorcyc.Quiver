using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver.Migration;

/// <summary>
/// File-format <b>version migration</b> tool for Quiver databases (v1/v2/v3 → v4).
/// <para>
/// From version 4.0 onwards, <see cref="QuiverDbContext.LoadAsync"/> no longer transparently accepts legacy
/// formats; encountering an old file throws <see cref="QuiverFormatVersionException"/>.
/// This class provides a one-time offline upgrade entry point.
/// </para>
/// <para>
/// Orthogonal to <b>schema migration</b> (<see cref="SchemaMigrationRule"/> / <see cref="MigrationBuilder{TEntity}"/>):
/// schema migration describes how entity properties evolve (rename / type conversion / add / remove fields)
/// and is applied at runtime during <c>LoadAsync</c>; this tool is only responsible for upgrading the file
/// container to the current format version. Both can be used together — pass <paramref name="migrationRules"/>
/// to <see cref="MigrateAsync"/> to apply schema rules at the same time as the format upgrade.
/// </para>
/// </summary>
public static class QuiverMigrator
{
    /// <summary>
    /// Upgrades a legacy Quiver file (v1/v2/v3) to the v4 format.
    /// <para>
    /// Internally, the file is loaded through <see cref="BinaryStorageProvider"/>'s version-agnostic load
    /// path and then written to <paramref name="destinationFile"/> in the v4 SegmentSet + Footer format.
    /// The write is routed through a <c>.tmp</c> file and finalized with an atomic <c>File.Move</c>
    /// to prevent a partial file being left behind on crash.
    /// </para>
    /// </summary>
    /// <param name="sourceFile">Path to the source file (v1/v2/v3/v4 all accepted).</param>
    /// <param name="destinationFile">Path for the output v4 file. May equal <paramref name="sourceFile"/> for an in-place upgrade.</param>
    /// <param name="typeMap">Maps type full name → CLR <see cref="Type"/>. Types present in the file but absent from the map are silently skipped.</param>
    /// <param name="migrationRules">Optional entity schema migration rules keyed by type full name. Applied during decoding to rename properties or transform values.</param>
    /// <param name="options">Migration behavior switches. May be <see langword="null"/> to use defaults.</param>
    /// <exception cref="FileNotFoundException">The source file does not exist.</exception>
    /// <exception cref="InvalidDataException">The source file format is not recognized.</exception>
    /// <exception cref="IOException">The destination already exists and <see cref="MigrateOptions.Overwrite"/> is not set.</exception>
    public static async Task MigrateAsync(
        string sourceFile,
        string destinationFile,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null,
        MigrateOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(destinationFile);
        ArgumentNullException.ThrowIfNull(typeMap);
        options ??= new MigrateOptions();

        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("Source file not found.", sourceFile);

        var sameTarget = string.Equals(
            Path.GetFullPath(sourceFile),
            Path.GetFullPath(destinationFile),
            StringComparison.OrdinalIgnoreCase);

        if (!sameTarget && File.Exists(destinationFile) && !options.Overwrite)
            throw new IOException(
                $"Destination file '{destinationFile}' already exists. Set MigrateOptions.Overwrite=true to replace it.");

        // Probe version first so we can short-circuit a noop on already-v4 files.
        int version;
        using (var probe = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
        {
            Span<byte> magic = stackalloc byte[4];
            if (probe.Read(magic) != 4)
                throw new InvalidDataException($"File '{sourceFile}' is too short to be a Quiver database.");
            version = magic[3] switch
            {
                0x01 when magic[0] == (byte)'Q' && magic[1] == (byte)'D' && magic[2] == (byte)'B' => 1,
                0x02 when magic[0] == (byte)'Q' && magic[1] == (byte)'D' && magic[2] == (byte)'B' => 2,
                0x03 when magic[0] == (byte)'Q' && magic[1] == (byte)'D' && magic[2] == (byte)'B' => 3,
                0x04 when magic[0] == (byte)'Q' && magic[1] == (byte)'D' && magic[2] == (byte)'B' => 4,
                _ => throw new InvalidDataException($"File '{sourceFile}' is not a recognized Quiver database (bad magic).")
            };
        }

        if (version == 4 && sameTarget)
        {
            if (!options.AllowNoop)
                throw new InvalidOperationException(
                    $"Source file is already v4 and destination equals source; set MigrateOptions.AllowNoop=true to permit no-op.");
            return; // nothing to do
        }

        // For legacy (v1/v2/v3) files: peek the stored TypeNames first so we can fail loudly
        // if typeMap is missing entries — otherwise LoadLegacy silently skips unknown types
        // and the migrator would produce an empty v4 file without any error.
        if (version < 4)
        {
            var peeked = BinaryStorageProvider.PeekLegacyTypeCounts(sourceFile);
            var missing = peeked
                .Where(kv => kv.Value > 0 && !typeMap.ContainsKey(kv.Key))
                .ToList();
            if (missing.Count > 0)
            {
                var detail = string.Join(", ",
                    missing.Select(kv => $"\"{kv.Key}\" ({kv.Value} entities)"));
                var known = typeMap.Count == 0 ? "(empty)" : string.Join(", ", typeMap.Keys.Select(k => $"\"{k}\""));
                throw new InvalidOperationException(
                    $"Migration aborted: source file '{sourceFile}' contains entity type(s) {detail} " +
                    $"but the supplied typeMap does not map them. " +
                    $"Add entries keyed by the exact stored TypeName. " +
                    $"Current typeMap keys: {known}.");
            }
        }

        // Load entities through the internal any-version path.
        var provider = new BinaryStorageProvider();
        var loaded = await provider.LoadAnyVersionAsync(sourceFile, typeMap, migrationRules)
            .ConfigureAwait(false);

        // Build the sets dictionary expected by SaveAsync.
        // IMPORTANT: rekey segments by the CURRENT CLR type's stored TypeName (from
        // [QuiverEntity] or Type.FullName via EntityTypeName.Resolve), not the legacy
        // stored TypeName. The runtime QuiverDbContext matches segments via the same
        // resolver, so preserving an old namespace-qualified name here would make the
        // migrated v4 file unreadable by the new code (segments silently skipped).
        var sets = new Dictionary<string, (Type Type, List<object> Entities)>(loaded.Count);
        foreach (var (typeName, entities) in loaded)
        {
            if (!typeMap.TryGetValue(typeName, out var clrType)) continue;
            var newKey = EntityTypeName.Resolve(clrType);
            if (sets.TryGetValue(newKey, out var existing))
            {
                existing.Entities.AddRange(entities);
                sets[newKey] = existing;
            }
            else
            {
                sets[newKey] = (clrType, entities);
            }
        }

        // Write to a tmp path; SaveAsync itself handles atomic move + flush.
        var tmpDest = destinationFile + ".migrating.tmp";
        if (File.Exists(tmpDest)) File.Delete(tmpDest);

        await provider.SaveAsync(tmpDest, sets).ConfigureAwait(false);

        if (sameTarget)
        {
            // Replace source atomically.
            File.Move(tmpDest, destinationFile, overwrite: true);
        }
        else
        {
            if (File.Exists(destinationFile)) File.Delete(destinationFile);
            File.Move(tmpDest, destinationFile);

            if (options.DeleteSourceOnSuccess && File.Exists(sourceFile))
                File.Delete(sourceFile);
        }
    }
}
