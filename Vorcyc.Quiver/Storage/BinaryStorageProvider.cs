using System.Reflection;
using System.Runtime.InteropServices;
using Vorcyc.Quiver.Migration;
using Vorcyc.Quiver.Payloads;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 二进制格式的存储提供者，实现 <see cref="IStorageProvider"/> 接口。
/// <para>
/// 采用自定义紧凑二进制协议；自 v4.0 起升级为 Segment + Footer 模型，
/// 支持段级 CRC32、增量追加（<c>AppendAsync</c>）、零反序列化合并（<c>QuiverDbFile.MergeAsync</c>）。
/// </para>
/// <para>
/// 文件布局（v4 / <c>QDB\x04</c>）：
/// <code>
/// [Magic "QDB\x04" 4B]
/// [HeaderLen u32][Header bytes]                ← 预留扩展，当前为空
/// [Segment 1] [Segment 2] ... [Segment N]
///   ┗ 每个 Segment 内部：[TypeName] [PropCount] [Descriptors] [EntityCount] [Entities...]
/// [FooterTopMagic "QDBF" 4B]
/// [SegmentCount u32]
/// for each: [TypeName] [Offset u64] [Length u64] [EntityCount u32] [CRC32 u32]
/// [FooterOffset u64]
/// [TrailerMagic "QDBE" 4B]                     ← 末尾 12 字节固定，反向定位
/// </code>
/// </para>
/// <para>
/// 兼容性：读取入口按前 4 字节 magic 分发，v1/v2/v3 走旧的纯顺序流路径。
/// </para>
/// </summary>
/// <seealso cref="IStorageProvider"/>
internal class BinaryStorageProvider : IStorageProvider
{
    /// <summary>当前写入版本：v4 / <c>QDB\x04</c>。</summary>
    private static readonly byte[] Magic   = "QDB\x04"u8.ToArray();

    /// <summary>v3 magic（兼容读取）。</summary>
    private static readonly byte[] MagicV3 = "QDB\x03"u8.ToArray();

    /// <summary>v2 magic（兼容读取）。</summary>
    private static readonly byte[] MagicV2 = "QDB\x02"u8.ToArray();

    /// <summary>v1 magic（兼容读取）。</summary>
    private static readonly byte[] MagicV1 = "QDB\x01"u8.ToArray();

    /// <summary>v4 footer 起始 magic（schema v1，位于 FooterOffset 处）。</summary>
    private static readonly byte[] FooterTopMagic = "QDBF"u8.ToArray();

    /// <summary>v4 footer 起始 magic（schema v2，拆分段后的新格式）。</summary>
    private static readonly byte[] FooterTopMagicV2 = "QDB2"u8.ToArray();

    /// <summary>v4 文件末尾 4 字节 magic（位于文件最后 4 字节）。</summary>
    private static readonly byte[] TrailerMagic = "QDBE"u8.ToArray();

    /// <summary>v4 文件末尾固定字节数：<c>[FooterOffset u64][TrailerMagic 4B]</c>。</summary>
    private const int TrailerSize = 12;

    /// <summary>
    /// 段的种类（v4 footer schema v2 起使用）。
    /// </summary>
    internal enum SegmentKind : byte
    {
        /// <summary>
        /// 旧式段：实体元数据 + 全部属性（含向量、blob）都写在同一个段内，自描述。
        /// 兼容路径：写入端可选；读取端按原 <see cref="ReadSegment"/> 解析。
        /// </summary>
        Mixed = 0,
        /// <summary>实体元数据段：只包含非 [QuiverVector] / [QuiverLargeField] 的字段。</summary>
        EntityMeta = 1,
        /// <summary>向量 blob 段：单类型 + 单字段的紧凑 <c>float[]</c> 拼接，4 KB 对齐。</summary>
        VectorBlob = 2,
        /// <summary>大字段 blob 段：单类型 + 单字段，[Offsets u64[]] + [Bytes]。</summary>
        Blob = 3,
        /// <summary>tombstone bitmap 段：单类型，已删除 id 的位图。</summary>
        Tombstone = 4,
        /// <summary>
        /// 索引快照段：保存某个 (类型, 向量字段) 的搜索索引内部结构（当前仅 HNSW），
        /// 让 LoadAsync 跳过 O(N log N) 的图重建，直接反序列化。
        /// 不带快照的旧文件 / 不支持快照的索引类型继续走在线重建路径。
        /// </summary>
        IndexSnapshot = 5,
    }

    #region 类型码

    /// <summary>
    /// 属性类型码枚举，用于在二进制流中标识属性值的 CLR 类型。
    /// <para>
    /// 写入时将属性的 <see cref="Type"/> 映射为单字节类型码，
    /// 读取时根据类型码决定如何反序列化对应字节。
    /// </para>
    /// </summary>
    private enum TypeCode : byte
    {
        /// <summary>字符串，使用 <see cref="BinaryWriter.Write(string)"/> 的长度前缀格式。</summary>
        String = 0,
        /// <summary>32 位有符号整数（4 字节）。</summary>
        Int32 = 1,
        /// <summary>64 位有符号整数（8 字节）。</summary>
        Int64 = 2,
        /// <summary>单精度浮点数（4 字节）。</summary>
        Single = 3,
        /// <summary>双精度浮点数（8 字节）。</summary>
        Double = 4,
        /// <summary>布尔值（1 字节）。</summary>
        Boolean = 5,
        /// <summary>日期时间，使用 <see cref="DateTime.ToBinary"/> 转为 Int64 存储。</summary>
        DateTime = 6,
        /// <summary>全局唯一标识符（16 字节）。</summary>
        Guid = 7,
        /// <summary>十进制数（16 字节）。</summary>
        Decimal = 8,
        /// <summary>浮点数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        FloatArray = 9,
        /// <summary>字符串数组，使用 [长度 int32] + [逐元素字符串] 写入。</summary>
        StringArray = 10,
        /// <summary>无符号单字节整数（1 字节）。</summary>
        Byte = 11,
        /// <summary>16 位有符号整数（2 字节）。</summary>
        Int16 = 12,
        /// <summary>半精度浮点数（2 字节），ML/AI 向量场景常用。</summary>
        Half = 13,
        /// <summary>带时区偏移的日期时间，使用 [Ticks int64] + [OffsetMinutes int16] 存储（10 字节）。</summary>
        DateTimeOffset = 14,
        /// <summary>时间间隔，使用 <see cref="System.TimeSpan.Ticks"/> 转为 Int64 存储（8 字节）。</summary>
        TimeSpan = 15,
        /// <summary>字节数组，使用 [长度 int32] + [原始字节] 写入。</summary>
        ByteArray = 16,
        /// <summary>双精度浮点数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        DoubleArray = 17,
        /// <summary>16 位无符号整数（2 字节）。</summary>
        UInt16 = 18,
        /// <summary>32 位无符号整数（4 字节）。</summary>
        UInt32 = 19,
        /// <summary>64 位无符号整数（8 字节）。</summary>
        UInt64 = 20,
        /// <summary>有符号单字节整数（1 字节）。</summary>
        SByte = 21,
        /// <summary>Unicode 字符，以 16 位无符号整数（2 字节）存储。</summary>
        Char = 22,
        /// <summary>仅日期（无时间），使用 <see cref="System.DateOnly.DayNumber"/> 转为 Int32 存储（4 字节）。</summary>
        DateOnly = 23,
        /// <summary>仅时间（无日期），使用 <see cref="System.TimeOnly.Ticks"/> 转为 Int64 存储（8 字节）。</summary>
        TimeOnly = 24,
        /// <summary>16 位无符号整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        UInt16Array = 25,
        /// <summary>32 位无符号整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        UInt32Array = 26,
        /// <summary>64 位无符号整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        UInt64Array = 27,
        /// <summary>有符号单字节整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        SByteArray = 28,
        // 29 保留：曾用于 CharArray，已移除（char[] 语义与 String 重叠，应使用 string）。
        /// <summary>仅日期数组，使用 [长度 int32] + [逐元素 DayNumber int32] 写入。</summary>
        DateOnlyArray = 30,
        /// <summary>仅时间数组，使用 [长度 int32] + [逐元素 Ticks int64] 写入。</summary>
        TimeOnlyArray = 31,
        /// <summary>16 位有符号整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        Int16Array = 32,
        /// <summary>32 位有符号整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        Int32Array = 33,
        /// <summary>64 位有符号整数数组，使用 [长度 int32] + [原始字节] 零拷贝写入。</summary>
        Int64Array = 34,
        /// <summary>布尔数组，使用 [长度 int32] + [每元素 1 字节] 写入。</summary>
        BooleanArray = 35,
        /// <summary>半精度浮点数组，使用 [长度 int32] + [原始字节（每元素 2 字节）] 零拷贝写入。</summary>
        HalfArray = 36,
    }

    /// <summary>
    /// 将 CLR <see cref="Type"/> 映射到对应的 <see cref="TypeCode"/>。
    /// </summary>
    /// <param name="type">要映射的属性类型。</param>
    /// <param name="entityTypeName">所属实体类型名，用于在不支持时给出友好错误信息（可选）。</param>
    /// <param name="propertyName">所属属性名，用于在不支持时给出友好错误信息（可选）。</param>
    /// <returns>对应的二进制类型码。</returns>
    /// <exception cref="NotSupportedException">当传入的类型不在支持列表中时抛出，错误信息包含实体、属性及受支持类型清单。</exception>
    private static TypeCode GetTypeCode(Type type, string? entityTypeName = null, string? propertyName = null) => type switch
    {
        _ when type == typeof(string) => TypeCode.String,
        _ when type == typeof(int) => TypeCode.Int32,
        _ when type == typeof(long) => TypeCode.Int64,
        _ when type == typeof(float) => TypeCode.Single,
        _ when type == typeof(double) => TypeCode.Double,
        _ when type == typeof(bool) => TypeCode.Boolean,
        _ when type == typeof(DateTime) => TypeCode.DateTime,
        _ when type == typeof(Guid) => TypeCode.Guid,
        _ when type == typeof(decimal) => TypeCode.Decimal,
        _ when type == typeof(float[]) => TypeCode.FloatArray,
        _ when type == typeof(string[]) => TypeCode.StringArray,
        _ when type == typeof(byte) => TypeCode.Byte,
        _ when type == typeof(short) => TypeCode.Int16,
        _ when type == typeof(Half) => TypeCode.Half,
        _ when type == typeof(DateTimeOffset) => TypeCode.DateTimeOffset,
        _ when type == typeof(TimeSpan) => TypeCode.TimeSpan,
        _ when type == typeof(byte[]) => TypeCode.ByteArray,
        _ when type == typeof(double[]) => TypeCode.DoubleArray,
        _ when type == typeof(ushort) => TypeCode.UInt16,
        _ when type == typeof(uint) => TypeCode.UInt32,
        _ when type == typeof(ulong) => TypeCode.UInt64,
        _ when type == typeof(sbyte) => TypeCode.SByte,
        _ when type == typeof(char) => TypeCode.Char,
        _ when type == typeof(DateOnly) => TypeCode.DateOnly,
        _ when type == typeof(TimeOnly) => TypeCode.TimeOnly,
        _ when type == typeof(ushort[]) => TypeCode.UInt16Array,
        _ when type == typeof(uint[]) => TypeCode.UInt32Array,
        _ when type == typeof(ulong[]) => TypeCode.UInt64Array,
        _ when type == typeof(sbyte[]) => TypeCode.SByteArray,
        _ when type == typeof(DateOnly[]) => TypeCode.DateOnlyArray,
        _ when type == typeof(TimeOnly[]) => TypeCode.TimeOnlyArray,
        _ when type == typeof(short[]) => TypeCode.Int16Array,
        _ when type == typeof(int[]) => TypeCode.Int32Array,
        _ when type == typeof(long[]) => TypeCode.Int64Array,
        _ when type == typeof(bool[]) => TypeCode.BooleanArray,
        _ when type == typeof(Half[]) => TypeCode.HalfArray,
        _ => throw BuildUnsupportedTypeException(type, entityTypeName, propertyName)
    };

