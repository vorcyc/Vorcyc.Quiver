using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 堪培拉距离转相似度：<c>similarity = 1 - (1/n) × Σ |xᵢ-yᵢ| / (|xᵢ| + |yᵢ|)</c>。值域 [0, 1]。
/// <para>
/// 一种加权的 L1 距离——每个维度的差异都按该维度的量级归一化。
/// 对接近零的值非常敏感（分母小时权重大），特别适合稀疏数据。
/// 当 <c>xᵢ = yᵢ = 0</c> 时该维度贡献为 0（完全匹配）。
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>稀疏文本特征（TF-IDF、BoW）的文档距离——稀疏维度的差异不被密集维度掩盖</item>
///   <item>化学指纹、生态计数数据的比较</item>
///   <item>数据分布差异较大时（不同量级的特征混合）的鲁棒度量</item>
/// </list>
/// </para>
/// </summary>
public readonly struct CanberraSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        var n = x.Length;
        if (n == 0) return 1f;

        int i = 0;
        float sum = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var vx = new Vector<float>(x[i..]);
                var vy = new Vector<float>(y[i..]);
                var absDiff = Vector.Abs(vx - vy);
                var absDenom = Vector.Abs(vx) + Vector.Abs(vy);
                // 分母为零时用 1 替代（此时分子也为零，商为 0，语义正确）
                var safeDenom = Vector.ConditionalSelect(
                    Vector.GreaterThan(absDenom, Vector<float>.Zero), absDenom, Vector<float>.One);
                vsum += absDiff / safeDenom;
            }
            sum = Vector.Sum(vsum);
        }

        for (; i < n; i++)
        {
            var absX = MathF.Abs(x[i]);
            var absY = MathF.Abs(y[i]);
            var denom = absX + absY;
            if (denom > 0f)
                sum += MathF.Abs(x[i] - y[i]) / denom;
        }

        return 1f - sum / n;
    }
}
