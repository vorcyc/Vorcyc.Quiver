using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Chebyshev distance (L∞ norm) converted to similarity: <c>similarity = 1 / (1 + max|xᵢ - yᵢ|)</c>. Range (0, 1].
/// <para>
/// Considers only the largest absolute difference across all dimensions, ignoring all others.
/// Equivalent to the minimum number of moves a chess king needs to travel from one square to another.
/// </para>
/// <para>
/// <b>Recommended use cases</b>:
/// <list type="bullet">
///   <item>Detecting the maximum deviation across any single dimension.</item>
///   <item>Feature deviation detection in quality control (alert when any single metric exceeds a threshold).</item>
///   <item>Chessboard distance, grid path planning, and other discrete-space problems.</item>
/// </list>
/// </para>
/// </summary>
public readonly struct ChebyshevSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int i = 0;
        float max = 0f;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var vmax = Vector<float>.Zero;
            var lastBlock = x.Length - x.Length % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                vmax = Vector.Max(vmax, Vector.Abs(new Vector<float>(x[i..]) - new Vector<float>(y[i..])));
            for (int j = 0; j < Vector<float>.Count; j++)
                if (vmax[j] > max) max = vmax[j];
        }

        for (; i < x.Length; i++)
        {
            var diff = MathF.Abs(x[i] - y[i]);
            if (diff > max) max = diff;
        }

        return 1f / (1f + max);
    }
}
