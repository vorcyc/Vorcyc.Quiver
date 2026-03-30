using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Vorcyc.Quiver;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// XML 格式的存储提供者，实现 <see cref="IStorageProvider"/> 接口。
/// <para>
/// 使用 <see cref="System.Xml.Linq"/> 进行序列化和反序列化，具有以下特点：
/// <list type="bullet">
///   <item>可读性好，生成的 XML 文件结构清晰，便于人工审查。</item>
///   <item>向量数据使用 Base64 编码，紧凑且无精度损失。</item>
///   <item>日期时间使用 ISO 8601 往返格式（<c>"O"</c>），保证跨时区精度。</item>
///   <item>数值类型使用 <see cref="CultureInfo.InvariantCulture"/>，保证跨区域一致性。</item>
/// </list>
/// </para>
/// <para>
/// XML 文件结构概览：
/// <code>
/// &lt;QuiverDb version="1"&gt;
///   &lt;Set type="FaceFeature" count="N"&gt;
///     &lt;Entity&gt;
///       &lt;Id&gt;...&lt;/Id&gt;
///       &lt;Embedding&gt;Base64...&lt;/Embedding&gt;
///     &lt;/Entity&gt;
///     ...
///   &lt;/Set&gt;
/// &lt;/QuiverDb&gt;
/// </code>
/// </para>
/// </summary>
/// <seealso cref="IStorageProvider"/>
/// <seealso cref="StorageFormat.Xml"/>
internal class XmlStorageProvider : IStorageProvider
{
    /// <summary>
    /// 将所有向量集合以 XML 格式异步持久化到指定文件。
    /// <para>
    /// 输出为 UTF-8 编码的 XML 文档，根元素为 <c>&lt;QuiverDb&gt;</c>，
    /// 每个向量集合对应一个 <c>&lt;Set&gt;</c> 子元素，实体按属性名称排序写入。
    /// </para>
    /// </summary>
    /// <param name="filePath">目标文件的绝对或相对路径。文件不存在时自动创建，已存在时覆盖。</param>
    /// <param name="sets">
    /// 要保存的向量集合字典。键为类型名称，值为元组 <c>(Type, List&lt;object&gt;)</c>，
    /// 包含实体的 CLR 类型及实体列表。
    /// </param>
    /// <returns>表示异步写入操作的任务。</returns>
    public async Task SaveAsync(string filePath, IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets)
    {
        // 创建根元素，附带版本号属性
        var root = new XElement("QuiverDb", new XAttribute("version", 1));

        foreach (var (typeName, (type, entities)) in sets)
        {
            // 每个向量集合对应一个 <Set> 元素，记录类型名称和实体数量
            var setEl = new XElement("Set",
                new XAttribute("type", typeName),
                new XAttribute("count", entities.Count));

            // 获取按名称排序的属性，保证 XML 中字段顺序一致
            var props = GetSortedProperties(type);

            foreach (var entity in entities)
            {
                // 每个实体对应一个 <Entity> 元素，逐属性写入子元素
                var entityEl = new XElement("Entity");
                foreach (var prop in props)
                    entityEl.Add(PropertyToXml(prop, prop.GetValue(entity)));
                setEl.Add(entityEl);
            }
            root.Add(setEl);
        }

        // 构建完整 XML 文档（声明 UTF-8 编码），异步写入文件
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        await doc.SaveAsync(writer, SaveOptions.None, CancellationToken.None);
    }

    /// <summary>
    /// 从指定 XML 文件异步加载所有向量集合。
    /// <para>
    /// 加载流程：
    /// <list type="number">
    ///   <item>异步读取并解析 XML 文档。</item>
    ///   <item>遍历每个 <c>&lt;Set&gt;</c> 元素，通过 <paramref name="typeMap"/> 匹配 CLR 类型。</item>
    ///   <item>若存在迁移规则，在查找属性元素时同时检查旧属性名（反向重命名映射）。</item>
    ///   <item>逐实体反序列化属性值；缺失的属性保持默认值，未匹配的类型被跳过（前向兼容）。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="filePath">要读取的 XML 文件路径。</param>
    /// <param name="typeMap">
    /// 类型名称到 CLR <see cref="Type"/> 的映射字典。
    /// <para>文件中存在但字典中缺失的类型将被跳过，确保前向兼容。</para>
    /// </param>
    /// <param name="migrationRules">
    /// 可选的 Schema 迁移规则字典。键为类型全名，值为迁移规则。
    /// 包含属性重命名映射，加载时将 XML 中的旧元素名映射到当前属性。
    /// </param>
    /// <returns>加载后的向量集合字典。键为类型名称，值为反序列化后的实体对象列表。</returns>
    public async Task<Dictionary<string, List<object>>> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        var result = new Dictionary<string, List<object>>();

