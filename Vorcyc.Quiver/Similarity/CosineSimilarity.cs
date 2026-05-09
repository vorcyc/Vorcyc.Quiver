using Vorcyc.Quiver.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Cosine similarity: <c>cos(θ) = (x·y) / (‖x‖ × ‖y‖)</c>. Range [-1, 1].
/// <para>
/// Measures the directional alignment of two vectors, independent of their absolute magnitudes.
/// Well suited for text embeddings, semantic search, and similar scenarios.
/// </para>
/// <para>
/// <b>Note</b>: when <see cref="DistanceMetric.Cosine"/> has pre-normalization enabled,
/// <see cref="DotProductSimilarity"/> is used instead (after normalization Dot = Cosine, avoiding the division).
/// This type is used only in non-pre-normalized scenarios or when explicitly specified by the user.
/// </para>
/// </summary>
public readonly struct CosineSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => VectorMath.CosineSimilarity(x, y);
}