    /// <summary>
    /// 受 <see cref="BinaryStorageProvider"/> 二进制序列化支持的全部 CLR 类型清单（用于错误提示）。
    /// 与 <see cref="GetTypeCode"/> 的映射保持一致。
    /// </summary>
    private static readonly string[] SupportedTypeNames =
    [
        // 标量
        "string", "bool", "char", "byte", "sbyte",
        "short", "ushort", "int", "uint", "long", "ulong",
        "float", "double", "decimal", "System.Half",
        "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
        "System.DateOnly", "System.TimeOnly", "System.Guid",
        // 数组
        "string[]", "bool[]", "byte[]", "sbyte[]",
        "short[]", "ushort[]", "int[]", "uint[]", "long[]", "ulong[]",
        "float[]", "double[]", "System.Half[]",
        "System.DateOnly[]", "System.TimeOnly[]",
    ];

    /// <summary>
    /// 构造类型不受支持时的友好异常，包含实体类型、属性名、不支持的 CLR 类型全名，
    /// 以及当前受支持类型清单，便于用户快速定位与修正实体定义。
    /// </summary>
    private static NotSupportedException BuildUnsupportedTypeException(
        Type type, string? entityTypeName, string? propertyName)
    {
        var where = entityTypeName is not null && propertyName is not null
            ? $"property '{entityTypeName}.{propertyName}' of type '{type.FullName ?? type.Name}'"
            : $"type '{type.FullName ?? type.Name}'";

        var message =
            $"Quiver binary serialization does not support {where}. " +
            $"Supported types are: {string.Join(", ", SupportedTypeNames)}. " +
            $"Consider changing the property to a supported type, or excluding it from the entity. " +
            $"(Note: nullable value types such as 'int?', generic collections such as 'List<T>', " +
            $"and custom/complex types are not supported.)";

        return new NotSupportedException(message);
    }

    #endregion

    #region Save (v4)

    /// <summary>
    /// 将所有向量集合以 v4 (Segment + Footer) 二进制格式异步持久化到指定文件。
    /// </summary>
    /// <param name="filePath">目标文件的绝对或相对路径。文件不存在时自动创建，已存在时覆盖。</param>
    /// <param name="sets">
    /// 要保存的向量集合字典。键为类型名称，值为元组 <c>(Type, List&lt;object&gt;)</c>，
    /// 包含实体的 CLR 类型及实体列表。
    /// </param>
    /// <returns>表示异步写入操作的任务。</returns>
    public async Task SaveAsync(string filePath, IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets)
        => await SaveAsync(filePath, sets, snapshotWriters: null);

    /// <summary>
    /// <inheritdoc cref="SaveAsync(string, IReadOnlyDictionary{string, ValueTuple{Type, List{object}}})" />
    /// <para>
    /// 可选 <paramref name="snapshotWriters"/>：每个 (TypeName → list of (FieldName, NodeCount, WriteCallback))
    /// 描述了该类型在常规段写完后还要追加的 <see cref="SegmentKind.IndexSnapshot"/> 段。WriteCallback 返回
    /// <see langword="false"/> 或写入失败时整段被回滚（不进入 footer），不影响主数据。
    /// </para>
    /// </summary>
    public async Task SaveAsync(
        string filePath,
        IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets,
        IReadOnlyDictionary<string, IReadOnlyList<(string FieldName, int NodeCount, Func<BinaryWriter, bool> Writer)>>? snapshotWriters)
        => await SaveAsync(filePath, sets, snapshotWriters, largeFieldSlices: null);

