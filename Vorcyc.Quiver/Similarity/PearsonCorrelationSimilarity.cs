using System.Numerics;
using Vorcyc.Quiver.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Pearson correlation coefficient: <c>r = Σ((xᵢ-μx)(yᵢ-μy)) / (√Σ(xᵢ-μx)² × √Σ(yᵢ-μy)²)</c>. Range [-1, 1].
/// <para>
/// Essentially <b>cosine similarity after mean-centering</b>: both vectors are centered (mean subtracted)
/// and then cosine similarity is computed. This makes the metric invariant to overall shifts in the
/// vector values, measuring only the linear co-variation trend across dimensions.
/// </para>
/// <para>
/// <b>Difference from cosine similarity</b>:
/// <list type="bullet">
///   <item>Cosine measures directional alignment of vectors relative to the origin.</item>
///   <item>Pearson measures linear correlation of the variation pattern across dimensions (direction after mean-centering).</item>
///   <item>When the per-dimension means differ significantly (e.g. TF-IDF vectors of documents with different lengths), Pearson is more robust.</item>
/// </list>
/// </para>
/// <para>
/// <b>Recommended use cases</b>:
/// <list type="bullet">
///   <item>Text embedding comparison (removes document-length / embedding-bias effects).</item>
///   <item>Document similarity with TF-IDF or BoW features.</item>
///   <item>Correlation analysis of user rating vectors in recommendation systems.</item>
///   <item>Gene expression profiling and time-series pattern matching.</item>
/// </list>
/// </para>
/// </summary>
public readonly struct PearsonCorrelationSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        var n = x.Length;
        if (n == 0) return 0f;

        // Pass 1: 均值（内部 Vector<T> 路径加速求和）
        var meanX = VectorMath.Sum(x) / n;
        var meanY = VectorMath.Sum(y) / n;

        // Pass 2: 去均值后的点积和 L2 范数²（数据仍在 L1 缓存中）
        int i = 0;
        float dotXY = 0f, normX2 = 0f, normY2 = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vmx = new Vector<float>(meanX);
            var vmy = new Vector<float>(meanY);
            var vDot = Vector<float>.Zero;
            var vNx2 = Vector<float>.Zero;
            var vNy2 = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;

            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var dx = new Vector<float>(x[i..]) - vmx;
                var dy = new Vector<float>(y[i..]) - vmy;
                vDot += dx * dy;
                vNx2 += dx * dx;
                vNy2 += dy * dy;
            }

            dotXY = Vector.Sum(vDot);
            normX2 = Vector.Sum(vNx2);
            normY2 = Vector.Sum(vNy2);
        }

        for (; i < n; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            dotXY += dx * dy;
            normX2 += dx * dx;
            normY2 += dy * dy;
        }

        var denom = MathF.Sqrt(normX2 * normY2);
        return denom > 0f ? dotXY / denom : 0f;
    }
}
