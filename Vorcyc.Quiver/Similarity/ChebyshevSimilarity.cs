using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 切比雪夫距离（L∞ 范数）转相似度：<c>similarity = 1 / (1 + max|xᵢ - yᵢ|)</c>。值域 (0, 1]。
/// <para>
/// 仅关注所有维度中最大的绝对差异，忽略其他维度。
/// 等价于国际象棋中国王从一个格子移动到另一个格子所需的最少步数。
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>需要检测任意单个维度上的最大偏差</item>
///   <item>质量控制中的特征偏差检测（任一指标超标即报警）</item>
///   <item>棋盘距离、网格路径规划等离散空间问题</item>
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
