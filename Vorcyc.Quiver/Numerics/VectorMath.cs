using System.Numerics;

namespace Vorcyc.Quiver.Numerics;

internal static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        ValidateSameLength(x, y);

        var n = x.Length;
        var i = 0;
        var sum = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                vsum += new Vector<float>(x[i..]) * new Vector<float>(y[i..]);
            sum = Vector.Sum(vsum);
        }

        for (; i < n; i++)
            sum += x[i] * y[i];

        return sum;
    }

    public static float Sum(ReadOnlySpan<float> source)
    {
        var n = source.Length;
        var i = 0;
        var sum = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                vsum += new Vector<float>(source[i..]);
            sum = Vector.Sum(vsum);
        }

        for (; i < n; i++)
            sum += source[i];

        return sum;
    }

    public static float Norm(ReadOnlySpan<float> source)
        => MathF.Sqrt(Dot(source, source));

    public static float Distance(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        ValidateSameLength(x, y);

        var n = x.Length;
        var i = 0;
        var sum = 0f;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vsum = Vector<float>.Zero;
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
            {
                var diff = new Vector<float>(x[i..]) - new Vector<float>(y[i..]);
                vsum += diff * diff;
            }
            sum = Vector.Sum(vsum);
        }

        for (; i < n; i++)
        {
            var diff = x[i] - y[i];
            sum += diff * diff;
        }

        return MathF.Sqrt(sum);
    }

    public static float CosineSimilarity(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        ValidateSameLength(x, y);

        var dot = Dot(x, y);
        var denom = Norm(x) * Norm(y);
        return denom > 0f ? dot / denom : 0f;
    }

    public static float MaxMagnitude(ReadOnlySpan<float> source)
    {
        var max = 0f;
        foreach (var value in source)
        {
            var abs = MathF.Abs(value);
            if (abs > max)
                max = abs;
        }
        return max;
    }

    public static void Divide(ReadOnlySpan<float> source, float divisor, Span<float> destination)
    {
        if (source.Length != destination.Length)
            throw new ArgumentException("Source and destination length mismatch.");

        var n = source.Length;
        var i = 0;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vdiv = new Vector<float>(divisor);
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                (new Vector<float>(source[i..]) / vdiv).CopyTo(destination[i..]);
        }

        for (; i < n; i++)
            destination[i] = source[i] / divisor;
    }

    public static void Add(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
    {
        ValidateSameLength(x, y);
        if (x.Length != destination.Length)
            throw new ArgumentException("Source and destination length mismatch.");

        var n = x.Length;
        var i = 0;

        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var lastBlock = n - n % Vector<float>.Count;
            for (; i < lastBlock; i += Vector<float>.Count)
                (new Vector<float>(x[i..]) + new Vector<float>(y[i..])).CopyTo(destination[i..]);
        }

        for (; i < n; i++)
            destination[i] = x[i] + y[i];
    }

    private static void ValidateSameLength(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        if (x.Length != y.Length)
            throw new ArgumentException("Vector length mismatch.");
    }

    // ── Half ↔ float 转换 ──
    //
    // Half（fp16）在 .NET 中没有 SIMD 算术加速（Vector<Half> 不支持乘加等运算），
    // 因此向量在物理上以 Half 存储以节省内存/磁盘，但在相似度计算的边界 widen 到 float，
    // 复用既有的 Vector<float> SIMD 路径。逐元素 (float)Half / (Half)float 由 JIT 编译为
    // 硬件 fp16↔fp32 转换指令（x86 F16C / ARM FCVT），开销远小于点积本身，且数值完全正确
    // （含 0 / 次正规 / inf / nan）。widen/narrow 不在搜索热路径的内层循环，无需手工 SIMD。

    /// <summary>
    /// 将 <paramref name="source"/>（fp16）批量加宽为 <paramref name="destination"/>（fp32）。
    /// 两者长度必须一致。逐元素转换由 JIT 编译为硬件 fp16→fp32 指令。
    /// </summary>
    public static void WidenHalfToFloat(ReadOnlySpan<Half> source, Span<float> destination)
    {
        if (source.Length != destination.Length)
            throw new ArgumentException("Source and destination length mismatch.");

        for (var i = 0; i < source.Length; i++)
            destination[i] = (float)source[i];
    }

    /// <summary>
    /// 将 <paramref name="source"/>（fp32）批量收窄为 <paramref name="destination"/>（fp16）。
    /// 两者长度必须一致。使用 IEEE 舍入的 <c>(Half)f</c>，通常只在写入/归一化回存时调用，
    /// 不在搜索热路径上。
    /// </summary>
    public static void NarrowFloatToHalf(ReadOnlySpan<float> source, Span<Half> destination)
    {
        if (source.Length != destination.Length)
            throw new ArgumentException("Source and destination length mismatch.");

        for (var i = 0; i < source.Length; i++)
            destination[i] = (Half)source[i];
    }

    /// <summary>
    /// 便捷重载：把 <paramref name="source"/>（fp16）加宽为新分配的 <c>float[]</c>。
    /// 热路径请改用 <see cref="WidenHalfToFloat(ReadOnlySpan{Half}, Span{float})"/> 复用缓冲。
    /// </summary>
    public static float[] WidenHalfToFloat(ReadOnlySpan<Half> source)
    {
        var dst = new float[source.Length];
        WidenHalfToFloat(source, dst);
        return dst;
    }

    /// <summary>便捷重载：把 <paramref name="source"/>（fp32）收窄为新分配的 <c>Half[]</c>。</summary>
    public static Half[] NarrowFloatToHalf(ReadOnlySpan<float> source)
    {
        var dst = new Half[source.Length];
        NarrowFloatToHalf(source, dst);
        return dst;
    }
}
