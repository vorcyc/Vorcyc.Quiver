using Vorcyc.Quiver.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Euclidean distance converted to similarity: <c>similarity = 1 / (1 + ‖x - y‖₂)</c>. Range (0, 1].
/// <para>
/// Similarity is 1 when the distance is 0 (identical vectors) and approaches 0 as the distance grows.
/// Well suited for spatial coordinates, physical distances, and other scenarios where absolute differences matter.
/// </para>
/// </summary>
public readonly struct EuclideanSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => 1f / (1f + VectorMath.Distance(x, y));
}