    public async Task SaveAsync(
        string filePath,
        IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets,
        IReadOnlyDictionary<string, IReadOnlyList<(string FieldName, int NodeCount, Func<BinaryWriter, bool> Writer)>>? snapshotWriters,
        IReadOnlyDictionary<string, Vorcyc.Quiver.Payloads.ILargeFieldSliceSource>? largeFieldSlices)
    {
        await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            WriteFileHeader(fs);

            var footerEntries = new List<FooterEntry>(sets.Count);
            foreach (var (typeName, (type, entities)) in sets)
            {
                Vorcyc.Quiver.Payloads.ILargeFieldSliceSource? sliceSource = null;
                largeFieldSlices?.TryGetValue(typeName, out sliceSource);
                WriteSegmentSet(fs, typeName, type, entities, footerEntries, sliceSource);

                if (snapshotWriters is not null
                    && snapshotWriters.TryGetValue(typeName, out var writers)
                    && writers is { Count: > 0 })
                {
                    foreach (var (fieldName, nodeCount, writer) in writers)
                    {
                        var entry = WriteIndexSnapshotSegment(fs, typeName, fieldName, nodeCount, writer);
                        if (entry is { } e) footerEntries.Add(e);
                    }
                }
            }

            WriteFooter(fs, footerEntries);
            // 把 OS 缓存中的字节刷到物理介质，避免 crash-after-rename 留下半截 .tmp。
            fs.Flush(flushToDisk: true);
        });
    }

    /// <summary>追加新段到现有 v4 文件，并重写 footer。仅适用于 <c>QDB\x04</c> 文件。</summary>
    /// <param name="filePath">现有 v4 文件路径；若不存在则创建一个空的 v4 文件再追加。</param>
    /// <param name="sets">要追加的实体集合（与 <see cref="SaveAsync"/> 同形）。</param>
    /// <param name="tombstones">每个类型本次需要追加 tombstone 的 internal id 列表（可为 null/空）。</param>
    public async Task AppendAsync(
        string filePath,
        IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets,
        IReadOnlyDictionary<string, int[]>? tombstones = null)
    {
        await Task.Run(() =>
        {
            List<FooterEntry> footerEntries;
            long appendOffset;

            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                using var initFs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                WriteFileHeader(initFs);
                footerEntries = new List<FooterEntry>();
                appendOffset = initFs.Position;
            }
            else
            {
                using var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                var version = DetectVersion(probe);
                if (version != 4)
                    throw new InvalidDataException("AppendAsync 仅支持 v4 (QDB\\x04) 文件。请先用 SaveAsync 重写为 v4。");
                var (entries, footerOffset) = ReadFooter(probe);
                footerEntries = entries.ToList();
                appendOffset = footerOffset;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536);
            fs.SetLength(appendOffset);
            fs.Position = appendOffset;

            foreach (var (typeName, (type, entities)) in sets)
                WriteSegmentSet(fs, typeName, type, entities, footerEntries);

            if (tombstones is not null)
            {
                foreach (var (typeName, deadIds) in tombstones)
                {
                    if (deadIds is null || deadIds.Length == 0) continue;
                    footerEntries.Add(WriteTombstoneSegment(fs, typeName, deadIds));
                }
            }

            WriteFooter(fs, footerEntries);
            fs.Flush(flushToDisk: true);
        });
    }

    /// <summary>写入文件头：Magic + HeaderLen(=0) + Header(空)。</summary>
    private static void WriteFileHeader(Stream fs)
    {
        fs.Write(Magic);
        Span<byte> headerLen = stackalloc byte[4];
        BitConverter.TryWriteBytes(headerLen, 0);
        fs.Write(headerLen);
    }

    /// <summary>
    /// 写入单个集合的全部段（一个 EntityMeta + 零或多个 VectorBlob），并把对应 footer 条目追加到 <paramref name="footerEntries"/>。
    /// <para>
    /// P1.2 起，向量字段（<see cref="QuiverVectorAttribute"/> 标记的 <c>float[]</c>）从实体元数据中物理拆出，
    /// 单独存为 <see cref="SegmentKind.VectorBlob"/> 段；未标记 <c>[QuiverVector]</c> 的普通字段（含普通 <c>float[]</c>/<c>byte[]</c>）
    /// 仍写在 <see cref="SegmentKind.EntityMeta"/> 段内。
    /// </para>
    /// </summary>
    /// <param name="fs">目标输出流。</param>
    /// <param name="typeName">实体类型完全限定名。</param>
    /// <param name="type">实体 CLR 类型。</param>
    /// <param name="entities">待写入实体列表（按现有顺序，行号即段内 index）。</param>
    /// <param name="footerEntries">用于追加新生成的 footer 条目。</param>
    internal static void WriteSegmentSet(Stream fs, string typeName, Type type, List<object> entities, List<FooterEntry> footerEntries, Vorcyc.Quiver.Payloads.ILargeFieldSliceSource? largeFieldSlices = null)
    {
        var (metaProps, vectorProps, blobProps) = ClassifyProperties(type);

        footerEntries.Add(WriteEntityMetaSegment(fs, typeName, metaProps, entities));

        foreach (var vp in vectorProps)
            footerEntries.Add(WriteVectorBlobSegment(fs, PayloadPipeline.CreateVectorDescriptor(typeName, type, vp), entities));

        foreach (var bp in blobProps)
            footerEntries.Add(WriteBlobSegment(fs, PayloadPipeline.CreateLargeFieldDescriptor(typeName, type, bp), entities, largeFieldSlices));
    }

    /// <summary>把实体类型属性拆为 (metaProps, vectorProps, blobProps)。各组内按名称排序。</summary>
    private static (PropertyInfo[] Meta, PropertyInfo[] Vector, PropertyInfo[] Blob) ClassifyProperties(Type type)
    {
        var all = GetSortedProperties(type);
        var meta = new List<PropertyInfo>(all.Length);
        var vec = new List<PropertyInfo>(2);
        var blob = new List<PropertyInfo>(2);
        foreach (var p in all)
        {
            if (p.PropertyType == typeof(float[]) && p.GetCustomAttribute<QuiverVectorAttribute>() != null)
                vec.Add(p);
            else if (p.PropertyType == typeof(byte[]) && p.GetCustomAttribute<QuiverLargeFieldAttribute>() != null)
                blob.Add(p);
            else
                meta.Add(p);
        }
        return (meta.ToArray(), vec.ToArray(), blob.ToArray());
    }

    /// <summary>Public 入口：合并工具的 LWW/FWW 路径用，会写出完整段集合并返回（顺序：EntityMeta 在前，VectorBlob 紧随）。</summary>
    internal static List<FooterEntry> WriteSegmentPublic(Stream fs, string typeName, Type type, List<object> entities)
    {
        var list = new List<FooterEntry>(2);
        WriteSegmentSet(fs, typeName, type, entities, list);
        return list;
    }

    /// <summary>写入 EntityMeta 段。布局与旧 Mixed 段相同，仅 props 不含 vector 字段。</summary>
    private static FooterEntry WriteEntityMetaSegment(Stream fs, string typeName, PropertyInfo[] metaProps, List<object> entities)
    {
        var offset = fs.Position;
        var tracker = new Crc32Helper.CrcTrackingStream(fs);
        using (var bw = new BinaryWriter(tracker, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(typeName);

            bw.Write(metaProps.Length);
            foreach (var prop in metaProps)
            {
                bw.Write(prop.Name);
                bw.Write((byte)GetTypeCode(prop.PropertyType, typeName, prop.Name));
            }

            bw.Write(entities.Count);
            foreach (var entity in entities)
                foreach (var prop in metaProps)
                    WriteValue(bw, prop.PropertyType, prop.GetValue(entity));

            bw.Flush();
        }

        var length = fs.Position - offset;
        var crc = tracker.GetCurrent();
        return new FooterEntry(typeName, offset, length, entities.Count, crc, SegmentKind.EntityMeta);
    }

    /// <summary>
    /// 写入 VectorBlob 段。默认 (Float32 + 无截断) 走旧版布局：
    /// <c>[TypeName s][FieldName s][Dim i32][Count i32][Flags u8] [NullBitmap ⌈Count/8⌉ B] [Floats: Count×Dim×4 B]</c>。
    /// 当字段声明了 <see cref="VectorQuantization.Sq8"/> 或 <c>EffectiveDimensions &lt; Dimensions</c> 时启用 v2 扩展头
    /// (Flags 高位 = Extended)，详见 <see cref="VectorBlobFormat"/>。
    /// <para>null 槽位用 0 填充占满整行 stride，保证 mmap 可按 index 直接定位。</para>
    /// </summary>
    private static FooterEntry WriteVectorBlobSegment(Stream fs, PayloadDescriptor descriptor, List<object> entities)
    {
        var vectorProp = descriptor.Property;
        var typeName = descriptor.TypeName;
        var declaredDim = descriptor.DeclaredDimensions;
        var storageDim = descriptor.StorageDimensions;
        var encoding = descriptor.VectorEncoding;
        var normFlag = descriptor.VectorNormFlags;
        var count = entities.Count;

        // 是否需要 v2 扩展头：任一非默认条件成立
        var useExtended = encoding != VectorBlobEncoding.Float32
                          || storageDim != declaredDim
                          || normFlag != VectorBlobNormFlags.None;

        // 预扫描收集 null 标记
        byte[]? nullBitmap = null;
        bool hasNulls = false;
        for (int i = 0; i < count; i++)
        {
            if (vectorProp.GetValue(entities[i]) is null)
            {
                PayloadPipeline.ValidateWriteValue(descriptor, null, i);
                if (nullBitmap is null) nullBitmap = new byte[(count + 7) >> 3];
                nullBitmap[i >> 3] |= (byte)(1 << (i & 7));
                hasNulls = true;
            }
        }

        byte flags = 0;
        if (hasNulls) flags |= VectorBlobFormat.FlagsHasNulls;
        if (useExtended) flags |= VectorBlobFormat.FlagsExtended;

        var offset = fs.Position;
        var tracker = new Crc32Helper.CrcTrackingStream(fs);
        using (var bw = new BinaryWriter(tracker, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(typeName);
            bw.Write(descriptor.FieldName);
            // 兼容性：旧 inspector 只读 Dim 字段；与 storageDim 保持一致
            bw.Write(storageDim);
            bw.Write(count);
            bw.Write(flags);

            if (useExtended)
            {
                bw.Write(VectorBlobFormat.HeaderVersion);
                bw.Write((byte)encoding);
                bw.Write(storageDim);
                bw.Write(storageDim); // EffectiveDim 当前等于 StorageDim；保留以备未来扩展
                bw.Write((byte)normFlag);
                bw.Write((byte)0); // Reserved0
                bw.Write((byte)0); // Reserved1
                bw.Write((byte)0); // Reserved2
                bw.Write(0f);       // QuantBias (symmetric SQ8 不使用)
                if (encoding == VectorBlobEncoding.Sq8)
                {
                    bw.Write(count); // 每行一个 scale
                }
            }

            if (hasNulls) bw.Write(nullBitmap!);

            // 收集每行 scale (仅 SQ8)；先全部置零，写完 payload 后回填到合适位置之前先写完
            // 流式写入：先把 scale 缓冲在内存，等行数据写完后追加？
            // 简化：在 payload 之前预留 scale 区？由于 BinaryWriter 难以回填，改为先扫一遍量化得到 codes+scales，再写。
            sbyte[]? sq8Buffer = null;
            float[]? scales = null;
            if (encoding == VectorBlobEncoding.Sq8)
            {
                sq8Buffer = System.Buffers.ArrayPool<sbyte>.Shared.Rent(storageDim);
                scales = new float[count];

                // 先把 scales 占位写到流（zero），等所有行写完后回不来——所以这里先收集所有 codes 到一个临时数组太占内存。
                // 折中：把 scales 数组写入流头，且行扫描两遍：第一遍量化每行得到 scale，第二遍直接写编码。
                // 但每行重复扫描太慢。改为：把 codes 暂存到 ArrayPool<sbyte> 全段缓冲，scales 同时收集。
                // 全段缓冲大小 = count * storageDim 字节，对于 100w × 768 dim 也仅 ~768 MB——太大。
                // 决策：把整个 SQ8 段写入一个 MemoryStream，最终一次 CopyTo tracker。可控：内存峰值 ≈ scale (count*4) + 最终段拷贝复用 64KB 缓冲。
                // 但这同样意味着 ~768MB 在内存。
                // 更好的方案：保证 scales 区紧跟 payload。我们已写了 i32 QuantScaleLen=count；
                // 这里改为先写 scales 占位再写 payload，并在写完每行时同步覆盖回 scales 区？BinaryWriter 不便 seek。
                // 最终方案：直接 Stream.Position 回写。fs 是可寻址的；tracker 透传 Write/Seek。
                // 实现：先记录 scalesOffset = fs.Position，写 count×4 零字节占位，再写 payload，
                // 最后 fs.Position = scalesOffset 回填，然后回到末尾。CRC tracker 此时已无法正确反映顺序——
                // CRC 仅用于完整性校验，回填覆盖会导致 CRC 失真。
                // 因此放弃 SQ8 直接流式写：改为分配 count×storageDim 字节缓冲（与 Float32 路径相同的总写入字节数），
                // 一次性写入。内存峰值与原 Float32 一致（~25% 行内字节，但需要保留 codes 数组）。
                // -> 实际上 SQ8 字节数 = count × storageDim，比 Float32 (count × storageDim × 4) 还小，没问题。
                System.Buffers.ArrayPool<sbyte>.Shared.Return(sq8Buffer);
                sq8Buffer = null;
                var codesAll = System.Buffers.ArrayPool<sbyte>.Shared.Rent(count * storageDim);
                // 与 Float32 路径一致：cosine 字段的 SQ8 编码必须基于已归一化的向量，
                // 否则 mmap 解码出的 codes*scale 不是单位向量，搜索相似度会失真。
                bool normalizeOnWrite = normFlag == VectorBlobNormFlags.L2Normalized;
                float[]? normBuf = normalizeOnWrite ? new float[storageDim] : null;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var v = vectorProp.GetValue(entities[i]) as float[];
                        var rowCodes = codesAll.AsSpan(i * storageDim, storageDim);
                        if (v is null)
                        {
                            rowCodes.Clear();
                            scales[i] = 0f;
                            continue;
                        }
                        PayloadPipeline.ValidateWriteValue(descriptor, v, i);
                        // 接受声明维度或已截断到 storageDim 的输入
                        if (v.Length != storageDim && v.Length != declaredDim)
                            throw new InvalidOperationException(
                                $"Vector length {v.Length} on '{typeName}.{descriptor.FieldName}' does not match storage Dim={storageDim} (declared {declaredDim}).");
                        var src = v.Length == storageDim ? v.AsSpan() : v.AsSpan(0, storageDim);
                        if (normalizeOnWrite)
                        {
                            var dst = normBuf.AsSpan(0, storageDim);
                            var norm = Vorcyc.Quiver.Numerics.VectorMath.Norm(src);
                            if (norm > 0f)
                                Vorcyc.Quiver.Numerics.VectorMath.Divide(src, norm, dst);
                            else
                                dst.Clear();
                            src = dst;
                        }
                        scales[i] = Sq8Codec.EncodeRow(src, rowCodes);
                    }
                    // 写入 scales
                    var scalesBytes = MemoryMarshal.AsBytes(scales.AsSpan());
                    tracker.Write(scalesBytes);
                    // 写入 codes (一次性)
                    var codesBytes = MemoryMarshal.AsBytes(codesAll.AsSpan(0, count * storageDim));
                    tracker.Write(codesBytes);
                }
                finally { System.Buffers.ArrayPool<sbyte>.Shared.Return(codesAll); }
            }
            else if (encoding == VectorBlobEncoding.Float16)
            {
                // Float16 紧凑写入：每元素 2 字节 (fp16)；null 槽位填零保持 stride 恒定。
                // 实体属性本身即为 Half[]；cosine 字段需在 float 域归一化后再 narrow 回 fp16 落盘，
                // 以保证 mmap 直读的字节即为单位向量。
                var byteLen = storageDim * sizeof(ushort);
                var zeros = System.Buffers.ArrayPool<byte>.Shared.Rent(byteLen);
                bool normalizeOnWrite = normFlag == VectorBlobNormFlags.L2Normalized;
                var rowHalf = new Half[storageDim];
                float[]? normBuf = normalizeOnWrite ? new float[storageDim] : null;
                try
                {
                    Array.Clear(zeros, 0, byteLen);
                    for (int i = 0; i < count; i++)
                    {
                        var v = vectorProp.GetValue(entities[i]) as Half[];
                        if (v is null)
                        {
                            tracker.Write(zeros, 0, byteLen);
                            continue;
                        }
                        PayloadPipeline.ValidateWriteValue(descriptor, v, i);
                        if (v.Length != storageDim && v.Length != declaredDim)
                            throw new InvalidOperationException(
                                $"Vector length {v.Length} on '{typeName}.{descriptor.FieldName}' does not match storage Dim={storageDim} (declared {declaredDim}).");
                        var src = v.Length == storageDim ? v.AsSpan() : v.AsSpan(0, storageDim);
                        ReadOnlySpan<Half> rowToWrite;
                        if (normalizeOnWrite)
                        {
                            // fp16 → fp32 归一化 → fp16，写到独立缓冲，避免修改实体数组。
                            var f = normBuf.AsSpan(0, storageDim);
                            Vorcyc.Quiver.Numerics.VectorMath.WidenHalfToFloat(src, f);
                            var norm = Vorcyc.Quiver.Numerics.VectorMath.Norm(f);
                            if (norm > 0f)
                                Vorcyc.Quiver.Numerics.VectorMath.Divide(f, norm, f);
                            else
                                f.Clear();
                            Vorcyc.Quiver.Numerics.VectorMath.NarrowFloatToHalf(f, rowHalf);
                            rowToWrite = rowHalf;
                        }
                        else
                        {
                            rowToWrite = src;
                        }
                        var bytes = MemoryMarshal.AsBytes(rowToWrite);
                        tracker.Write(bytes);
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(zeros); }
            }
            else
            {
                // Float32 紧凑写入；null 槽位填零保持 stride 恒定
                var byteLen = storageDim * sizeof(float);
                var zeros = System.Buffers.ArrayPool<byte>.Shared.Rent(byteLen);
                // 当 header 标记 L2Normalized 时（即 cosine 字段），必须保证写入磁盘的字节本身已归一化。
                // mmap 加载路径直接把这些字节暴露给搜索（不会再做 per-row 归一化），所以这里
                // 不能假设上层 PrepareVectors 已经原地归一化过（例如 QuiverMigrator 直接调用 SaveAsync
                // 时实体上的向量是原始未归一化数据）。对已归一化的向量再次归一化是幂等的，开销可忽略。
                bool normalizeOnWrite = normFlag == VectorBlobNormFlags.L2Normalized;
                float[]? normBuf = normalizeOnWrite ? new float[storageDim] : null;
                try
                {
                    Array.Clear(zeros, 0, byteLen);
                    for (int i = 0; i < count; i++)
                    {
                        var v = vectorProp.GetValue(entities[i]) as float[];
                        if (v is null)
                        {
                            tracker.Write(zeros, 0, byteLen);
                        }
                        else
                        {
                            PayloadPipeline.ValidateWriteValue(descriptor, v, i);
                            if (v.Length != storageDim && v.Length != declaredDim)
                                throw new InvalidOperationException(
                                    $"Vector length {v.Length} on '{typeName}.{descriptor.FieldName}' does not match storage Dim={storageDim} (declared {declaredDim}).");
                            var src = v.Length == storageDim ? v.AsSpan() : v.AsSpan(0, storageDim);
                            ReadOnlySpan<byte> bytes;
                            if (normalizeOnWrite)
                            {
                                // 写到独立缓冲，避免修改实体自身的数组（实体可能仍被外部引用）。
                                var dst = normBuf.AsSpan(0, storageDim);
                                var norm = Vorcyc.Quiver.Numerics.VectorMath.Norm(src);
                                if (norm > 0f)
                                    Vorcyc.Quiver.Numerics.VectorMath.Divide(src, norm, dst);
                                else
                                    dst.Clear();
                                bytes = MemoryMarshal.AsBytes((ReadOnlySpan<float>)dst);
                            }
                            else
                            {
                                bytes = MemoryMarshal.AsBytes(src);
                            }
                            tracker.Write(bytes);
                        }
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(zeros); }
            }

            bw.Flush();
        }

        var length = fs.Position - offset;
        var crc = tracker.GetCurrent();
        return new FooterEntry(typeName, offset, length, count, crc, SegmentKind.VectorBlob, descriptor.FieldName, storageDim, 0);
    }

    /// <summary>
    /// 写入 Blob 段。布局：
    /// <c>[TypeName s][FieldName s][Count i32][HasNulls u8] [NullBitmap ⌈Count/8⌉ B (if HasNulls=1)] [Offsets (Count+1) × i64] [Bytes ...]</c>。
    /// <para>Offsets 数组让任意 index 可在 O(1) 时间内定位到字节范围；null 槽位的 length 为 0。</para>
    /// </summary>
    private static FooterEntry WriteBlobSegment(Stream fs, PayloadDescriptor descriptor, List<object> entities, Vorcyc.Quiver.Payloads.ILargeFieldSliceSource? largeFieldSlices)
    {
        var typeName = descriptor.TypeName;
        var blobProp = descriptor.Property;
        var count = entities.Count;

        // 预扫描：统计 null bitmap 与 offsets
        byte[]? nullBitmap = null;
        bool hasNulls = false;
        var offsets = new long[count + 1];
        long cursor = 0;
        for (int i = 0; i < count; i++)
        {
            offsets[i] = cursor;
            var arr = blobProp.GetValue(entities[i]) as byte[];
            if (arr is null)
            {
                if (largeFieldSlices is not null
                    && largeFieldSlices.TryGetLargeFieldSlice(i, descriptor.FieldName, out var slice))
                {
                    if (slice.IsNull)
                    {
                        PayloadPipeline.ValidateWriteValue(descriptor, null, i);
                        if (nullBitmap is null) nullBitmap = new byte[(count + 7) >> 3];
                        nullBitmap[i >> 3] |= (byte)(1 << (i & 7));
                        hasNulls = true;
                    }
                    else
                    {
                        cursor += slice.Length;
                    }
                    continue;
                }

                PayloadPipeline.ValidateWriteValue(descriptor, null, i);
                if (nullBitmap is null) nullBitmap = new byte[(count + 7) >> 3];
                nullBitmap[i >> 3] |= (byte)(1 << (i & 7));
                hasNulls = true;
            }
            else
            {
                PayloadPipeline.ValidateWriteValue(descriptor, arr, i);
                cursor += arr.Length;
            }
        }
        offsets[count] = cursor;

        var offset = fs.Position;
        var tracker = new Crc32Helper.CrcTrackingStream(fs);
        using (var bw = new BinaryWriter(tracker, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(typeName);
            bw.Write(descriptor.FieldName);
            bw.Write(count);
            bw.Write(hasNulls);
            if (hasNulls) bw.Write(nullBitmap!);

            // Offsets
            var offsetBytes = MemoryMarshal.AsBytes(offsets.AsSpan());
            tracker.Write(offsetBytes);

            // Bytes
            for (int i = 0; i < count; i++)
            {
                var arr = blobProp.GetValue(entities[i]) as byte[];
                if (arr is { Length: > 0 })
                {
                    tracker.Write(arr, 0, arr.Length);
                }
                else if (arr is null
                         && largeFieldSlices is not null
                         && largeFieldSlices.TryGetLargeFieldSlice(i, descriptor.FieldName, out var slice)
                         && !slice.IsNull)
                {
                    slice.CopyTo(tracker);
                }
            }
            bw.Flush();
        }

        var length = fs.Position - offset;
        var crc = tracker.GetCurrent();
        return new FooterEntry(typeName, offset, length, count, crc, SegmentKind.Blob, descriptor.FieldName, 0, 0);
    }

    /// <summary>
    /// 写入 Tombstone 段。布局：<c>[TypeName s][DeadCount i32][DeadIds: i32 × DeadCount]</c>。
    /// <para>
    /// DeadIds 是按升序排列的内部行号；加载时这些行号上的实体将从 <see cref="LoadV4"/> 的结果中剔除。
    /// 即使空 tombstone 也允许写入（用于显式标记“此前所有删除已被合并”）。
    /// </para>
    /// </summary>
    internal static FooterEntry WriteTombstoneSegment(Stream fs, string typeName, IEnumerable<int> deadIds)
    {
        var sorted = deadIds.Distinct().OrderBy(x => x).ToArray();
        var offset = fs.Position;
        var tracker = new Crc32Helper.CrcTrackingStream(fs);
        using (var bw = new BinaryWriter(tracker, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(typeName);
            bw.Write(sorted.Length);
            tracker.Write(MemoryMarshal.AsBytes(sorted.AsSpan()));
            bw.Flush();
        }
        var length = fs.Position - offset;
        var crc = tracker.GetCurrent();
        return new FooterEntry(typeName, offset, length, sorted.Length, crc, SegmentKind.Tombstone, null, 0, 0);
    }

    /// <summary>
    /// 写入 IndexSnapshot 段。布局：<c>[TypeName s][FieldName s][PayloadLen i32][Payload bytes]</c>。
    /// <para>
    /// Payload 由具体索引实现（当前为 HNSW）通过 <paramref name="writeSnapshotPayload"/> 回调写出，
    /// 内部需要带自校验指纹（参数 / 维度 / 相似度类型）。调用方在 payload 写入失败或返回
    /// <see langword="false"/> 时应整体丢弃该段（不写入 footer 条目）。
    /// </para>
    /// <para>
    /// <paramref name="entityCount"/> 用于 footer 信息展示，并不参与正确性校验；通常传入快照覆盖的节点数。
    /// </para>
    /// </summary>
    /// <returns>
    /// 当 <paramref name="writeSnapshotPayload"/> 返回 <see langword="true"/> 且 payload 长度 &gt; 0 时返回
    /// 写入的 footer entry；否则回滚到段起点并返回 <see langword="null"/>。
    /// </returns>
    internal static FooterEntry? WriteIndexSnapshotSegment(
        Stream fs,
        string typeName,
        string fieldName,
        int entityCount,
        Func<BinaryWriter, bool> writeSnapshotPayload)
    {
        ArgumentNullException.ThrowIfNull(writeSnapshotPayload);

        // 先把整段写到内存缓冲：避免依赖 fs 可读（SaveAsync 用 FileAccess.Write 打开）。
        // 同时让 CRC 计算和 PayloadLen 回填都在内存里完成，最终一次性写盘。
        var buffer = new MemoryStream(256);
        bool wrote;
        long lengthPos;
        long payloadStart;
        using (var bw = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(typeName);
            bw.Write(fieldName);
            lengthPos = buffer.Position;
            bw.Write(0); // 占位 PayloadLen
            bw.Flush();
            payloadStart = buffer.Position;
            try
            {
                wrote = writeSnapshotPayload(bw);
                bw.Flush();
            }
            catch
            {
                return null;
            }
        }

        if (!wrote) return null;
        var payloadLen = (int)(buffer.Position - payloadStart);
        if (payloadLen <= 0) return null;

        // 回填 PayloadLen
        var savedPos = buffer.Position;
        buffer.Position = lengthPos;
        Span<byte> lenBuf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payloadLen);
        buffer.Write(lenBuf);
        buffer.Position = savedPos;

        var segmentBytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var crc = Crc32Helper.Compute(segmentBytes);

        var offset = fs.Position;
        fs.Write(segmentBytes);
        var length = fs.Position - offset;
        return new FooterEntry(typeName, offset, length, entityCount, crc, SegmentKind.IndexSnapshot, fieldName, 0, 0);
    }

    /// <summary>
    /// 写入文件尾：FooterTopMagic + SchemaVersion(u8) + SegmentCount + Entries + FooterOffset + TrailerMagic。
    /// <para>schema v2 起，每个 entry 在原有 5 个字段后追加 <c>[Kind u8][FieldName string][Dim i32][FirstId i32]</c>。</para>
    /// </summary>
    private static void WriteFooter(Stream fs, List<FooterEntry> entries)
    {
        var footerOffset = fs.Position;
        using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(FooterTopMagicV2);
            bw.Write(entries.Count);
            foreach (var e in entries)
            {
                bw.Write(e.TypeName);
                bw.Write(e.Offset);
                bw.Write(e.Length);
                bw.Write(e.EntityCount);
                bw.Write(e.Crc32);
                bw.Write((byte)e.Kind);
                bw.Write(e.FieldName ?? string.Empty);
                bw.Write(e.Dim);
                bw.Write(e.FirstId);
            }
            bw.Write(footerOffset);
            bw.Write(TrailerMagic);
            bw.Flush();
        }
    }

    #endregion

    /// <summary>
    /// 将单个属性值写入二进制流。
    /// <para>
    /// 写入格式：先写 1 字节的 null 标志（<see langword="false"/> 表示 null，<see langword="true"/> 表示非 null），
    /// 非 null 时紧接着写入实际值的字节。
    /// </para>
    /// </summary>
    /// <param name="bw">目标二进制写入器。</param>
    /// <param name="type">属性的 CLR 类型。</param>
    /// <param name="value">属性值，可能为 <see langword="null"/>。</param>
    private static void WriteValue(BinaryWriter bw, Type type, object? value)
    {
        // null 标志：false = null，无后续字节
        if (value == null) { bw.Write(false); return; }
        bw.Write(true);

        if (type == typeof(string)) bw.Write((string)value);
        else if (type == typeof(int)) bw.Write((int)value);
        else if (type == typeof(long)) bw.Write((long)value);
        else if (type == typeof(float)) bw.Write((float)value);
        else if (type == typeof(double)) bw.Write((double)value);
        else if (type == typeof(bool)) bw.Write((bool)value);
        else if (type == typeof(DateTime)) bw.Write(((DateTime)value).ToBinary());
        else if (type == typeof(Guid)) bw.Write(((Guid)value).ToByteArray());
        else if (type == typeof(decimal)) bw.Write((decimal)value);
        else if (type == typeof(byte)) bw.Write((byte)value);
        else if (type == typeof(short)) bw.Write((short)value);
        else if (type == typeof(Half)) bw.Write((Half)value);
        else if (type == typeof(DateTimeOffset))
        {
            // DateTimeOffset：Ticks (8 字节) + Offset 分钟数 (2 字节) = 10 字节
            var dto = (DateTimeOffset)value;
            bw.Write(dto.Ticks);
            bw.Write((short)dto.Offset.TotalMinutes);
        }
        else if (type == typeof(TimeSpan)) bw.Write(((TimeSpan)value).Ticks);
        else if (type == typeof(float[]))
        {
            // 浮点数组：先写长度，再用 MemoryMarshal 零拷贝写入原始字节
            var arr = (float[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(string[]))
        {
            // 字符串数组：先写长度，再逐个写入每个字符串
            var arr = (string[])value;
            bw.Write(arr.Length);
            foreach (var s in arr) bw.Write(s);
        }
        else if (type == typeof(byte[]))
        {
            // 字节数组：先写长度，再直接写入原始字节
            var arr = (byte[])value;
            bw.Write(arr.Length);
            bw.Write(arr);
        }
        else if (type == typeof(double[]))
        {
            // 双精度浮点数组：先写长度，再用 MemoryMarshal 零拷贝写入原始字节
            var arr = (double[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(ushort)) bw.Write((ushort)value);
        else if (type == typeof(uint)) bw.Write((uint)value);
        else if (type == typeof(ulong)) bw.Write((ulong)value);
        else if (type == typeof(sbyte)) bw.Write((sbyte)value);
        else if (type == typeof(char)) bw.Write((ushort)(char)value); // 以 UInt16 存储，避免 BinaryWriter 受编码影响的 char 写法
        else if (type == typeof(DateOnly)) bw.Write(((DateOnly)value).DayNumber);
        else if (type == typeof(TimeOnly)) bw.Write(((TimeOnly)value).Ticks);
        else if (type == typeof(ushort[]))
        {
            var arr = (ushort[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(uint[]))
        {
            var arr = (uint[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(ulong[]))
        {
            var arr = (ulong[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(sbyte[]))
        {
            var arr = (sbyte[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(DateOnly[]))
        {
            var arr = (DateOnly[])value;
            bw.Write(arr.Length);
            foreach (var d in arr) bw.Write(d.DayNumber);
        }
        else if (type == typeof(TimeOnly[]))
        {
            var arr = (TimeOnly[])value;
            bw.Write(arr.Length);
            foreach (var t in arr) bw.Write(t.Ticks);
        }
        else if (type == typeof(short[]))
        {
            var arr = (short[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(int[]))
        {
            var arr = (int[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(long[]))
        {
            var arr = (long[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(bool[]))
        {
            // 布尔数组：先写长度，再逐元素写入 1 字节（bool 在内存中非保证 1 字节，故不做零拷贝）。
            var arr = (bool[])value;
            bw.Write(arr.Length);
            foreach (var b in arr) bw.Write(b);
        }
        else if (type == typeof(Half[]))
        {
            // 半精度浮点数组：每元素 2 字节，按原始字节零拷贝写入。
            var arr = (Half[])value;
            bw.Write(arr.Length);
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
    }

    #region Load

    /// <summary>
    /// 从指定二进制文件异步加载所有向量集合。
    /// <para>
    /// 加载入口按前 4 字节 magic 分发到 v4 (Segment+Footer) 或 v1/v2/v3 (顺序流) 路径。
    /// </para>
    /// </summary>
    /// <param name="filePath">要读取的二进制文件路径。</param>
    /// <param name="typeMap">
    /// 类型名称到 CLR <see cref="Type"/> 的映射字典。
    /// 文件中存在但字典中缺失的类型将被跳过。
    /// </param>
    /// <param name="migrationRules">
    /// 可选的 Schema 迁移规则字典。键为类型全名，值为迁移规则。
    /// 包含属性重命名映射，加载时将文件中的旧属性名映射到当前 CLR 类型的新属性名。
    /// </param>
    /// <returns>加载后的向量集合字典，键为类型名称，值为实体对象列表。</returns>
    /// <exception cref="InvalidDataException">当文件魔术字节不匹配时抛出。</exception>
    /// <exception cref="QuiverFormatVersionException">
    /// 当文件是 v1/v2/v3 旧格式时抛出。请先调用
    /// <see cref="QuiverMigrator.MigrateAsync"/> 升级到 v4。
    /// </exception>
    public async Task<Dictionary<string, List<object>>> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        var result = new Dictionary<string, List<object>>();

        await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            var version = DetectVersion(fs);

            if (version == 4)
                LoadV4(fs, typeMap, migrationRules, result, isMmapField: null, regions: null);
            else
                throw new QuiverFormatVersionException(filePath, version);
        });

        return result;
    }

    /// <summary>
    /// 仅供 <see cref="QuiverMigrator.MigrateAsync"/> 等迁移工具使用：
    /// 强制读取任意版本（v1/v2/v3/v4）的文件，不抛 <see cref="QuiverFormatVersionException"/>。
    /// 正常运行时加载请走 <see cref="LoadAsync(string, IReadOnlyDictionary{string, Type}, IReadOnlyDictionary{string, SchemaMigrationRule}?)"/>。
    /// </summary>
    internal async Task<Dictionary<string, List<object>>> LoadAnyVersionAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        var result = new Dictionary<string, List<object>>();

        await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            var version = DetectVersion(fs);

            if (version == 4)
                LoadV4(fs, typeMap, migrationRules, result, isMmapField: null, regions: null);
            else
                LoadLegacy(fs, typeMap, migrationRules, result);
        });

        return result;
    }

    /// <summary>
    /// mmap 感知的加载入口。对于命中 <paramref name="isMmapField"/> 谓词的 <c>(typeName, fieldName)</c>，
    /// <b>跳过</b> 向量 <c>float[]</c> 的物化（不写回实体属性，也不分配数组），改为输出
    /// <see cref="MmapVectorRegion"/> 描述符到 <paramref name="regions"/>；后续由 <see cref="QuiverDbContext"/>
    /// 把这些区域绑定到 <see cref="Vorcyc.Quiver.Indexing.MmapVectorStore"/>。
    /// <para>
    /// 谓词使用文件中保存的原始字段名（未应用 schema 重命名）；如果调用方启用了重命名，
    /// 需要在外部用 <paramref name="migrationRules"/> 自行映射。
    /// </para>
    /// </summary>
    public async Task<(Dictionary<string, List<object>> Sets, List<MmapVectorRegion> Regions)> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        Func<string, string, bool> isMmapField,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        ArgumentNullException.ThrowIfNull(isMmapField);
        var result = new Dictionary<string, List<object>>();
        var regions = new List<MmapVectorRegion>();

        await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            var version = DetectVersion(fs);

            if (version == 4)
                LoadV4(fs, typeMap, migrationRules, result, isMmapField, regions);
            else
                throw new QuiverFormatVersionException(filePath, version);
        });

        return (result, regions);
    }

    public async Task<(Dictionary<string, List<object>> Sets, List<MmapVectorRegion> VectorRegions, List<LargeFieldRegion> LargeFieldRegions)> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        Func<string, string, bool> isMmapField,
        Func<string, string, bool> isLazyLargeField,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        ArgumentNullException.ThrowIfNull(isMmapField);
        ArgumentNullException.ThrowIfNull(isLazyLargeField);
        var result = new Dictionary<string, List<object>>();
        var vectorRegions = new List<MmapVectorRegion>();
        var largeFieldRegions = new List<LargeFieldRegion>();

        await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            var version = DetectVersion(fs);

            if (version == 4)
                LoadV4(fs, typeMap, migrationRules, result, isMmapField, vectorRegions, isLazyLargeField, largeFieldRegions);
            else
                throw new QuiverFormatVersionException(filePath, version);
        });

        return (result, vectorRegions, largeFieldRegions);
    }

    /// <summary>
    /// 不重新加载实体，仅扫描 v4 文件的 footer 并解析每个 <c>VectorBlob</c> 段的物理布局，
    /// 主要用于 <see cref="QuiverDbContext.SaveAsync"/> 写盘后重新建立 mmap 绑定。
    /// </summary>
    public static List<MmapVectorRegion> ReadVectorBlobRegions(string filePath)
    {
        var regions = new List<MmapVectorRegion>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        if (DetectVersion(fs) != 4) return regions;

        fs.Position = 4;
        var br0 = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        var headerLen = br0.ReadInt32();
        fs.Position += headerLen;

        var (entries, _) = ReadFooter(fs);

        // 与 LoadV4 一致：在重新打开的文件中按 EntityMeta -> VectorBlob 顺序追踪每个 typeName 的当前实体起始 index。
        var lastChunkStart = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastChunkCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalPerType = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            fs.Position = entry.Offset;
            using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

            switch (entry.Kind)
            {
                case SegmentKind.Mixed:
                case SegmentKind.EntityMeta:
                {
                    var before = totalPerType.GetValueOrDefault(entry.TypeName);
                    lastChunkStart[entry.TypeName] = before;
                    lastChunkCount[entry.TypeName] = entry.EntityCount;
                    totalPerType[entry.TypeName] = before + entry.EntityCount;
                    break;
                }
                case SegmentKind.VectorBlob:
                {
                    var typeName = br.ReadString();
                    var fieldName = br.ReadString();
                    var dim = br.ReadInt32();
                    var rowCount = br.ReadInt32();
                    var flags = br.ReadByte();
                    bool hasNulls = (flags & VectorBlobFormat.FlagsHasNulls) != 0;
                    bool extended = (flags & VectorBlobFormat.FlagsExtended) != 0;

                    var encoding = VectorBlobEncoding.Float32;
                    var storageDim = dim;
                    var effectiveDim = dim;
                    float[]? sq8Scales = null;

                    if (extended)
                    {
                        var version = br.ReadByte();
                        if (version != VectorBlobFormat.HeaderVersion)
                            throw new InvalidDataException($"Unsupported VectorBlob extended header version {version}.");
                        encoding = (VectorBlobEncoding)br.ReadByte();
                        storageDim = br.ReadInt32();
                        effectiveDim = br.ReadInt32();
                        _ = br.ReadByte(); // normFlags
                        _ = br.ReadByte(); // Reserved0
                        _ = br.ReadByte(); // Reserved1
                        _ = br.ReadByte(); // Reserved2
                        _ = br.ReadSingle(); // QuantBias (reserved, symmetric SQ8 = 0)
                        if (encoding == VectorBlobEncoding.Sq8)
                        {
                            var scaleLen = br.ReadInt32();
                            var scaleBytes = br.ReadBytes(scaleLen * sizeof(float));
                            sq8Scales = new float[scaleLen];
                            MemoryMarshal.Cast<byte, float>(scaleBytes).CopyTo(sq8Scales);
                        }
                    }

                    byte[]? nullBitmap = hasNulls ? br.ReadBytes((rowCount + 7) >> 3) : null;
                    var payloadOffset = fs.Position;
                    var chunkStart = lastChunkStart.GetValueOrDefault(typeName);
                    var chunkCount = lastChunkCount.GetValueOrDefault(typeName, rowCount);
                    regions.Add(new MmapVectorRegion(typeName, fieldName, effectiveDim, rowCount, payloadOffset, chunkStart, chunkCount, nullBitmap, encoding, storageDim, sq8Scales));
                    break;
                }
                default:
                    break;
            }
        }
        return regions;
    }

    /// <summary>
    /// 扫描 v4 文件 footer，把所有 <see cref="SegmentKind.IndexSnapshot"/> 段读出为
    /// <c>(typeName, fieldName) → payload bytes</c> 字典。Payload 即各索引实现自己定义的格式
    /// （例如 <c>HnswIndex</c> 内嵌的 <c>QHNS</c> 序列）。
    /// <para>
    /// CRC 校验失败的段会被跳过（不抛异常，但通过返回值与 footer 段计数差异可感知）。
    /// 不是 v4 文件直接返回空字典。
    /// </para>
    /// </summary>
    public static Dictionary<(string TypeName, string FieldName), byte[]> ReadIndexSnapshots(string filePath)
    {
        var snapshots = new Dictionary<(string, string), byte[]>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        if (DetectVersion(fs) != 4) return snapshots;

        fs.Position = 4;
        var br0 = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        var headerLen = br0.ReadInt32();
        fs.Position += headerLen;

        var (entries, _) = ReadFooter(fs);

        foreach (var entry in entries)
        {
            if (entry.Kind != SegmentKind.IndexSnapshot) continue;

            // 校验段 CRC，损坏即跳过
            var actualCrc = Crc32Helper.ComputeFromStream(fs, entry.Offset, entry.Length);
            if (actualCrc != entry.Crc32) continue;

            fs.Position = entry.Offset;
            using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
            var typeName = br.ReadString();
            var fieldName = br.ReadString();
            var payloadLen = br.ReadInt32();
            if (payloadLen <= 0) continue;

            var payload = br.ReadBytes(payloadLen);
            if (payload.Length != payloadLen) continue;
            snapshots[(typeName, fieldName)] = payload;
        }

        return snapshots;
    }
    /// <exception cref="InvalidDataException">不是已知 Quiver 文件。</exception>
    private static int DetectVersion(Stream fs)
    {
        fs.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4) throw new InvalidDataException("Invalid QuiverDb binary file: too short.");
        if (magic.SequenceEqual(Magic))   return 4;
        if (magic.SequenceEqual(MagicV3)) return 3;
        if (magic.SequenceEqual(MagicV2)) return 2;
        if (magic.SequenceEqual(MagicV1)) return 1;
        throw new InvalidDataException("Invalid QuiverDb binary file.");
    }

    /// <summary>
    /// v4 路径：按 footer 顺序解码每段。Mixed 段独立成实体；EntityMeta 段产生实体并记录区间，
    /// 紧随其后的同类型 VectorBlob 段把向量回填到该区间。
    /// </summary>
    private static void LoadV4(
        Stream fs,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        Dictionary<string, List<object>> result,
        Func<string, string, bool>? isMmapField,
        List<MmapVectorRegion>? regions,
        Func<string, string, bool>? isLazyLargeField = null,
        List<LargeFieldRegion>? largeFieldRegions = null)
    {
        // 跳过文件头（HeaderLen + Header bytes）
        fs.Position = 4; // 跳过 Magic
        var br0 = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        var headerLen = br0.ReadInt32();
        fs.Position += headerLen;

        var (entries, _) = ReadFooter(fs);

        // 每个 typeName 维护“最近一次 EntityMeta 段在 result 中产生的实体起始 index 和数量”，
        // 以便后续 VectorBlob 段把向量回填到正确的实体区间。
        var lastChunk = new Dictionary<string, (int Start, int Count)>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            fs.Position = entry.Offset;
            using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

            switch (entry.Kind)
            {
                case SegmentKind.Mixed:
                {
                    // 老式段：读到的实体追加进 result；记录起始/数量供（极少出现的）后续 VectorBlob 回填用。
                    var before = result.TryGetValue(entry.TypeName, out var existing) ? existing.Count : 0;
                    ReadSegment(br, typeMap, migrationRules, result);
                    var after = result.TryGetValue(entry.TypeName, out var nowList) ? nowList.Count : before;
                    if (after > before) lastChunk[entry.TypeName] = (before, after - before);
                    break;
                }
                case SegmentKind.EntityMeta:
                {
                    var before = result.TryGetValue(entry.TypeName, out var existing) ? existing.Count : 0;
                    ReadSegment(br, typeMap, migrationRules, result);
                    var after = result.TryGetValue(entry.TypeName, out var nowList) ? nowList.Count : before;
                    lastChunk[entry.TypeName] = (before, after - before);
                    break;
                }
                case SegmentKind.VectorBlob:
                {
                    if (!typeMap.TryGetValue(entry.TypeName, out var clrType)) { /* unknown type → skip */ break; }
                    if (!lastChunk.TryGetValue(entry.TypeName, out var chunk) || chunk.Count == 0) break;
                    if (!result.TryGetValue(entry.TypeName, out var entities)) break;
                    ReadVectorBlobIntoEntities(br, clrType, entities, chunk.Start, chunk.Count, migrationRules, isMmapField, regions);
                    break;
                }
                case SegmentKind.Blob:
                {
                    if (!typeMap.TryGetValue(entry.TypeName, out var clrType)) break;
                    if (!lastChunk.TryGetValue(entry.TypeName, out var chunk) || chunk.Count == 0) break;
                    if (!result.TryGetValue(entry.TypeName, out var entities)) break;
                    ReadBlobIntoEntities(br, clrType, entities, chunk.Start, chunk.Count, migrationRules, isLazyLargeField, largeFieldRegions);
                    break;
                }
                case SegmentKind.Tombstone:
                {
                    var deadTypeName = br.ReadString();
                    var deadCount = br.ReadInt32();
                    if (deadCount <= 0) break;
                    if (!result.TryGetValue(deadTypeName, out var deadEntities)) { br.BaseStream.Position += deadCount * sizeof(int); break; }
                    var idBytes = br.ReadBytes(deadCount * sizeof(int));
                    var deadIds = MemoryMarshal.Cast<byte, int>(idBytes);
                    for (int k = 0; k < deadIds.Length; k++)
                    {
                        int idx = deadIds[k];
                        if ((uint)idx < (uint)deadEntities.Count) deadEntities[idx] = null!;
                    }
                    break;
                }
                default:
                    // 暂未实现的段种类：跳过（footer 已记录长度，下一轮 fs.Position 重定位即可）
                    break;
            }
        }
    }

    /// <summary>读取 VectorBlob 段并把向量回填到 <paramref name="entities"/> 的 <c>[start, start+count)</c> 区间。</summary>
    private static void ReadVectorBlobIntoEntities(
        BinaryReader br,
        Type clrType,
        List<object> entities,
        int start,
        int count,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        Func<string, string, bool>? isMmapField,
        List<MmapVectorRegion>? regions)
    {
        var typeName = br.ReadString();
        var fieldName = br.ReadString();
        var dim = br.ReadInt32();
        var blobCount = br.ReadInt32();
        var flags = br.ReadByte();
        bool hasNulls = (flags & VectorBlobFormat.FlagsHasNulls) != 0;
        bool extended = (flags & VectorBlobFormat.FlagsExtended) != 0;

        var encoding = VectorBlobEncoding.Float32;
        var storageDim = dim;
        var effectiveDim = dim;
        float[]? sq8Scales = null;

        if (extended)
        {
            var version = br.ReadByte();
            if (version != VectorBlobFormat.HeaderVersion)
                throw new InvalidDataException($"Unsupported VectorBlob extended header version {version}.");
            encoding = (VectorBlobEncoding)br.ReadByte();
            storageDim = br.ReadInt32();
            effectiveDim = br.ReadInt32();
            _ = br.ReadByte(); // normFlags
            _ = br.ReadByte();
            _ = br.ReadByte();
            _ = br.ReadByte();
            _ = br.ReadSingle(); // QuantBias
            if (encoding == VectorBlobEncoding.Sq8)
            {
                var scaleLen = br.ReadInt32();
                var scaleBytes = br.ReadBytes(scaleLen * sizeof(float));
                sq8Scales = new float[scaleLen];
                MemoryMarshal.Cast<byte, float>(scaleBytes).CopyTo(sq8Scales);
            }
        }

        byte[]? nullBitmap = hasNulls ? br.ReadBytes((blobCount + 7) >> 3) : null;

        int stride = VectorBlobFormat.GetRowStride(encoding, storageDim);
        long payloadOffset = br.BaseStream.Position;

        // mmap 接管：跳过行字节并登记 region
        if (isMmapField is not null && isMmapField(typeName, fieldName))
        {
            regions?.Add(new MmapVectorRegion(typeName, fieldName, effectiveDim, blobCount, payloadOffset, start, count, nullBitmap, encoding, storageDim, sq8Scales));
            br.BaseStream.Position = payloadOffset + (long)blobCount * stride;
            return;
        }

        // Schema 迁移：字段重命名
        string clrFieldName = fieldName;
        if (migrationRules != null && migrationRules.TryGetValue(typeName, out var rule)
            && rule.PropertyRenames.TryGetValue(fieldName, out var renamed))
        {
            clrFieldName = renamed;
        }
        var prop = clrType.GetProperty(clrFieldName);
        PayloadDescriptor? descriptor = prop is null
            ? null
            : PayloadPipeline.CreateReadDescriptor(typeName, clrType, clrFieldName, PayloadKind.Vector);

        int n = Math.Min(count, blobCount);
        var rowBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            for (int i = 0; i < blobCount; i++)
            {
                br.BaseStream.ReadExactly(rowBuf.AsSpan(0, stride));
                if (i >= n) continue;
                bool isNull = hasNulls && (nullBitmap![i >> 3] & (1 << (i & 7))) != 0;
                if (isNull)
                {
                    if (descriptor is { } d) PayloadPipeline.ValidateReadNull(d, i);
                    continue;
                }
                if (prop is null) continue;
                var target = entities[start + i];
                if (target is null) continue;

                // Float16 字段直接还原为 Half[]，保持 fp16 物理语义（不经 float 中转回填实体）。
                if (encoding == VectorBlobEncoding.Float16)
                {
                    var half = new Half[effectiveDim];
                    MemoryMarshal.Cast<byte, Half>(rowBuf.AsSpan(0, effectiveDim * sizeof(ushort))).CopyTo(half);
                    prop.SetValue(target, half);
                    continue;
                }

                float[] v;
                if (encoding == VectorBlobEncoding.Float32)
                {
                    v = new float[effectiveDim];
                    // 磁盘 storageDim 可能 > effectiveDim（理论上不会，因为我们当前写时取 EffectiveDim=StorageDim）；
                    // 取前 effectiveDim 个 float 即可。
                    MemoryMarshal.Cast<byte, float>(rowBuf.AsSpan(0, effectiveDim * sizeof(float))).CopyTo(v);
                }
                else // SQ8
                {
                    v = new float[effectiveDim];
                    var codes = MemoryMarshal.Cast<byte, sbyte>(rowBuf.AsSpan(0, storageDim));
                    var scale = sq8Scales is not null && i < sq8Scales.Length ? sq8Scales[i] : 0f;
                    Sq8Codec.DecodeRow(codes.Slice(0, effectiveDim), scale, v);
                }
                prop.SetValue(target, v);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rowBuf); }
    }

    /// <summary>读取 Blob 段并把 <c>byte[]</c> 回填到 <paramref name="entities"/> 的 <c>[start, start+count)</c> 区间。</summary>
    private static void ReadBlobIntoEntities(
        BinaryReader br,
        Type clrType,
        List<object> entities,
        int start,
        int count,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        Func<string, string, bool>? isLazyLargeField = null,
        List<LargeFieldRegion>? largeFieldRegions = null)
    {
        var typeName = br.ReadString();
        var fieldName = br.ReadString();
        var blobCount = br.ReadInt32();
        var hasNulls = br.ReadBoolean();
        byte[]? nullBitmap = hasNulls ? br.ReadBytes((blobCount + 7) >> 3) : null;

        // Offsets: (blobCount+1) × i64
        var offsets = new long[blobCount + 1];
        var offsetBytes = br.ReadBytes((blobCount + 1) * sizeof(long));
        MemoryMarshal.Cast<byte, long>(offsetBytes).CopyTo(offsets);

        // Schema 迁移：字段重命名
        string clrFieldName = fieldName;
        if (migrationRules != null && migrationRules.TryGetValue(typeName, out var rule)
            && rule.PropertyRenames.TryGetValue(fieldName, out var renamed))
        {
            clrFieldName = renamed;
        }
        var prop = clrType.GetProperty(clrFieldName);
        PayloadDescriptor? descriptor = prop is null
            ? null
            : PayloadPipeline.CreateReadDescriptor(typeName, clrType, clrFieldName, PayloadKind.LargeField);

        long totalBytes = offsets[blobCount];
        long payloadStart = br.BaseStream.Position;

        if (isLazyLargeField is not null && isLazyLargeField(typeName, fieldName))
        {
            largeFieldRegions?.Add(new LargeFieldRegion(typeName, fieldName, blobCount, payloadStart, start, count, nullBitmap, offsets));
            br.BaseStream.Position = payloadStart + totalBytes;
            return;
        }

        int n = Math.Min(count, blobCount);
        if (prop is null)
        {
            // 未知字段：跳过全部 bytes
            br.BaseStream.Position = payloadStart + totalBytes;
            return;
        }

        for (int i = 0; i < blobCount; i++)
        {
            long byteLen = offsets[i + 1] - offsets[i];
            if (i >= n)
            {
                br.BaseStream.Position += byteLen;
                continue;
            }
            bool isNull = hasNulls && (nullBitmap![i >> 3] & (1 << (i & 7))) != 0;
            if (isNull || byteLen == 0)
            {
                if (isNull && descriptor is { } d) PayloadPipeline.ValidateReadNull(d, i);
                if (!isNull && byteLen == 0)
                {
                    var t = entities[start + i];
                    if (t is not null) prop.SetValue(t, Array.Empty<byte>());
                }
                continue;
            }
            var buf = new byte[byteLen];
            br.BaseStream.ReadExactly(buf);
            var target = entities[start + i];
            if (target is not null) prop.SetValue(target, buf);
        }
    }

    /// <summary>v1/v2/v3 路径：直接顺序读取 [SetCount] + 各 set。</summary>
    private static void LoadLegacy(
        Stream fs,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        Dictionary<string, List<object>> result)
    {
        // 流已前进过 Magic，继续读 SetCount
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        var setCount = br.ReadInt32();
        for (int s = 0; s < setCount; s++)
            ReadSegment(br, typeMap, migrationRules, result);
    }

    /// <summary>
    /// 仅供迁移工具诊断使用：扫描 v1/v2/v3 旧格式文件，返回 <c>TypeName → EntityCount</c>。
    /// 不实例化任何实体、不需要 <c>typeMap</c>，纯结构遍历。
    /// </summary>
    /// <param name="filePath">v1/v2/v3 旧格式文件路径。</param>
    /// <returns>类型全名到实体计数的字典。</returns>
    /// <exception cref="InvalidDataException">非旧格式文件或结构损坏。</exception>
    internal static Dictionary<string, int> PeekLegacyTypeCounts(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var version = DetectVersion(fs);
        if (version == 4)
            throw new InvalidDataException("PeekLegacyTypeCounts only supports v1/v2/v3 files.");

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        var setCount = br.ReadInt32();
        for (int s = 0; s < setCount; s++)
        {
            var typeName = br.ReadString();
            var propCount = br.ReadInt32();
            var descriptors = new (string Name, TypeCode Code)[propCount];
            for (int p = 0; p < propCount; p++)
                descriptors[p] = (br.ReadString(), (TypeCode)br.ReadByte());

            var entityCount = br.ReadInt32();
            result[typeName] = (result.TryGetValue(typeName, out var prev) ? prev : 0) + entityCount;

            // 跳过所有实体的字节
            for (int e = 0; e < entityCount; e++)
                for (int p = 0; p < propCount; p++)
                    SkipValue(br, descriptors[p].Code);
        }
        return result;
    }

    /// <summary>
    /// 反向定位并解析 footer。返回 footer 条目和 footer 起始偏移（用于 append 时定位 truncate 点）。
    /// </summary>
    internal static (List<FooterEntry> Entries, long FooterOffset) ReadFooter(Stream fs)
    {
        if (fs.Length < TrailerSize) throw new InvalidDataException("File too small for v4 footer.");
        fs.Position = fs.Length - TrailerSize;
        Span<byte> tail = stackalloc byte[TrailerSize];
        fs.ReadExactly(tail);
        var footerOffset = BitConverter.ToInt64(tail[..8]);
        if (!tail[8..12].SequenceEqual(TrailerMagic))
            throw new InvalidDataException("Invalid v4 trailer magic. File may be truncated or corrupted.");

        fs.Position = footerOffset;
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        Span<byte> top = stackalloc byte[4];
        fs.ReadExactly(top);

        bool schemaV2;
        if (top.SequenceEqual(FooterTopMagicV2)) schemaV2 = true;
        else if (top.SequenceEqual(FooterTopMagic)) schemaV2 = false;
        else throw new InvalidDataException("Invalid v4 footer top magic.");

        var count = br.ReadInt32();
        var entries = new List<FooterEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var typeName = br.ReadString();
            var offset = br.ReadInt64();
            var length = br.ReadInt64();
            var entityCount = br.ReadInt32();
            var crc = br.ReadUInt32();
            SegmentKind kind = SegmentKind.Mixed;
            string? fieldName = null;
            int dim = 0;
            int firstId = -1;
            if (schemaV2)
            {
                kind = (SegmentKind)br.ReadByte();
                var fn = br.ReadString();
                if (fn.Length > 0) fieldName = fn;
                dim = br.ReadInt32();
                firstId = br.ReadInt32();
            }
            entries.Add(new FooterEntry(typeName, offset, length, entityCount, crc, kind, fieldName, dim, firstId));
        }
        return (entries, footerOffset);
    }

    /// <summary>解码单个段（公开包装，供同程序集的合并工具复用）。</summary>
    internal static void ReadSegmentPublic(
        BinaryReader br,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        Dictionary<string, List<object>> result)
        => ReadSegment(br, typeMap, migrationRules, result);

    /// <summary>解码单个段（=旧的单 set 字节布局），结果合并入 <paramref name="result"/>。</summary>
    private static void ReadSegment(
        BinaryReader br,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        Dictionary<string, List<object>> result)
    {
        var typeName = br.ReadString();

        var propCount = br.ReadInt32();
        var descriptors = new (string Name, TypeCode Code)[propCount];
        for (int p = 0; p < propCount; p++)
            descriptors[p] = (br.ReadString(), (TypeCode)br.ReadByte());

        var hasType = typeMap.TryGetValue(typeName, out var type);

        SchemaMigrationRule? rule = null;
        if (hasType && migrationRules != null)
            migrationRules.TryGetValue(typeName, out rule);

        var propMap = hasType
            ? descriptors.Select(d =>
            {
                var name = d.Name;
                if (rule?.PropertyRenames.TryGetValue(name, out var newName) == true)
                    name = newName;
                return type!.GetProperty(name);
            }).ToArray()
            : null;

        var entityCount = br.ReadInt32();
        var entities = hasType
            ? (result.TryGetValue(typeName, out var existing) ? existing : (result[typeName] = new List<object>(entityCount)))
            : null;

        for (int e = 0; e < entityCount; e++)
        {
            var entity = hasType ? Activator.CreateInstance(type!)! : null;

            for (int p = 0; p < propCount; p++)
            {
                var prop = propMap?[p];
                if (prop != null && entity != null)
                {
                    var value = ReadValue(br, descriptors[p].Code);
                    if (value != null)
                    {
                        if (value.GetType() != prop.PropertyType)
                            value = CoerceValue(value, prop.PropertyType);
                        if (value != null)
                            prop.SetValue(entity, value);
                    }
                }
                else
                {
                    SkipValue(br, descriptors[p].Code);
                }
            }

            if (entity != null) entities!.Add(entity);
        }
    }

    /// <summary>
    /// 根据类型码从二进制流中读取单个属性值。
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <param name="code">属性的类型码，决定读取方式。</param>
    /// <returns>反序列化后的属性值；若为 null 标志则返回 <see langword="null"/>。</returns>
    /// <exception cref="NotSupportedException">当遇到未知的类型码时抛出。</exception>
    private static object? ReadValue(BinaryReader br, TypeCode code)
    {
        // 先读 null 标志
        if (!br.ReadBoolean()) return null;

        return code switch
        {
            TypeCode.String => br.ReadString(),
            TypeCode.Int32 => br.ReadInt32(),
            TypeCode.Int64 => br.ReadInt64(),
            TypeCode.Single => br.ReadSingle(),
            TypeCode.Double => br.ReadDouble(),
            TypeCode.Boolean => br.ReadBoolean(),
            TypeCode.DateTime => DateTime.FromBinary(br.ReadInt64()),
            TypeCode.Guid => new Guid(br.ReadBytes(16)),
            TypeCode.Decimal => br.ReadDecimal(),
            TypeCode.FloatArray => ReadFloatArray(br),
            TypeCode.StringArray => ReadStringArray(br),
            TypeCode.Byte => br.ReadByte(),
            TypeCode.Int16 => br.ReadInt16(),
            TypeCode.Half => br.ReadHalf(),
            TypeCode.DateTimeOffset => ReadDateTimeOffset(br),
            TypeCode.TimeSpan => TimeSpan.FromTicks(br.ReadInt64()),
            TypeCode.ByteArray => ReadByteArray(br),
            TypeCode.DoubleArray => ReadDoubleArray(br),
            TypeCode.UInt16 => br.ReadUInt16(),
            TypeCode.UInt32 => br.ReadUInt32(),
            TypeCode.UInt64 => br.ReadUInt64(),
            TypeCode.SByte => br.ReadSByte(),
            TypeCode.Char => (char)br.ReadUInt16(),
            TypeCode.DateOnly => DateOnly.FromDayNumber(br.ReadInt32()),
            TypeCode.TimeOnly => new TimeOnly(br.ReadInt64()),
            TypeCode.UInt16Array => ReadBlittableArray<ushort>(br, sizeof(ushort)),
            TypeCode.UInt32Array => ReadBlittableArray<uint>(br, sizeof(uint)),
            TypeCode.UInt64Array => ReadBlittableArray<ulong>(br, sizeof(ulong)),
            TypeCode.SByteArray => ReadBlittableArray<sbyte>(br, sizeof(sbyte)),
            TypeCode.DateOnlyArray => ReadDateOnlyArray(br),
            TypeCode.TimeOnlyArray => ReadTimeOnlyArray(br),
            TypeCode.Int16Array => ReadBlittableArray<short>(br, sizeof(short)),
            TypeCode.Int32Array => ReadBlittableArray<int>(br, sizeof(int)),
            TypeCode.Int64Array => ReadBlittableArray<long>(br, sizeof(long)),
            TypeCode.BooleanArray => ReadBooleanArray(br),
            TypeCode.HalfArray => ReadBlittableArray<Half>(br, 2),
            _ => throw new NotSupportedException()
        };
    }

    /// <summary>
    /// 跳过二进制流中一个属性值占用的全部字节，保证流位置正确。
    /// <para>
    /// 当文件中记录的属性在当前 CLR 类型中已不存在（例如属性被删除或重命名）时调用此方法，
    /// 确保后续属性仍能正确定位——这是二进制格式前向兼容的核心机制。
    /// </para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <param name="code">要跳过的属性类型码。</param>
    private static void SkipValue(BinaryReader br, TypeCode code)
    {
        // null 标志为 false 时无后续字节，直接返回
        if (!br.ReadBoolean()) return;

        switch (code)
        {
            case TypeCode.String: br.ReadString(); break;
            case TypeCode.Int32: br.ReadInt32(); break;
            case TypeCode.Int64: br.ReadInt64(); break;
            case TypeCode.Single: br.ReadSingle(); break;
            case TypeCode.Double: br.ReadDouble(); break;
            case TypeCode.Boolean: br.ReadBoolean(); break;
            case TypeCode.DateTime: br.ReadInt64(); break;           // DateTime 以 Int64 存储
            case TypeCode.Guid: br.ReadBytes(16); break;             // Guid 固定 16 字节
            case TypeCode.Decimal: br.ReadDecimal(); break;
            case TypeCode.FloatArray:
                br.ReadBytes(br.ReadInt32() * sizeof(float)); break; // 跳过 [长度 × 4] 字节
            case TypeCode.StringArray:
                for (int i = br.ReadInt32(); i > 0; i--) br.ReadString(); break; // 逐个跳过字符串
            case TypeCode.Byte: br.ReadByte(); break;                // 1 字节
            case TypeCode.Int16: br.ReadInt16(); break;              // 2 字节
            case TypeCode.Half: br.ReadBytes(2); break;              // Half 固定 2 字节
            case TypeCode.DateTimeOffset:                            // Ticks (8) + OffsetMinutes (2) = 10 字节
                br.ReadInt64(); br.ReadInt16(); break;
            case TypeCode.TimeSpan: br.ReadInt64(); break;           // TimeSpan 以 Int64 Ticks 存储
            case TypeCode.ByteArray:
                br.ReadBytes(br.ReadInt32()); break;                 // 跳过 [长度] 字节
            case TypeCode.DoubleArray:
                br.ReadBytes(br.ReadInt32() * sizeof(double)); break; // 跳过 [长度 × 8] 字节
            case TypeCode.UInt16: br.ReadUInt16(); break;            // 2 字节
            case TypeCode.UInt32: br.ReadUInt32(); break;            // 4 字节
            case TypeCode.UInt64: br.ReadUInt64(); break;            // 8 字节
            case TypeCode.SByte: br.ReadSByte(); break;              // 1 字节
            case TypeCode.Char: br.ReadUInt16(); break;              // 以 UInt16 存储，2 字节
            case TypeCode.DateOnly: br.ReadInt32(); break;           // DayNumber Int32，4 字节
            case TypeCode.TimeOnly: br.ReadInt64(); break;           // Ticks Int64，8 字节
            case TypeCode.UInt16Array:
                br.ReadBytes(br.ReadInt32() * sizeof(ushort)); break; // 跳过 [长度 × 2] 字节
            case TypeCode.UInt32Array:
                br.ReadBytes(br.ReadInt32() * sizeof(uint)); break;   // 跳过 [长度 × 4] 字节
            case TypeCode.UInt64Array:
                br.ReadBytes(br.ReadInt32() * sizeof(ulong)); break;  // 跳过 [长度 × 8] 字节
            case TypeCode.SByteArray:
                br.ReadBytes(br.ReadInt32() * sizeof(sbyte)); break;  // 跳过 [长度 × 1] 字节
            case TypeCode.DateOnlyArray:
                br.ReadBytes(br.ReadInt32() * sizeof(int)); break;    // 跳过 [长度 × 4] 字节（DayNumber）
            case TypeCode.TimeOnlyArray:
                br.ReadBytes(br.ReadInt32() * sizeof(long)); break;   // 跳过 [长度 × 8] 字节（Ticks）
            case TypeCode.Int16Array:
                br.ReadBytes(br.ReadInt32() * sizeof(short)); break;  // 跳过 [长度 × 2] 字节
            case TypeCode.Int32Array:
                br.ReadBytes(br.ReadInt32() * sizeof(int)); break;    // 跳过 [长度 × 4] 字节
            case TypeCode.Int64Array:
                br.ReadBytes(br.ReadInt32() * sizeof(long)); break;   // 跳过 [长度 × 8] 字节
            case TypeCode.BooleanArray:
                br.ReadBytes(br.ReadInt32()); break;                  // 跳过 [长度 × 1] 字节
            case TypeCode.HalfArray:
                br.ReadBytes(br.ReadInt32() * 2); break;              // 跳过 [长度 × 2] 字节
        }
    }

    /// <summary>
    /// 从二进制流中读取浮点数组。
    /// <para>
    /// 使用 <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/> 将原始字节
    /// 零拷贝转换为 <c>float</c>，避免逐元素读取的开销。
    /// </para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的浮点数组。</returns>
    private static float[] ReadFloatArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        var bytes = br.ReadBytes(len * sizeof(float));
        var floats = new float[len];
        // 利用 MemoryMarshal 将 byte[] 重新解释为 float[]，零拷贝、无精度损失
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(floats);
        return floats;
    }

    /// <summary>
    /// 从二进制流中读取字符串数组。
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的字符串数组。</returns>
    private static string[] ReadStringArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        var arr = new string[len];
        for (int i = 0; i < len; i++) arr[i] = br.ReadString();
        return arr;
    }

    /// <summary>
    /// 从二进制流中读取 <see cref="System.DateTimeOffset"/>。
    /// <para>
    /// 存储格式为 [Ticks int64] + [OffsetMinutes int16]，共 10 字节。
    /// </para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的 <see cref="System.DateTimeOffset"/> 值。</returns>
    private static DateTimeOffset ReadDateTimeOffset(BinaryReader br)
    {
        var ticks = br.ReadInt64();
        var offsetMinutes = br.ReadInt16();
        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
    }

    /// <summary>
    /// 从二进制流中读取字节数组。
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的字节数组。</returns>
    private static byte[] ReadByteArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        return br.ReadBytes(len);
    }

    /// <summary>
    /// 从二进制流中读取双精度浮点数组。
    /// <para>
    /// 使用 <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/> 将原始字节
    /// 零拷贝转换为 <c>double</c>，避免逐元素读取的开销。
    /// </para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的双精度浮点数组。</returns>
    private static double[] ReadDoubleArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        var bytes = br.ReadBytes(len * sizeof(double));
        var doubles = new double[len];
        MemoryMarshal.Cast<byte, double>(bytes).CopyTo(doubles);
        return doubles;
    }

    /// <summary>
    /// 从二进制流中读取可按位复制（blittable）的非托管类型数组。
    /// <para>
    /// 存储格式为 [长度 int32] + [原始字节]，使用 <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/>
    /// 将字节零拷贝重解释为 <typeparamref name="T"/>，适用于 <c>short/int/long/ushort/uint/ulong/sbyte/Half</c> 等定长非托管类型。
    /// </para>
    /// </summary>
    /// <typeparam name="T">目标非托管元素类型。</typeparam>
    /// <param name="br">源二进制读取器。</param>
    /// <param name="elementSize">单个元素占用的字节数。</param>
    /// <returns>反序列化后的数组。</returns>
    private static T[] ReadBlittableArray<T>(BinaryReader br, int elementSize) where T : unmanaged
    {
        var len = br.ReadInt32();
        var bytes = br.ReadBytes(len * elementSize);
        var arr = new T[len];
        MemoryMarshal.Cast<byte, T>(bytes).CopyTo(arr);
        return arr;
    }

    /// <summary>
    /// 从二进制流中读取 <see cref="System.DateOnly"/> 数组。
    /// <para>存储格式为 [长度 int32] + [逐元素 DayNumber int32]。</para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的 <see cref="System.DateOnly"/> 数组。</returns>
    private static DateOnly[] ReadDateOnlyArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        var arr = new DateOnly[len];
        for (int i = 0; i < len; i++) arr[i] = DateOnly.FromDayNumber(br.ReadInt32());
        return arr;
    }

    /// <summary>
    /// 从二进制流中读取 <see cref="System.TimeOnly"/> 数组。
    /// <para>存储格式为 [长度 int32] + [逐元素 Ticks int64]。</para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的 <see cref="System.TimeOnly"/> 数组。</returns>
    private static TimeOnly[] ReadTimeOnlyArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        var arr = new TimeOnly[len];
        for (int i = 0; i < len; i++) arr[i] = new TimeOnly(br.ReadInt64());
        return arr;
    }

    /// <summary>
    /// 从二进制流中读取布尔数组。
    /// <para>存储格式为 [长度 int32] + [每元素 1 字节]，逐元素读取以保证跨平台一致。</para>
    /// </summary>
    /// <param name="br">源二进制读取器。</param>
    /// <returns>反序列化后的布尔数组。</returns>
    private static bool[] ReadBooleanArray(BinaryReader br)
    {
        var len = br.ReadInt32();
        var arr = new bool[len];
        for (int i = 0; i < len; i++) arr[i] = br.ReadBoolean();
        return arr;
    }

    #endregion

    #region 类型强转

    /// <summary>
    /// 将从文件中读取的值自动强转为当前属性所需的目标类型。
    /// <para>
    /// 当实体类的属性类型发生变更（如 <c>int → long</c>、<c>float → double</c>）后，
    /// 旧文件中存储的值类型与当前属性类型不一致。此方法尝试进行安全的隐式转换，
    /// 覆盖以下场景：
    /// <list type="bullet">
    ///   <item>数值类型之间的拓宽转换（byte→short→int→long→float→double→decimal）</item>
    ///   <item><see cref="Half"/> 与 float/double 之间的互转</item>
    ///   <item><see cref="DateTime"/> → <see cref="DateTimeOffset"/> 的升级</item>
    ///   <item><c>float[]</c> ↔ <c>double[]</c> 向量数组的元素级转换</item>
    /// </list>
    /// 不可转换时返回 <see langword="null"/>，调用方将跳过赋值（属性保留默认值）。
    /// </para>
    /// </summary>
    /// <param name="value">从文件中读取的原始值（非 null）。</param>
    /// <param name="targetType">当前 CLR 属性的目标类型。</param>
    /// <returns>转换后的值；无法转换时返回 <see langword="null"/>。</returns>
    private static object? CoerceValue(object value, Type targetType)
    {
        // ── Half 特殊处理（Convert.ChangeType 不支持 Half） ──
        if (value is Half h)
        {
            if (targetType == typeof(float)) return (float)h;
            if (targetType == typeof(double)) return (double)h;
            return null;
        }
        if (targetType == typeof(Half))
        {
            if (value is float f) return (Half)f;
            if (value is double d) return (Half)d;
            return null;
        }

        // ── DateTime → DateTimeOffset 升级 ──
        if (value is DateTime dt && targetType == typeof(DateTimeOffset))
            return new DateTimeOffset(dt);

        // ── DateOnly / TimeOnly 与 DateTime / TimeSpan 的自然互转 ──
        if (value is DateTime dtToDateOnly && targetType == typeof(DateOnly))
            return DateOnly.FromDateTime(dtToDateOnly);
        if (value is DateOnly dateOnly && targetType == typeof(DateTime))
            return dateOnly.ToDateTime(TimeOnly.MinValue);
        if (value is DateTime dtToTimeOnly && targetType == typeof(TimeOnly))
            return TimeOnly.FromDateTime(dtToTimeOnly);
        if (value is TimeOnly timeOnly && targetType == typeof(TimeSpan))
            return timeOnly.ToTimeSpan();
        if (value is TimeSpan tsToTimeOnly && targetType == typeof(TimeOnly))
            return TimeOnly.FromTimeSpan(tsToTimeOnly);

        // ── TimeSpan → long (Ticks) 或反向 ──
        if (value is TimeSpan ts && targetType == typeof(long))
            return ts.Ticks;
        if (value is long ticks && targetType == typeof(TimeSpan))
            return TimeSpan.FromTicks(ticks);

        // ── float[] ↔ double[] 向量数组互转 ──
        if (value is float[] fa && targetType == typeof(double[]))
            return Array.ConvertAll(fa, x => (double)x);
        if (value is double[] da && targetType == typeof(float[]))
            return Array.ConvertAll(da, x => (float)x);

        // ── 通用数值拓宽（int→long, float→double, byte→int 等） ──
        try
        {
            return Convert.ChangeType(value, targetType);
        }
        catch
        {
            // 类型完全不兼容，放弃转换
            return null;
        }
    }

    #endregion

    /// <summary>
    /// 获取指定类型的所有公共可读写实例属性，并按名称升序排列。
    /// <para>
    /// 按名称排序确保属性在二进制流中的读写顺序始终一致，
    /// 不受编译器或反射返回顺序变化的影响。
    /// </para>
    /// </summary>
    /// <param name="type">要获取属性的 CLR 类型。</param>
    /// <returns>按名称排序后的 <see cref="PropertyInfo"/> 数组。</returns>
    private static PropertyInfo[] GetSortedProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => p.CanRead && p.CanWrite)
               .OrderBy(p => p.Name)
               .ToArray();
}

/// <summary>
/// v4 footer 中的单段元数据。
/// <para>
/// Schema v1 字段：<c>TypeName, Offset, Length, EntityCount, Crc32</c>。
/// Schema v2 追加字段：<c>Kind, FieldName, Dim, FirstId</c>。
/// </para>
/// </summary>
internal readonly record struct FooterEntry(
    string TypeName,
    long Offset,
    long Length,
    int EntityCount,
    uint Crc32,
    BinaryStorageProvider.SegmentKind Kind = BinaryStorageProvider.SegmentKind.Mixed,
    string? FieldName = null,
    int Dim = 0,
    int FirstId = -1);