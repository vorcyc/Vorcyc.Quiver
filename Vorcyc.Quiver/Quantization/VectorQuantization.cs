namespace Vorcyc.Quiver;

/// <summary>
/// Vector storage quantization mode declared on <see cref="QuiverVectorAttribute"/>.
/// <para>
/// Quantization is purely a <em>storage</em> concern: it changes the on-disk row layout
/// of a <c>VectorBlob</c> segment and the byte footprint of the corresponding
/// <see cref="Indexing.IVectorStore"/>, but it does not alter the public
/// <c>float[]</c> contract exposed by entity properties.
/// </para>
/// </summary>
public enum VectorQuantization : byte
{
    /// <summary>No quantization. Vectors are stored as raw <c>float32</c> (default, lossless).</summary>
    None = 0,

    /// <summary>
    /// 8-bit signed scalar quantization. Each component is encoded as one signed byte using a
    /// per-row scale and a per-segment bias, reducing on-disk and mmap footprint to ~25% of
    /// <see cref="None"/>. Recommended for normalized embeddings (cosine / dot-product metrics).
    /// </summary>
    Sq8 = 1,
}
