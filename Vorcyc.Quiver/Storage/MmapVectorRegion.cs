namespace Vorcyc.Quiver.Storage;

/// <summary>
/// Describes the physical layout of a vector payload region in a v4 file, allowing
/// <see cref="Vorcyc.Quiver.Indexing.MmapVectorStore"/> to bind the region directly as a
/// <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/> view.
/// <para>
/// Produced during load by the mmap-aware <c>LoadAsync</c> path in <see cref="BinaryStorageProvider"/>:
/// for fields matched by the <c>isMmapField</c> predicate, the loader does not allocate a <c>float[]</c>
/// and write it back to the entity. Instead, it returns this structure; the caller
/// (<see cref="Vorcyc.Quiver.QuiverDbContext"/>) then binds it to the corresponding store.
/// </para>
/// </summary>
/// <param name="TypeName">The owning type full name, exactly matching the <c>typeName</c> stored in the entity metadata segment.</param>
/// <param name="FieldName">The vector field name as stored in the file, before schema rename mapping; callers must apply migration rules themselves.</param>
/// <param name="Dim">The effective dimension exposed at runtime. Equal to <paramref name="StorageDim"/> when no truncation is applied.</param>
/// <param name="RowCount">The number of vector rows in the region.</param>
/// <param name="PayloadOffset">The absolute offset of row data within the database file, after the segment header, scales, and null bitmap.</param>
/// <param name="EntityChunkStart">The starting index in the <c>[TypeName]</c> entity list returned by <c>LoadAsync</c>; maps row <c>i</c> to <c>entities[Start + i]</c>.</param>
/// <param name="EntityChunkCount">The number of entities produced by the corresponding entity metadata segment. Usually equals <see cref="RowCount"/>.</param>
/// <param name="NullBitmap">A null bitmap of length <c>⌈RowCount/8⌉</c>; <c>null</c> means the region contains no null slots.</param>
/// <param name="Encoding">The vector row encoding (Float32 / Sq8).</param>
/// <param name="StorageDim">The physical dimension of each stored row; row bytes are <c>StorageDim</c> for SQ8 and <c>StorageDim × 4</c> for Float32.</param>
/// <param name="Sq8Scales">SQ8 only: per-row scale values with length <see cref="RowCount"/>; <c>null</c> for other encodings.</param>
public readonly record struct MmapVectorRegion(
    string TypeName,
    string FieldName,
    int Dim,
    int RowCount,
    long PayloadOffset,
    int EntityChunkStart,
    int EntityChunkCount,
    byte[]? NullBitmap,
    VectorBlobEncoding Encoding = VectorBlobEncoding.Float32,
    int StorageDim = 0,
    float[]? Sq8Scales = null);
