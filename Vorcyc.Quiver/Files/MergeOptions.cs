namespace Vorcyc.Quiver.Files;

/// <summary>
/// Conflict resolution policy when merging multiple v4 files.
/// </summary>
public enum MergeConflictPolicy
{
    /// <summary>
    /// Concatenates all segments from every input file without deduplication. Fastest path — O(I/O), pure byte copy.
    /// Suitable for archive scenarios where the application layer guarantees globally unique primary keys.
    /// </summary>
    Append = 0,

    /// <summary>
    /// Deduplicates by <c>QuiverKey</c> primary key; when the same key appears in multiple files, the entry from the
    /// later file in the input sequence is kept. Requires decoding each segment to build a key → segment-position index.
    /// </summary>
    LastWriterWins = 1,

    /// <summary>
    /// Deduplicates by <c>QuiverKey</c> primary key; when the same key appears in multiple files, the entry from the
    /// earlier file in the input sequence is kept. Requires decoding each segment to build a key → segment-position index.
    /// </summary>
    FirstWriterWins = 2,
}

/// <summary>
/// Options that control the multi-file merge operation.
/// </summary>
public class MergeOptions
{
    /// <summary>Conflict resolution policy. Default is <see cref="MergeConflictPolicy.Append"/>.</summary>
    public MergeConflictPolicy ConflictPolicy { get; init; } = MergeConflictPolicy.Append;

    /// <summary>Whether to verify the CRC32 checksum of every input segment during the merge. Default is <see langword="true"/>.</summary>
    public bool VerifyCrc { get; init; } = true;

    /// <summary>Optional progress reporter for the merge operation.</summary>
    public IProgress<MergeProgress>? Progress { get; init; }
}

/// <summary>
/// An immutable progress snapshot emitted during a merge operation.
/// </summary>
/// <param name="ProcessedSegments">Number of segments processed so far.</param>
/// <param name="TotalSegments">Total number of segments across all input files.</param>
/// <param name="ProcessedBytes">Total bytes written to the destination file so far.</param>
/// <param name="CurrentFile">Path of the input file currently being processed.</param>
public readonly record struct MergeProgress(
    int ProcessedSegments,
    int TotalSegments,
    long ProcessedBytes,
    string CurrentFile);

/// <summary>
/// Diagnostic information for a <c>Quiver</c> v4 database file.
/// </summary>
public class QuiverFileInfo
{
    /// <summary>Absolute path of the inspected file.</summary>
    public required string FilePath { get; init; }

    /// <summary>On-disk format version (1 / 2 / 3 / 4).</summary>
    public required int FormatVersion { get; init; }

    /// <summary>Total file size in bytes.</summary>
    public required long FileSize { get; init; }

    /// <summary>Ordered list of segments found in the file. Only populated for v4 files; empty for older versions.</summary>
    public required IReadOnlyList<SegmentInfo> Segments { get; init; }

    /// <summary>Whether all per-segment CRC32 checksums passed. Only meaningful for v4 files.</summary>
    public required bool CrcValid { get; init; }

    /// <summary>Per-type entity counts accumulated across all segments.</summary>
    public required IReadOnlyDictionary<string, long> EntityCounts { get; init; }
}

/// <summary>
/// Diagnostic information for a single segment within a v4 file.
/// </summary>
/// <param name="TypeName">Fully qualified CLR type name of the entity type stored in this segment.</param>
/// <param name="Offset">Byte offset of the segment start within the file.</param>
/// <param name="Length">Length of the segment in bytes.</param>
/// <param name="EntityCount">Number of entity records in this segment.</param>
/// <param name="StoredCrc">CRC32 value recorded in the footer for this segment.</param>
/// <param name="ActualCrc">CRC32 computed from the segment bytes; a mismatch with <see cref="StoredCrc"/> indicates corruption.</param>
/// <param name="Kind">Segment kind (Mixed / EntityMeta / VectorBlob / Blob / Tombstone). Schema v1 files report all segments as Mixed.</param>
/// <param name="FieldName">Entity field name for VectorBlob and Blob segments; <see langword="null"/> for EntityMeta and Mixed segments.</param>
/// <param name="Dim">Vector dimensionality for VectorBlob segments; <c>0</c> for all other kinds.</param>
public readonly record struct SegmentInfo(
    string TypeName,
    long Offset,
    long Length,
    int EntityCount,
    uint StoredCrc,
    uint ActualCrc,
    SegmentKind Kind = SegmentKind.Mixed,
    string? FieldName = null,
    int Dim = 0);

/// <summary>
/// Public mirror of the segment-kind discriminator used internally by <see cref="Vorcyc.Quiver.Storage.BinaryStorageProvider"/>.
/// </summary>
public enum SegmentKind : byte
{
    /// <summary>Legacy self-describing segment containing entity metadata and all field data.</summary>
    Mixed = 0,
    /// <summary>Entity metadata segment (scalar fields and vector references; no raw vector or blob data).</summary>
    EntityMeta = 1,
    /// <summary>Raw vector blob segment for a single entity type and a single vector field.</summary>
    VectorBlob = 2,
    /// <summary>Large-field blob segment for a single entity type and a single <c>[QuiverLargeField]</c> property.</summary>
    Blob = 3,
    /// <summary>Tombstone segment recording deletion records (type + key) applied during load.</summary>
    Tombstone = 4,
    /// <summary>Index snapshot segment for indexes that support topology persistence (e.g. HNSW).</summary>
    IndexSnapshot = 5,
}
