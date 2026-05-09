using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Generalized Jaccard similarity: <c>similarity = Σ min(xᵢ, yᵢ) / Σ max(xᵢ, yᵢ)</c>. Range [0, 1].
/// <para>
/// A continuous-value extension of the classical Jaccard coefficient (also known as Ruzicka similarity).
/// Reduces to the standard set Jaccard coefficient <c>|A∩B| / |A∪B|</c> when vectors are binary (0/1).
/// </para>
/// <para>
/// <b>Recommended use cases</b>:
/// <list type="bullet">
///   <item>Document similarity with sparse non-negative text features such as BoW and TF-IDF.</item>
///   <item>Set overlap measurement for binary feature vectors.</item>
///   <item>Community similarity for species abundance data in ecology.</item>
///   <item>Comparison of histogram features (color histograms, gradient histograms).</item>
/// </list>
/// </para>
/// <para>
/// <b>Note</b>: requires non-negative vector elements. Negative elements may produce unexpected
/// results due to min/max behavior.
/// </para>
/// </summary>
public readonly struct JaccardSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int i = 0;
        float sumMin = 0f, sumMax = 0f;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var vMin = Vector<float>.Zero;
            var vMax = Vector<float>.Zero;
            var lastBlock = x.Length - x.Length % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var vx = new Vector<float>(x[i..]);
                var vy = new Vector<float>(y[i..]);
                vMin += Vector.Min(vx, vy);
                vMax += Vector.Max(vx, vy);
            }
            sumMin = Vector.Sum(vMin);
            sumMax = Vector.Sum(vMax);
        }

        for (; i < x.Length; i++)
        {
            sumMin += MathF.Min(x[i], y[i]);
            sumMax += MathF.Max(x[i], y[i]);
        }

        return sumMax > 0f ? sumMin / sumMax : 1f;
    }
}
