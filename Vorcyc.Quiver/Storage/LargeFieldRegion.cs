namespace Vorcyc.Quiver.Storage;

internal readonly record struct LargeFieldRegion(
    string TypeName,
    string FieldName,
    int RowCount,
    long PayloadOffset,
    int EntityChunkStart,
    int EntityChunkCount,
    byte[]? NullBitmap,
    long[] Offsets);
