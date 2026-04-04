using System.Numerics;
using System.Numerics.Tensors;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 皮尔逊相关系数：<c>r = Σ((xᵢ-μx)(yᵢ-μy)) / (√Σ(xᵢ-μx)² × √Σ(yᵢ-μy)²)</c>。值域 [-1, 1]。
/// <para>
/// 本质上是<b>去均值后的余弦相似度</b>——先将两个向量各自中心化（减去均值），
/// 再计算余弦相似度。这使得度量不受向量整体偏移的影响，仅衡量维度间的线性共变趋势。
/// </para>
/// <para>
/// <b>与余弦相似度的区别</b>：
/// <list type="bullet">
///   <item>Cosine 衡量向量方向的一致性（原点出发）</item>
///   <item>Pearson 衡量维度间变化模式的线性相关性（去均值后的方向）</item>
///   <item>当向量的各维度均值差异较大时（如不同文档长度的 TF-IDF），Pearson 更稳健</item>
/// </list>
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>文本嵌入比较（去除文档长度 / embedding 偏置的影响）</item>
///   <item>TF-IDF 或 BoW 特征的文档相似度</item>
///   <item>推荐系统中用户评分向量的相关性分析</item>
///   <item>基因表达谱、时间序列模式匹配</item>
/// </list>
/// </para>
/// </summary>
public readonly struct PearsonCorrelationSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        var n = x.Length;
        if (n == 0) return 0f;

        // Pass 1: 均值（TensorPrimitives 内部 SIMD 加速求和）
        var meanX = TensorPrimitives.Sum(x) / n;
        var meanY = TensorPrimitives.Sum(y) / n;

        // Pass 2: 去均值后的点积和 L2 范数²（数据仍在 L1 缓存中）
        int i = 0;
        float dotXY = 0f, normX2 = 0f, normY2 = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vmx = new Vector<float>(meanX);
            var vmy = new Vector<float>(meanY);
            var vDot = Vector<float>.Zero;
            var vNx2 = Vector<float>.Zero;
            var vNy2 = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;

            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var dx = new Vector<float>(x[i..]) - vmx;
                var dy = new Vector<float>(y[i..]) - vmy;
                vDot += dx * dy;
                vNx2 += dx * dx;
                vNy2 += dy * dy;
            }

            dotXY = Vector.Sum(vDot);
            normX2 = Vector.Sum(vNx2);
            normY2 = Vector.Sum(vNy2);
        }

        for (; i < n; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            dotXY += dx * dy;
            normX2 += dx * dx;
            normY2 += dy * dy;
        }

        var denom = MathF.Sqrt(normX2 * normY2);
        return denom > 0f ? dotXY / denom : 0f;
    }
}
