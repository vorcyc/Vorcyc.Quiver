using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Vorcyc.Quiver.Migration;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 提供基于 XML 的向量集合持久化实现。
/// <para>
/// 该实现使用 <see cref="XmlWriter"/> 与 <see cref="XmlReader"/> 顺序写入和读取 XML，
/// 避免 <see cref="System.Xml.Linq.XDocument"/> 在大文件场景下将整棵文档树加载到内存中。
/// </para>
/// <para>
/// 根元素为 <c>&lt;QuiverDb&gt;</c>，每个集合对应一个 <c>&lt;Set&gt;</c> 元素，
/// 每个实体对应一个 <c>&lt;Entity&gt;</c> 元素，属性使用同名子元素表示。
/// </para>
/// </summary>
/// <seealso cref="IStorageProvider"/>
/// <seealso cref="ExportFormat.Xml"/>
internal class XmlExportProvider : IStorageProvider
{
    /// <summary>
    /// 当前 XML 存储格式的架构版本号。
    /// </summary>
    private const int CurrentSchemaVersion = 4;

    /// <summary>
    /// 将所有向量集合以 XML 格式异步写入指定文件。
    /// <para>
    /// 该方法按集合、实体、属性的顺序逐步写出 XML 内容，不会在内存中构建完整文档树，
    /// 更适合大文件导出场景。
    /// </para>
    /// </summary>
    /// <param name="filePath">目标文件路径。若文件已存在将被覆盖。</param>
    /// <param name="sets">
    /// 待保存的集合字典。
    /// <para>
    /// 键为集合名称；值为包含实体类型与实体列表的元组。
    /// </para>
    /// </param>
    /// <returns>表示异步保存操作的任务。</returns>
    public async Task SaveAsync(string filePath, IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets)
    {
        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new UTF8Encoding(false)
        };

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        await using var writer = XmlWriter.Create(stream, settings);

        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "QuiverDb", null);
        await writer.WriteAttributeStringAsync(null, "version", null, CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));

        foreach (var (setName, (entityType, entities)) in sets)
        {
            await writer.WriteStartElementAsync(null, "Set", null);
            await writer.WriteAttributeStringAsync(null, "type", null, setName);
            await writer.WriteAttributeStringAsync(null, "count", null, entities.Count.ToString(CultureInfo.InvariantCulture));

            // 按名称稳定排序，保证同一类型生成的字段顺序一致。
            var props = GetSortedProperties(entityType);

            foreach (var entity in entities)
            {
                await writer.WriteStartElementAsync(null, "Entity", null);

                foreach (var prop in props)
                    await WritePropertyAsync(writer, prop, prop.GetValue(entity));

                await writer.WriteEndElementAsync();
            }

            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>
    /// 从指定 XML 文件异步加载所有向量集合。
    /// <para>
    /// 该方法使用 <see cref="XmlReader"/> 顺序读取文档，仅保留当前正在处理的节点内容，
    /// 因而比基于 DOM 的读取方式更适合大文件输入。
    /// </para>
    /// </summary>
    /// <param name="filePath">要读取的 XML 文件路径。</param>
    /// <param name="typeMap">
    /// 集合名称到 CLR 类型的映射字典。
    /// <para>
    /// 若 XML 中某个集合名称未在此映射表中注册，则该集合会被跳过。
    /// </para>
    /// </param>
    /// <param name="migrationRules">
    /// 可选的架构迁移规则字典。
    /// <para>
    /// 键为集合名称，值为对应的迁移规则。若存在属性重命名规则，
    /// 读取时会将旧元素名映射为当前属性名。
    /// </para>
    /// </param>
    /// <returns>
    /// 包含所有成功加载集合的字典。
    /// <para>
    /// 字典键为集合名称，值为该集合中反序列化后的实体对象列表。
    /// </para>
    /// </returns>
    public async Task<Dictionary<string, List<object>>> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        var result = new Dictionary<string, List<object>>();

        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var reader = XmlReader.Create(stream, settings);

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "Set")
                continue;

            var setName = reader.GetAttribute("type");
            if (string.IsNullOrEmpty(setName))
                continue;

            if (!typeMap.TryGetValue(setName, out var entityType))
            {
                // 对当前版本未知的集合直接跳过，保持前向兼容。
                await SkipElementAsync(reader);
                continue;
            }

            SchemaMigrationRule? rule = null;
            migrationRules?.TryGetValue(setName, out rule);

            var props = GetSortedProperties(entityType);
            var propMap = props.ToDictionary(p => p.Name);
            var entities = new List<object>();

            if (reader.IsEmptyElement)
            {
                result[setName] = entities;
                continue;
            }

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "Set")
                    break;

                if (reader.NodeType != XmlNodeType.Element || reader.Name != "Entity")
                    continue;

                var entity = Activator.CreateInstance(entityType)!;

                if (!reader.IsEmptyElement)
                {
                    await reader.ReadAsync();
                    while (true)
                    {
                        if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "Entity")
                            break;

                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            var elementName = reader.Name;
                            var targetName = ResolvePropertyName(elementName, rule);

                            if (!propMap.TryGetValue(targetName, out var prop))
                            {
                                await SkipElementAsync(reader);
                                continue;
                            }

                            var isNull = reader.GetAttribute("null") != null;
                            if (isNull)
                            {
                                await SkipElementAsync(reader);
                                continue;
                            }

                            var value = await ReadElementValueAsync(reader, prop.PropertyType);
                            prop.SetValue(entity, value);
                            continue;
                        }

                        if (!await reader.ReadAsync())
                            break;
                    }
                }

                entities.Add(entity);
            }

            result[setName] = entities;
        }

        return result;
    }

    /// <summary>
    /// 根据迁移规则将 XML 元素名解析为目标属性名。
    /// </summary>
    /// <param name="xmlName">XML 中读取到的元素名称。</param>
    /// <param name="rule">当前集合对应的迁移规则。</param>
    /// <returns>最终应写入对象属性的名称。</returns>
    private static string ResolvePropertyName(string xmlName, SchemaMigrationRule? rule)
    {
        if (rule is null)
            return xmlName;

        foreach (var (oldName, newName) in rule.PropertyRenames)
        {
            if (oldName == xmlName)
                return newName;
        }

        return xmlName;
    }

    /// <summary>
    /// 将单个属性值写入为 XML 元素。
    /// </summary>
    /// <param name="writer">目标 XML 写入器。</param>
    /// <param name="prop">属性元数据。</param>
    /// <param name="value">属性值，可能为 <see langword="null"/>。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    private static async Task WritePropertyAsync(XmlWriter writer, PropertyInfo prop, object? value)
    {
        await writer.WriteStartElementAsync(null, prop.Name, null);

        if (value == null)
        {
            await writer.WriteAttributeStringAsync(null, "null", null, "true");
            await writer.WriteEndElementAsync();
            return;
        }

        var type = prop.PropertyType;

        if (type == typeof(float[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((float[])value).AsSpan())));
        else if (type == typeof(double[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((double[])value).AsSpan())));
        else if (type == typeof(Half[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((Half[])value).AsSpan())));
        else if (type == typeof(short[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((short[])value).AsSpan())));
        else if (type == typeof(int[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((int[])value).AsSpan())));
        else if (type == typeof(long[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((long[])value).AsSpan())));
        else if (type == typeof(ushort[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((ushort[])value).AsSpan())));
        else if (type == typeof(uint[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((uint[])value).AsSpan())));
        else if (type == typeof(ulong[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((ulong[])value).AsSpan())));
        else if (type == typeof(sbyte[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((sbyte[])value).AsSpan())));
        else if (type == typeof(bool[]))
            await writer.WriteStringAsync(Convert.ToBase64String(MemoryMarshal.AsBytes(((bool[])value).AsSpan())));
        else if (type == typeof(byte[]))
            await writer.WriteStringAsync(Convert.ToBase64String((byte[])value));
        else if (type == typeof(string[]))
        {
            foreach (var s in (string[])value)
            {
                await writer.WriteStartElementAsync(null, "Item", null);
                await writer.WriteStringAsync(SanitizeXmlString(s));
                await writer.WriteEndElementAsync();
            }
        }
        else if (type == typeof(DateOnly[]))
        {
            foreach (var d in (DateOnly[])value)
            {
                await writer.WriteStartElementAsync(null, "Item", null);
                await writer.WriteStringAsync(d.ToString("O", CultureInfo.InvariantCulture));
                await writer.WriteEndElementAsync();
            }
        }
        else if (type == typeof(TimeOnly[]))
        {
            foreach (var t in (TimeOnly[])value)
            {
                await writer.WriteStartElementAsync(null, "Item", null);
                await writer.WriteStringAsync(t.ToString("O", CultureInfo.InvariantCulture));
                await writer.WriteEndElementAsync();
            }
        }
        else if (type == typeof(DateTime))
            await writer.WriteStringAsync(((DateTime)value).ToString("O", CultureInfo.InvariantCulture));
        else if (type == typeof(DateTimeOffset))
            await writer.WriteStringAsync(((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture));
        else if (type == typeof(DateOnly))
            await writer.WriteStringAsync(((DateOnly)value).ToString("O", CultureInfo.InvariantCulture));
        else if (type == typeof(TimeOnly))
            await writer.WriteStringAsync(((TimeOnly)value).ToString("O", CultureInfo.InvariantCulture));
        else if (type == typeof(TimeSpan))
            await writer.WriteStringAsync(((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture));
        else if (type == typeof(Half))
            await writer.WriteStringAsync(((Half)value).ToString("R", CultureInfo.InvariantCulture));
        else if (type == typeof(char))
            await writer.WriteStringAsync(((ushort)(char)value).ToString(CultureInfo.InvariantCulture));
        else if (type == typeof(string))
            await writer.WriteStringAsync(SanitizeXmlString((string)value));
        else
            await writer.WriteStringAsync(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

        await writer.WriteEndElementAsync();
    }

    /// <summary>
    /// 读取当前属性元素的内容，并转换为目标 CLR 类型。
    /// </summary>
    /// <param name="reader">当前定位在属性元素上的 XML 读取器。</param>
    /// <param name="type">目标属性类型。</param>
    /// <returns>转换后的属性值。</returns>
    private static async Task<object?> ReadElementValueAsync(XmlReader reader, Type type)
    {
        if (type == typeof(string))
            return await reader.ReadElementContentAsStringAsync();

        if (type == typeof(string[]))
            return await ReadItemArrayAsync(reader, s => s);

        if (type == typeof(DateOnly[]))
            return await ReadItemArrayAsync(reader, s => DateOnly.Parse(s, CultureInfo.InvariantCulture));

        if (type == typeof(TimeOnly[]))
            return await ReadItemArrayAsync(reader, s => TimeOnly.Parse(s, CultureInfo.InvariantCulture));

        var text = await reader.ReadElementContentAsStringAsync();

        if (type == typeof(int)) return int.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return long.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return float.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return bool.Parse(text);
        if (type == typeof(Guid)) return Guid.Parse(text);
        if (type == typeof(decimal)) return decimal.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(short)) return short.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(byte)) return byte.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(Half)) return Half.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateOnly)) return DateOnly.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(TimeOnly)) return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(ushort)) return ushort.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(uint)) return uint.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(ulong)) return ulong.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(sbyte)) return sbyte.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(char)) return (char)ushort.Parse(text, CultureInfo.InvariantCulture);

        if (type == typeof(float[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new float[b.Length / sizeof(float)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(double[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new double[b.Length / sizeof(double)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(Half[]))
        {
            var b = Convert.FromBase64String(text);
            return MemoryMarshal.Cast<byte, Half>(new ReadOnlySpan<byte>(b)).ToArray();
        }

        if (type == typeof(short[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new short[b.Length / sizeof(short)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(int[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new int[b.Length / sizeof(int)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(long[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new long[b.Length / sizeof(long)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(ushort[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new ushort[b.Length / sizeof(ushort)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(uint[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new uint[b.Length / sizeof(uint)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(ulong[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new ulong[b.Length / sizeof(ulong)];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(sbyte[]))
        {
            var b = Convert.FromBase64String(text);
            return MemoryMarshal.Cast<byte, sbyte>(new ReadOnlySpan<byte>(b)).ToArray();
        }

        if (type == typeof(bool[]))
        {
            var b = Convert.FromBase64String(text);
            var a = new bool[b.Length];
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }

        if (type == typeof(byte[]))
            return Convert.FromBase64String(text);

        return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 读取由多个 <c>&lt;Item&gt;</c> 子元素组成的数组值。
    /// </summary>
    /// <typeparam name="T">数组元素类型。</typeparam>
    /// <param name="reader">当前定位在数组属性元素上的 XML 读取器。</param>
    /// <param name="converter">将文本转换为目标类型的委托。</param>
    /// <returns>解析得到的数组。</returns>
    private static async Task<T[]> ReadItemArrayAsync<T>(XmlReader reader, Func<string, T> converter)
    {
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync();
            return [];
        }

        var items = new List<T>();
        var containerName = reader.Name;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == containerName)
                break;

            if (reader.NodeType != XmlNodeType.Element || reader.Name != "Item")
                continue;

            var value = await reader.ReadElementContentAsStringAsync();
            items.Add(converter(value));
        }

        return items.ToArray();
    }

    /// <summary>
    /// 跳过当前元素及其全部子节点。
    /// </summary>
    /// <param name="reader">当前定位在元素起始节点上的 XML 读取器。</param>
    /// <returns>表示异步跳过操作的任务。</returns>
    private static async Task SkipElementAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync();
            return;
        }

        var depth = reader.Depth;
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
        }
    }

    /// <summary>
    /// 获取指定类型的所有公共实例可读写属性，并按名称升序排列。
    /// </summary>
    /// <param name="type">目标 CLR 类型。</param>
    /// <returns>按属性名称排序后的属性数组。</returns>
    private static PropertyInfo[] GetSortedProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => p.CanRead && p.CanWrite)
               .OrderBy(p => p.Name)
               .ToArray();

    /// <summary>
    /// 移除 XML 1.0 不允许出现在文本节点中的字符。
    /// </summary>
    /// <param name="value">待清理的字符串。</param>
    /// <returns>移除非法字符后的字符串。</returns>
    private static string SanitizeXmlString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        StringBuilder? sb = null;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool valid = c == '\t' || c == '\n' || c == '\r'
                         || (c >= 0x20 && c <= 0xD7FF)
                         || (c >= 0xE000 && c <= 0xFFFD);

            if (!valid)
            {
                sb ??= new StringBuilder(value, 0, i, value.Length);
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? value;
    }
}