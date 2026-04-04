using System.Numerics.Tensors;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 余弦相似度：<c>cos(θ) = (x·y) / (‖x‖ × ‖y‖)</c>。值域 [-1, 1]。
/// <para>
/// 衡量向量方向的一致性，不关心向量的绝对长度。
/// 适合文本嵌入、语义搜索等场景。
/// </para>
/// <para>
/// <b>注意</b>：当 <see cref="DistanceMetric.Cosine"/> 启用预归一化时，
/// 实际使用的是 <see cref="DotProductSimilarity"/>（归一化后 Dot = Cosine，省去除法）。
/// 本类型仅用于非预归一化场景或用户显式指定。
/// </para>
/// </summary>
public readonly struct CosineSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => TensorPrimitives.CosineSimilarity(x, y);
}
