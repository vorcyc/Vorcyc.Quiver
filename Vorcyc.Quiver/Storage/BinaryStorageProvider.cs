using System.Reflection;
using System.Runtime.InteropServices;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 二进制格式的存储提供者，实现 <see cref="IStorageProvider"/> 接口。
/// <para>
/// 采用自定义紧凑二进制协议，具有以下特点：
/// <list type="bullet">
///   <item>文件体积最小、读写性能最优，适合生产环境。</item>
///   <item>向量数据使用零拷贝 <see cref="MemoryMarshal.AsBytes{T}(Span{T})"/> 直写，无精度损失。</item>
///   <item>支持前向兼容——加载时可跳过已删除或未知属性的字节。</item>
/// </list>
/// </para>
/// <para>
/// 二进制文件格式概览：
/// <code>
/// [Magic 4B] [SetCount int32]
///   ┗ 每个 Set：[TypeName string] [PropCount int32]
///       ┗ 每个属性描述符：[PropName string] [TypeCode byte]
///       ┗ [EntityCount int32]
///           ┗ 每个实体：按属性描述符顺序写入各字段值
/// </code>
/// </para>
/// </summary>
/// <seealso cref="IStorageProvider"/>
/// <seealso cref="StorageFormat.Binary"/>
internal class BinaryStorageProvider : IStorageProvider
{
    /// <summary>
    /// 文件魔术字节，用于标识文件格式和版本。
    /// <para>值为 <c>"QDB\x01"</c>（ASCII），其中 <c>\x01</c> 表示版本 1。</para>
    /// </summary>
    private static readonly byte[] Magic = "QDB\x01"u8.ToArray();

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
    }

    /// <summary>
    /// 将 CLR <see cref="Type"/> 映射到对应的 <see cref="TypeCode"/>。
    /// </summary>
    /// <param name="type">要映射的属性类型。</param>
    /// <returns>对应的二进制类型码。</returns>
    /// <exception cref="NotSupportedException">当传入的类型不在支持列表中时抛出。</exception>
    private static TypeCode GetTypeCode(Type type) => type switch
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
        _ => throw new NotSupportedException($"Binary serialization of '{type.Name}' is not supported.")
    };

    #endregion

    #region Save

    /// <summary>
    /// 将所有向量集合以二进制格式异步持久化到指定文件。
    /// </summary>
    /// <param name="filePath">目标文件的绝对或相对路径。文件不存在时自动创建，已存在时覆盖。</param>
    /// <param name="sets">
    /// 要保存的向量集合字典。键为类型名称，值为元组 <c>(Type, List&lt;object&gt;)</c>，
    /// 包含实体的 CLR 类型及实体列表。
    /// </param>
    /// <returns>表示异步写入操作的任务。</returns>
    public async Task SaveAsync(string filePath, IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets)
    {
        await Task.Run(() =>
        {
            // 使用 64KB 缓冲区提升顺序写入性能
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            using var bw = new BinaryWriter(fs);

            // 写入魔术字节和集合数量（文件头）
            bw.Write(Magic);
            bw.Write(sets.Count);

            foreach (var (typeName, (type, entities)) in sets)
            {
                // 写入类型名称，用于加载时匹配 CLR 类型
                bw.Write(typeName);

                // 属性描述符（名称 + 类型码），按名称排序保证读写顺序一致
                var props = GetSortedProperties(type);
                bw.Write(props.Length);
                foreach (var prop in props)
                {
                    bw.Write(prop.Name);
                    bw.Write((byte)GetTypeCode(prop.PropertyType));
                }

                // 逐个写入实体数据：按描述符顺序序列化每个属性值
                bw.Write(entities.Count);
                foreach (var entity in entities)
                    foreach (var prop in props)
                        WriteValue(bw, prop.PropertyType, prop.GetValue(entity));
            }
        });
    }

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
    }

    #endregion

    #region Load

    /// <summary>
    /// 从指定二进制文件异步加载所有向量集合。
    /// <para>
    /// 加载流程：
    /// <list type="number">
    ///   <item>校验魔术字节，确保文件格式正确。</item>
    ///   <item>读取属性描述符，与当前 CLR 类型的属性进行匹配（支持通过迁移规则重命名映射）。</item>
    ///   <item>逐实体反序列化属性值；遇到已删除或未知属性时跳过对应字节（前向兼容）。</item>
    /// </list>
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
    public async Task<Dictionary<string, List<object>>> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        var result = new Dictionary<string, List<object>>();

        await Task.Run(() =>
        {
            // 使用 64KB 缓冲区提升顺序读取性能
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var br = new BinaryReader(fs);

            // 校验文件头魔术字节
            var magic = br.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("Invalid QuiverDb binary file.");

            var setCount = br.ReadInt32();

            for (int s = 0; s < setCount; s++)
            {
                var typeName = br.ReadString();

                // 读取属性描述符：文件中记录的属性名称和类型码
                var propCount = br.ReadInt32();
                var descriptors = new (string Name, TypeCode Code)[propCount];
                for (int p = 0; p < propCount; p++)
                    descriptors[p] = (br.ReadString(), (TypeCode)br.ReadByte());

                // 尝试将文件中的类型名匹配到当前程序的 CLR 类型
                var hasType = typeMap.TryGetValue(typeName, out var type);

                // 获取当前类型的迁移规则（如果有）
                SchemaMigrationRule? rule = null;
                if (hasType && migrationRules != null)
                    migrationRules.TryGetValue(typeName, out rule);

                // 建立描述符索引到 PropertyInfo 的映射，用于反射赋值
                // 如果存在重命名规则，先将文件中的旧属性名映射到新属性名
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
                var entities = new List<object>(entityCount);

                for (int e = 0; e < entityCount; e++)
                {
                    // 通过反射创建实体实例
                    var entity = hasType ? Activator.CreateInstance(type!)! : null;

                    for (int p = 0; p < propCount; p++)
                    {
                        var prop = propMap?[p];
                        if (prop != null && entity != null)
                        {
                            // 正常读取并赋值
                            var value = ReadValue(br, descriptors[p].Code);
                            if (value != null)
                            {
                                // 文件中的类型与当前属性类型不一致时，尝试自动强转（隐式迁移）
                                if (value.GetType() != prop.PropertyType)
                                    value = CoerceValue(value, prop.PropertyType);
                                if (value != null)
                                    prop.SetValue(entity, value);
                            }
                        }
                        else
                        {
                            // 属性已删除或类型未知——跳过对应字节，保持流位置正确（前向兼容）
                            SkipValue(br, descriptors[p].Code);
                        }
                    }

                    if (entity != null) entities.Add(entity);
                }

                if (hasType) result[typeName] = entities;
            }
        });

        return result;
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