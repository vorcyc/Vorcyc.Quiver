using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// Static abstract interface for vector similarity computation.
/// <para>
/// Implemented via <c>static abstract</c> + <see langword="struct"/>: the JIT emits specialized
/// machine code for each concrete type, and <c>TSim.Compute()</c> is inlined directly at the call
/// site — no virtual dispatch, no delegate indirection.
/// </para>
/// <para>
/// The generic parameter <typeparamref name="T"/> supports <see cref="float"/>, <see cref="double"/>,
/// <see cref="Half"/>, and any other numeric type that satisfies the constraints.
/// </para>
/// </summary>
/// <typeparam name="T">
/// Scalar element type. Constrained to <see langword="unmanaged"/> (value type, usable with
/// <c>Span</c> and pointers), <see cref="INumber{TSelf}"/> (arithmetic and comparison), and
/// <see cref="IRootFunctions{TSelf}"/> (square root, required by Euclidean distance).
/// </typeparam>
/// <example>
/// <b>Implementing a custom metric</b>:
/// <code>
/// public readonly struct ManhattanSimilarity : ISimilarity&lt;float&gt;
/// {
///     public static float Compute(ReadOnlySpan&lt;float&gt; x, ReadOnlySpan&lt;float&gt; y)
///     {
///         float sum = 0;
///         for (int i = 0; i &lt; x.Length; i++)
///             sum += MathF.Abs(x[i] - y[i]);
///         return 1f / (1f + sum);
///     }
/// }
/// </code>
/// <b>Using it on an entity</b>:
/// <code>
/// [QuiverVector(128, CustomSimilarity = typeof(ManhattanSimilarity))]
/// public float[] Embedding { get; set; }
/// </code>
/// </example>
/// <seealso cref="CosineSimilarity"/>
/// <seealso cref="DotProductSimilarity"/>
/// <seealso cref="EuclideanSimilarity"/>
/// <seealso cref="ManhattanSimilarity"/>
/// <seealso cref="ChebyshevSimilarity"/>
/// <seealso cref="PearsonCorrelationSimilarity"/>
/// <seealso cref="HammingSimilarity"/>
/// <seealso cref="JaccardSimilarity"/>
/// <seealso cref="CanberraSimilarity"/>
public interface ISimilarity<T> where T : unmanaged, INumber<T>, IRootFunctions<T>
{
    /// <summary>
    /// Computes the similarity between two equal-length vectors.
    /// </summary>
    /// <param name="x">The first vector.</param>
    /// <param name="y">The second vector. Must have the same length as <paramref name="x"/>.</param>
    /// <returns>
    /// A similarity score where higher values indicate greater similarity.
    /// Distance-based metrics must be converted to similarity (e.g. <c>1 / (1 + distance)</c>)
    /// to ensure a consistent sort order.
    /// </returns>
    static abstract T Compute(ReadOnlySpan<T> x, ReadOnlySpan<T> y);
}
