using System.Numerics.Tensors;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 点积（内积）相似度：<c>x·y = Σ(xᵢ × yᵢ)</c>。
/// <para>
/// 值域取决于向量长度和分布。归一化向量的点积等价于余弦相似度。
/// 也用作 <see cref="DistanceMetric.Cosine"/> 预归一化模式的实际计算函数。
/// </para>
/// <para>
/// 适合最大内积搜索（MIPS）和已归一化向量的场景。
/// </para>
/// </summary>
public readonly struct DotProductSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => TensorPrimitives.Dot(x, y);
}
