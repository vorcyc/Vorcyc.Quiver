using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 曼哈顿距离（L1 范数）转相似度：<c>similarity = 1 / (1 + Σ|xᵢ - yᵢ|)</c>。值域 (0, 1]。
/// <para>
/// 也称"城市街区距离"，计算沿各坐标轴的绝对差之和。
/// 相比欧几里得距离对离群维度不那么敏感（不平方放大差异）。
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>稀疏特征向量（大多数维度为 0，少数维度有值）</item>
///   <item>高维空间中需要对异常维度更鲁棒的度量</item>
///   <item>推荐系统中的用户偏好向量比较</item>
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
