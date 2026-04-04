using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 广义 Jaccard 相似度：<c>similarity = Σ min(xᵢ, yᵢ) / Σ max(xᵢ, yᵢ)</c>。值域 [0, 1]。
/// <para>
/// 经典 Jaccard 系数的连续值扩展（也称 Ruzicka 相似度）。
/// 当向量为二值（0/1）时退化为标准的集合 Jaccard 系数 <c>|A∩B| / |A∪B|</c>。
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>BoW（词袋）/ TF-IDF 等稀疏非负文本特征的文档相似度</item>
///   <item>二值化特征向量的集合重叠度量</item>
///   <item>生态学中物种丰度数据的群落相似度</item>
///   <item>直方图特征（颜色直方图、梯度直方图）的比较</item>
/// </list>
/// </para>
/// <para>
/// <b>注意</b>：要求向量元素为非负值。负值元素的 min/max 行为可能产生非预期结果。
/// </para>
/// </summary>
public readonly struct JaccardSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int i = 0;
        float sumMin = 0f, sumMax = 0f;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var vMin = Vector<float>.Zero;
            var vMax = Vector<float>.Zero;
            var lastBlock = x.Length - x.Length % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var vx = new Vector<float>(x[i..]);
                var vy = new Vector<float>(y[i..]);
                vMin += Vector.Min(vx, vy);
                vMax += Vector.Max(vx, vy);
            }
            sumMin = Vector.Sum(vMin);
            sumMax = Vector.Sum(vMax);
        }

        for (; i < x.Length; i++)
        {
            sumMin += MathF.Min(x[i], y[i]);
            sumMax += MathF.Max(x[i], y[i]);
        }

        return sumMax > 0f ? sumMin / sumMax : 1f;
    }
}