        // 异步读取并解析 XML 文档
        await using var stream = File.OpenRead(filePath);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);

        // 遍历根元素下的每个 <Set> 子元素
        foreach (var setEl in doc.Root!.Elements("Set"))
        {
            // 从 type 属性获取类型名称，匹配 CLR 类型
            var typeName = setEl.Attribute("type")!.Value;
            if (!typeMap.TryGetValue(typeName, out var type)) continue;

            // 获取当前类型的迁移规则（如果有）
            SchemaMigrationRule? rule = null;
            if (migrationRules != null)
                migrationRules.TryGetValue(typeName, out rule);

            var props = GetSortedProperties(type);
            var entities = new List<object>();

            // 遍历 <Entity> 元素，反射创建实体并逐属性赋值
            foreach (var entityEl in setEl.Elements("Entity"))
            {
                var entity = Activator.CreateInstance(type)!;
                foreach (var prop in props)
                {
                    var propEl = entityEl.Element(prop.Name);

                    // 当前属性名未找到元素时，尝试通过反向重命名映射查找旧属性名的元素
                    if (propEl == null && rule?.ReverseRenames.TryGetValue(prop.Name, out var oldName) == true)
                        propEl = entityEl.Element(oldName);

                    // 跳过缺失的属性或标记为 null 的属性
                    if (propEl == null || propEl.Attribute("null") != null) continue;
                    prop.SetValue(entity, XmlToValue(propEl, prop.PropertyType));
                }
                entities.Add(entity);
            }
            result[typeName] = entities;
        }

        return result;
    }

    #region 序列化辅助

    /// <summary>
    /// 将单个属性值序列化为 XML 元素。
    /// <para>
    /// 不同类型的序列化策略：
    /// <list type="bullet">
    ///   <item><see langword="null"/>：添加 <c>null="true"</c> 属性标记。</item>
    ///   <item><c>float[]</c>：使用 <see cref="MemoryMarshal.AsBytes{T}(Span{T})"/>
    ///     零拷贝转换后进行 Base64 编码，紧凑且无精度损失。</item>
    ///   <item><c>string[]</c>：每个元素写入一个 <c>&lt;Item&gt;</c> 子元素。</item>
    ///   <item><see cref="DateTime"/>：使用 ISO 8601 往返格式（<c>"O"</c>）。</item>
    ///   <item>其他类型：使用 <see cref="Convert.ToString(object, IFormatProvider)"/>
    ///     配合 <see cref="CultureInfo.InvariantCulture"/>。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="prop">要序列化的属性元数据。</param>
    /// <param name="value">属性值，可能为 <see langword="null"/>。</param>
    /// <returns>表示该属性的 <see cref="XElement"/>。</returns>
    private static XElement PropertyToXml(PropertyInfo prop, object? value)
    {
        var el = new XElement(prop.Name);

        // null 值：添加标记属性后直接返回空元素
        if (value == null) { el.Add(new XAttribute("null", true)); return el; }

        var type = prop.PropertyType;

        if (type == typeof(float[]))
        {
            // 向量用 Base64 编码：先零拷贝转为字节，再编码为字符串，紧凑且无精度损失
            var arr = (float[])value;
            el.Value = Convert.ToBase64String(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else if (type == typeof(string[]))
        {
            // 字符串数组：每个元素对应一个 <Item> 子元素
            foreach (var s in (string[])value)
                el.Add(new XElement("Item", s));
        }
        else if (type == typeof(DateTime))
            // 日期时间使用 ISO 8601 往返格式，保证跨时区精度
            el.Value = ((DateTime)value).ToString("O");
        else
            // 其他类型：使用不变区域性格式化，保证跨区域一致性
            el.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        return el;
    }

    /// <summary>
    /// 将 XML 元素反序列化为对应的 CLR 属性值。
    /// <para>
    /// 不同类型的反序列化策略：
    /// <list type="bullet">
    ///   <item><c>float[]</c>：从 Base64 解码后通过 <see cref="Buffer.BlockCopy"/> 还原浮点数组。</item>
    ///   <item><c>string[]</c>：收集所有 <c>&lt;Item&gt;</c> 子元素的文本值。</item>
    ///   <item><see cref="DateTime"/>：使用 <see cref="DateTimeStyles.RoundtripKind"/> 保留时区信息。</item>
    ///   <item>数值类型：使用 <see cref="CultureInfo.InvariantCulture"/> 解析。</item>
    ///   <item>兜底：通过 <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/> 进行通用转换。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="el">包含属性值的 XML 元素。</param>
    /// <param name="type">目标属性的 CLR 类型。</param>
    /// <returns>反序列化后的属性值。</returns>
    private static object? XmlToValue(XElement el, Type type)
    {
        if (type == typeof(float[]))
        {
            // Base64 解码 → 字节块拷贝还原浮点数组
            var bytes = Convert.FromBase64String(el.Value);
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
        // 字符串数组：收集所有 <Item> 子元素
        if (type == typeof(string[])) return el.Elements("Item").Select(e => e.Value).ToArray();
        if (type == typeof(string)) return el.Value;
        // 以下数值类型均使用不变区域性解析，与写入时保持一致
        if (type == typeof(int)) return int.Parse(el.Value, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return long.Parse(el.Value, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return float.Parse(el.Value, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(el.Value, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return bool.Parse(el.Value);
        // DateTime 使用 RoundtripKind 保留原始时区信息
        if (type == typeof(DateTime)) return DateTime.Parse(el.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(Guid)) return Guid.Parse(el.Value);
        if (type == typeof(decimal)) return decimal.Parse(el.Value, CultureInfo.InvariantCulture);

        // 兜底：使用通用类型转换
        return Convert.ChangeType(el.Value, type, CultureInfo.InvariantCulture);
    }

    #endregion

    /// <summary>
    /// 获取指定类型的所有公共可读写实例属性，并按名称升序排列。
    /// <para>
    /// 按名称排序确保属性在 XML 中的读写顺序始终一致，
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