using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Canberra distance converted to similarity: <c>similarity = 1 - (1/n) × Σ |xᵢ-yᵢ| / (|xᵢ| + |yᵢ|)</c>. Range [0, 1].
/// <para>
/// A weighted L1 distance where each dimension's difference is normalized by the magnitude of that dimension.
/// Highly sensitive to values near zero (small denominators produce large weights), making it especially
/// suitable for sparse data. When <c>xᵢ = yᵢ = 0</c> the contribution of that dimension is 0 (perfect match).
/// </para>
/// <para>
/// <b>Recommended use cases</b>:
/// <list type="bullet">
///   <item>Document distance with sparse text features (TF-IDF, BoW) — differences in sparse dimensions are not dominated by dense ones.</item>
///   <item>Comparison of chemical fingerprints and ecological count data.</item>
///   <item>Robust metric when feature magnitudes vary widely (mixed-scale features).</item>
/// </list>
/// </para>
/// </summary>
public readonly struct CanberraSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        var n = x.Length;
        if (n == 0) return 1f;

        int i = 0;
        float sum = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var vx = new Vector<float>(x[i..]);
                var vy = new Vector<float>(y[i..]);
                var absDiff = Vector.Abs(vx - vy);
                var absDenom = Vector.Abs(vx) + Vector.Abs(vy);
                // When denominator is zero substitute 1 (numerator is also zero, so the quotient is 0 — semantically correct)
                var safeDenom = Vector.ConditionalSelect(
                    Vector.GreaterThan(absDenom, Vector<float>.Zero), absDenom, Vector<float>.One);
                vsum += absDiff / safeDenom;
            }
            sum = Vector.Sum(vsum);
        }

        for (; i < n; i++)
        {
            var absX = MathF.Abs(x[i]);
            var absY = MathF.Abs(y[i]);
            var denom = absX + absY;
            if (denom > 0f)
                sum += MathF.Abs(x[i] - y[i]) / denom;
        }

        return 1f - sum / n;
    }
}
