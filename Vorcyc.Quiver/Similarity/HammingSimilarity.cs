using System.Numerics;

namespace Vorcyc.Quiver.Similarity;

/// <summary>
/// 汉明相似度：<c>similarity = 1 - (不等元素数 / 总维度)</c>。值域 [0, 1]。
/// <para>
/// 比较两个向量中对应位置值不相等的元素比例。
/// 完全相同时为 1，所有元素都不同时为 0。
/// </para>
/// <para>
/// <b>适用场景</b>：
/// <list type="bullet">
///   <item>二值化向量（0/1）的位级比较</item>
///   <item>局部敏感哈希（LSH）生成的二进制哈希码比对</item>
///   <item>SimHash / MinHash 等文本指纹的快速近似匹配</item>
///   <item>量化向量（值域有限离散集）的比较</item>
/// </list>
/// </para>
/// <para>
/// <b>注意</b>：本实现使用精确浮点比较（<c>==</c>）。
/// 对于连续浮点向量，大多数元素几乎不会完全相等，建议仅用于二值化或量化后的向量。
/// </para>
/// </summary>
public readonly struct HammingSimilarity : ISimilarity<float>
{
    /// <inheritdoc />
    public static float Compute(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        var n = x.Length;
        if (n == 0) return 1f;

        int i = 0;
        float matchCount = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vMatches = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var eq = Vector.Equals(new Vector<float>(x[i..]), new Vector<float>(y[i..]));
                vMatches += Vector.ConditionalSelect(eq, Vector<float>.One, Vector<float>.Zero);
            }
            matchCount = Vector.Sum(vMatches);
        }

        for (; i < n; i++)
        {
            if (x[i] == y[i])
                matchCount++;
        }

        return matchCount / n;
    }
}
