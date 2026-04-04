using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 向量相似度计算的静态抽象接口。
/// <para>
/// 通过 <c>static abstract</c> + <see langword="struct"/> 实现，JIT 对每个具体类型生成特化机器码，
/// <c>TSim.Compute()</c> 在调用站点被直接内联，无虚分派、无委托间接调用。
/// </para>
/// <para>
/// 泛型参数 <typeparamref name="T"/> 支持 <see cref="float"/>、<see cref="double"/>、<see cref="Half"/>
/// 等满足约束的数值类型，配合 <see cref="System.Numerics.Tensors.TensorPrimitives"/> 的泛型重载实现多精度计算。
/// </para>
/// </summary>
/// <typeparam name="T">
/// 标量元素类型。约束为 <see langword="unmanaged"/>（值类型，可用于 Span 和指针操作）、
/// <see cref="INumber{TSelf}"/>（四则运算和比较）、<see cref="IRootFunctions{TSelf}"/>（平方根，Euclidean 需要）。
/// </typeparam>
/// <example>
/// <b>实现自定义度量</b>：
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
/// <b>在实体上使用</b>：
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
    /// 计算两个等长向量的相似度。
    /// </summary>
    /// <param name="x">第一个向量。</param>
    /// <param name="y">第二个向量，维度须与 <paramref name="x"/> 一致。</param>
    /// <returns>
    /// 相似度分数，值越大越相似。
    /// 距离型度量须转换为相似度（如 <c>1 / (1 + distance)</c>），确保排序方向统一。
    /// </returns>
    static abstract T Compute(ReadOnlySpan<T> x, ReadOnlySpan<T> y);
}
