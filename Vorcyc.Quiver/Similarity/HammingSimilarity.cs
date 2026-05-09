using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Hamming similarity: <c>similarity = 1 - (number of differing elements / total dimensions)</c>. Range [0, 1].
/// <para>
/// Measures the proportion of corresponding positions at which the two vectors differ.
/// Returns 1 when the vectors are identical and 0 when every element differs.
/// </para>
/// <para>
/// <b>Recommended use cases</b>:
/// <list type="bullet">
///   <item>Bit-level comparison of binary (0/1) vectors.</item>
///   <item>Comparing binary hash codes produced by locality-sensitive hashing (LSH).</item>
///   <item>Fast approximate matching of text fingerprints such as SimHash and MinHash.</item>
///   <item>Comparison of quantized vectors with a finite discrete value set.</item>
/// </list>
/// </para>
/// <para>
/// <b>Note</b>: this implementation uses exact floating-point equality (<c>==</c>).
/// For continuous floating-point vectors most elements will almost never be exactly equal;
/// use only with binarized or quantized vectors.
/// </para>
/// </summary>
public readonly struct HammingSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        var n = x.Length;
        if (n == 0) return 1f;

        int i = 0;
        float matchCount = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vMatches = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var eq = Vector.Equals(new Vector<float>(x[i..]), new Vector<float>(y[i..]));
                vMatches += Vector.ConditionalSelect(eq, Vector<float>.One, Vector<float>.Zero);
            }
            matchCount = Vector.Sum(vMatches);
        }

        for (; i < n; i++)
        {
            if (x[i] == y[i])
                matchCount++;
        }

        return matchCount / n;
    }
}
