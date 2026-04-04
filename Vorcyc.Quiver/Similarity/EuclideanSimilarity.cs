using System.Numerics.Tensors;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 欧几里得距离转相似度：<c>similarity = 1 / (1 + ‖x - y‖₂)</c>。值域 (0, 1]。
/// <para>
/// 距离为 0（完全相同）时相似度为 1，距离趋向无穷时相似度趋向 0。
/// 适合空间坐标、物理距离等关注绝对差异的场景。
/// </para>
/// </summary>
public readonly struct EuclideanSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
        => 1f / (1f + TensorPrimitives.Distance(x, y));
}
