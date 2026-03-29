namespace Vorcyc.Quiver.Storage;

using System.Text.Json;

/// <summary>
/// JSON 格式的存储提供者，实现 <see cref="IStorageProvider"/> 接口。
/// <para>
/// 使用 <see cref="System.Text.Json"/> 进行序列化和反序列化，具有以下特点：
/// <list type="bullet">
///   <item>可读性好，生成的 JSON 文件便于调试和手动编辑。</item>
///   <item>支持通过 <see cref="JsonSerializerOptions"/> 自定义缩进、命名策略等行为。</item>
///   <item>文件体积相对较大，适合开发和调试阶段使用。</item>
/// </list>
/// </para>
/// </summary>
/// <param name="jsonOptions">
/// JSON 序列化选项，由 <see cref="QuiverDbOptions.JsonOptions"/> 传入，
/// 控制输出格式（缩进、命名策略等）。
/// </param>
/// <seealso cref="IStorageProvider"/>
/// <seealso cref="StorageFormat.Json"/>
/// <seealso cref="QuiverDbOptions.JsonOptions"/>
internal class JsonStorageProvider(JsonSerializerOptions jsonOptions) : IStorageProvider
{
    /// <summary>
    /// 将所有向量集合以 JSON 格式异步持久化到指定文件。
    /// <para>
    /// 序列化结构为一个 JSON 对象，每个键对应一个向量集合的类型名称，
    /// 值为该集合中所有实体组成的数组。示例：
    /// <code>
    /// {
    ///   "FaceFeature": [
    ///     { "id": "...", "embedding": [...] },
    ///     ...
    ///   ]
    /// }
    /// </code>
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
        // 构建扁平字典：类型名称 → 实体列表（忽略 Type，序列化时由 JsonSerializer 自动推断）
        var data = new Dictionary<string, object>();
        foreach (var (typeName, (_, entities)) in sets)
            data[typeName] = entities;

        // 使用配置的选项序列化为 JSON 字符串，然后异步写入文件
        var json = JsonSerializer.Serialize(data, jsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// 从指定 JSON 文件异步加载所有向量集合。
    /// <para>
    /// 加载流程：
    /// <list type="number">
    ///   <item>异步读取文件全部文本并解析为 <see cref="JsonDocument"/>。</item>
    ///   <item>遍历根对象的每个属性，通过 <paramref name="typeMap"/> 匹配对应的 CLR 类型。</item>
    ///   <item>逐元素反序列化为实体对象；未匹配的类型名称将被跳过（前向兼容）。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="filePath">要读取的 JSON 文件路径。</param>
    /// <param name="typeMap">
    /// 类型名称到 CLR <see cref="Type"/> 的映射字典。
    /// <para>文件中存在但字典中缺失的类型将被跳过，确保前向兼容。</para>
    /// </param>
    /// <returns>加载后的向量集合字典。键为类型名称，值为反序列化后的实体对象列表。</returns>
    public async Task<Dictionary<string, List<object>>> LoadAsync(string filePath, IReadOnlyDictionary<string, Type> typeMap)
    {
        var result = new Dictionary<string, List<object>>();

        // 一次性读取整个 JSON 文件
        var json = await File.ReadAllTextAsync(filePath);
        // 解析为 DOM 文档，以便按属性逐项遍历
        using var doc = JsonDocument.Parse(json);

        // 遍历根对象的每个属性（每个属性对应一个向量集合）
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            // 跳过类型映射中不存在的集合（前向兼容：文件包含新增类型时不报错）
            if (!typeMap.TryGetValue(prop.Name, out var type)) continue;

            // 逐个反序列化数组中的实体对象
            var entities = new List<object>();
            foreach (var element in prop.Value.EnumerateArray())
            {
                var entity = element.Deserialize(type, jsonOptions);
                if (entity != null) entities.Add(entity);
            }
            result[prop.Name] = entities;
        }

        return result;
    }
}