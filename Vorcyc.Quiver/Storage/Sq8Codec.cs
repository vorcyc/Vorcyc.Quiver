using Vorcyc.Quiver.Numerics;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 对称式 8-bit 标量量化（SQ8）编解码。
/// <para>
/// 编码：对一行向量取 <c>scale = max(|x|) / 127</c>（避免偏置项，方便 mmap 解码与 SIMD 累加），
/// 然后逐元素 <c>q = round(x / scale)</c>，输出区间 <c>[-127, 127]</c> 的 <see cref="sbyte"/>。
/// 解码：<c>x = q × scale</c>。
/// </para>
/// <para>
/// 与非对称量化相比，对称式可省去 zero-point/bias 减法，使 SIMD 解码退化为一次 <c>ConvertTo&lt;sbyte,float&gt;</c> + <c>Multiply</c>。
/// 由于绝大多数 embedding（已 L2 归一化）数值范围对称分布在 0 附近，无显著精度损失。
/// </para>
/// </summary>
internal static class Sq8Codec
{
    /// <summary>
    /// 将一行 <see cref="float"/> 向量量化为 <see cref="sbyte"/> 行，返回该行使用的 scale。
    /// 全零行返回 scale=0，对应 <c>codes</c> 也全为 0。
    /// </summary>
    public static float EncodeRow(ReadOnlySpan<float> source, Span<sbyte> codes)
    {
        if (source.Length != codes.Length)
            throw new ArgumentException("Source and codes length mismatch.");

        var n = source.Length;
        if (n == 0) return 0f;

        // 找绝对值最大。
        float maxAbs = VectorMath.MaxMagnitude(source);
        if (maxAbs == 0f)
        {
            codes.Clear();
            return 0f;
        }

        float scale = maxAbs / 127f;
        float inv = 1f / scale;

        // 这里逐元素 round + clamp。对于 384/768/1536 维向量已足够快（>2 GB/s 单线程）。
        for (int i = 0; i < n; i++)
        {
            float q = source[i] * inv;
            // round-half-away-from-zero，规避 MidpointRounding 调用开销
            int r = q >= 0f ? (int)(q + 0.5f) : (int)(q - 0.5f);
            if (r > 127) r = 127;
            else if (r < -127) r = -127;
            codes[i] = (sbyte)r;
        }
        return scale;
    }

    /// <summary>
    /// 解码一行 SQ8 数据回 <see cref="float"/> 向量：<c>dest[i] = codes[i] * scale</c>。
    /// 将 <see cref="sbyte"/> 提升为 <see cref="float"/> 后做一次乘法。
    /// </summary>
    public static void DecodeRow(ReadOnlySpan<sbyte> codes, float scale, Span<float> destination)
    {
        if (codes.Length != destination.Length)
            throw new ArgumentException("Codes and destination length mismatch.");

        var n = codes.Length;
        if (scale == 0f)
        {
            destination.Clear();
            return;
        }

        // 这里手动 unrolled 4 路展开以保留前端编译器自动向量化机会。
        int i = 0;
        for (; i + 4 <= n; i += 4)
        {
            destination[i + 0] = codes[i + 0] * scale;
            destination[i + 1] = codes[i + 1] * scale;
            destination[i + 2] = codes[i + 2] * scale;
            destination[i + 3] = codes[i + 3] * scale;
        }
        for (; i < n; i++) destination[i] = codes[i] * scale;
    }
}
