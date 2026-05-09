namespace Vorcyc.Quiver.Storage;

// ══════════════════════════════════════════════════════════════════
// VectorBlob 段编码 / 版本契约（V4 阶段）。
//
// 旧版（"v1"）布局仍由 BinaryStorageProvider.WriteVectorBlobSegment 兼容读取：
//   [TypeName s][FieldName s][Dim i32][Count i32][HasNulls u8(0|1)]
//   [NullBitmap ⌈Count/8⌉ B (if HasNulls=1)]
//   [Floats: Count × Dim × 4 B]
//
// 扩展（"v2"）布局为 SQ8 / Matryoshka 截断引入。
// 检测方式：原先表示 HasNulls 的字节被重新解释为 flags 字节。
// 目前所有已生成文件该字节只可能是 0 或 1，因此最高位从未被使用；
// v2 通过设置 <see cref="FlagsExtended"/>（bit 7）标识。
//
//   [TypeName s][FieldName s][Dim i32][Count i32][Flags u8]
//   if (Flags &amp; FlagsExtended) != 0:
//       [u8  Version       ] // currently <see cref="HeaderVersion"/>
//       [u8  Encoding      ] // <see cref="VectorBlobEncoding"/>
//       [i32 StorageDim    ] // dimension actually stored on disk (after Matryoshka truncation)
//       [i32 EffectiveDim  ] // dimension exposed to the index/runtime (== StorageDim unless reserved for future ops)
//       [u8  NormFlags     ] // <see cref="VectorBlobNormFlags"/>
//       [u8  Reserved0     ]
//       [u8  Reserved1     ]
//       [u8  Reserved2     ]
//       [f32 QuantBias     ] // only when Encoding == SQ8, otherwise written as 0
//       [i32 QuantScaleLen ] // only when Encoding == SQ8 (per-row scale array length, currently == Count)
//       [scale payload     ] // only when Encoding == SQ8 (QuantScaleLen × f32)
//   [NullBitmap ⌈Count/8⌉ B (if Flags &amp; FlagsHasNulls)]
//   [Row payload: Count × <see cref="GetRowStride"/>(Encoding, StorageDim) B]
//
// 说明：
//   * 前置 Dim 字段保持等于 StorageDim，以兼容仍只读取旧 header 的工具（例如外部检查器）。
//   * EffectiveDim 是运行时物化和索引工作的维度，必须 ≤ QuiverVectorAttribute.Dimensions。
//   * SQ8 行布局为：[i8 × StorageDim]（逐维 int8 code）。逐行 scale 存放在上面的 QuantScale payload 中；bias 是段级共享值。
//   * Float16 行布局为：[binary16 × StorageDim]（逐维 little-endian fp16），无 QuantScale/bias；用于声明为 Half[] 的字段，磁盘相比 Float32 减半。
//   * null 行仍占用完整 stride，以保持 mmap 行距恒定。
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Per-row vector encoding used inside vector payload segments.
/// </summary>
public enum VectorBlobEncoding : byte
{
    /// <summary>Raw little-endian <c>float32</c> values; stride = <c>Dim × 4</c>.</summary>
    Float32 = 0,

    /// <summary>Signed scalar 8-bit quantization; stride = <c>Dim × 1</c> codes, with additional per-row scale and segment-level bias.</summary>
    Sq8 = 1,

    /// <summary>
    /// Raw little-endian IEEE-754 <c>binary16</c> (fp16 / <see cref="System.Half"/>) values; stride = <c>Dim × 2</c>.
    /// <para>
    /// Unlike <see cref="Sq8"/>, this is lossless per-element half-precision storage that halves disk and memory
    /// usage compared to <see cref="Float32"/>; at search time values are widened to <c>float</c> to reuse the
    /// existing SIMD path. Used only for vector fields declared as <c>Half[]</c>.
    /// </para>
    /// </summary>
    Float16 = 2,
}

/// <summary>Flag bits packed into the legacy <c>HasNulls</c> byte of the vector segment header.</summary>
[Flags]
public enum VectorBlobFlags : byte
{
    /// <summary>No additional flags.</summary>
    None = 0,

    /// <summary>This segment carries a per-row null bitmap (legacy bit 0).</summary>
    HasNulls = 1 << 0,

    /// <summary>An extended v2 header follows immediately after the legacy header byte.</summary>
    Extended = 1 << 7,
}

/// <summary>Indicates whether the stored vectors were normalized at write time.</summary>
[Flags]
public enum VectorBlobNormFlags : byte
{
    /// <summary>No normalization flags.</summary>
    None = 0,

    /// <summary>Every non-null row was L2-normalized before being written.</summary>
    L2Normalized = 1 << 0,
}

/// <summary>描述扩展 <c>VectorBlob</c> header 布局的常量与辅助方法。</summary>
internal static class VectorBlobFormat
{
    /// <summary>当前扩展 header 版本；扩展布局发生破坏性变化时递增。</summary>
    public const byte HeaderVersion = 1;

    /// <summary><see cref="VectorBlobFlags.Extended"/> 的便捷别名。</summary>
    public const byte FlagsExtended = (byte)VectorBlobFlags.Extended;

    /// <summary><see cref="VectorBlobFlags.HasNulls"/> 的便捷别名。</summary>
    public const byte FlagsHasNulls = (byte)VectorBlobFlags.HasNulls;

    /// <summary>
    /// 根据编码和存储维度返回磁盘上一行向量占用的字节数。
    /// </summary>
    public static int GetRowStride(VectorBlobEncoding encoding, int storageDim) => encoding switch
    {
        VectorBlobEncoding.Float32 => storageDim * sizeof(float),
        VectorBlobEncoding.Sq8 => storageDim, // 每个维度一个有符号字节。
        VectorBlobEncoding.Float16 => storageDim * 2, // 每个维度一个 IEEE binary16（2 字节）。
        _ => throw new InvalidDataException($"Unknown VectorBlob encoding: {(byte)encoding}."),
    };
}
