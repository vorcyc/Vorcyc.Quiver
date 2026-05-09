using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Manhattan distance (L1 norm) converted to similarity: <c>similarity = 1 / (1 + Σ|xᵢ - yᵢ|)</c>. Range (0, 1].
/// <para>
/// Also known as "city-block distance": computes the sum of absolute differences along each coordinate axis.
/// Less sensitive to outlier dimensions than Euclidean distance because differences are not squared.
/// </para>
/// <para>
/// <b>Recommended use cases</b>:
/// <list type="bullet">
///   <item>Sparse feature vectors where most dimensions are zero and only a few have non-zero values.</item>
///   <item>High-dimensional spaces requiring a metric that is more robust to anomalous dimensions.</item>
///   <item>User preference vector comparison in recommendation systems.</item>
/// </list>
/// </para>
/// </summary>
public readonly struct ManhattanSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int i = 0;
        float sum = 0f;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = x.Length - x.Length % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                vsum += Vector.Abs(new Vector<float>(x[i..]) - new Vector<float>(y[i..]));
            sum = Vector.Sum(vsum);
        }

        for (; i < x.Length; i++)
            sum += MathF.Abs(x[i] - y[i]);

        return 1f / (1f + sum);
    }
}
