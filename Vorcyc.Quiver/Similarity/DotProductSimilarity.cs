using Vorcyc.Quiver.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Dot product (inner product) similarity: <c>x·y = Σ(xᵢ × yᵢ)</c>.
/// <para>
/// The value range depends on vector magnitudes and distribution. The dot product of normalized
/// vectors is equivalent to cosine similarity. Also used as the actual compute function for
/// <see cref="DistanceMetric.Cosine"/> when pre-normalization is enabled.
/// </para>
/// <para>
/// Well suited for maximum inner-product search (MIPS) and scenarios that operate on pre-normalized vectors.
/// </para>
/// </summary>
public readonly struct DotProductSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => VectorMath.Dot(x, y);
}
