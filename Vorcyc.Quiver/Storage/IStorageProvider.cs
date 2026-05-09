namespace Vorcyc.Quiver.Storage;

using Vorcyc.Quiver.Migration;

/// <summary>
/// 导出/导入存储提供者接口，定义可读格式（JSON、XML）的序列化契约。
/// <para>
/// 此接口仅用于 <see cref="QuiverDbContext.ExportAsync"/> 和 <see cref="QuiverDbContext.ImportAsync"/>，
/// 不参与数据库的主存储路径（主存储始终使用 <see cref="BinaryStorageProvider"/>）。
/// </para>
/// </summary>
/// <seealso cref="JsonExportProvider"/>
/// <seealso cref="XmlExportProvider"/>
/// <seealso cref="ExportStorageProviderFactory"/>
internal interface IStorageProvider
{
    /// <summary>
    /// 将所有向量集合异步持久化到指定文件。
    /// </summary>
    /// <param name="filePath">目标文件的绝对或相对路径。文件不存在时自动创建，已存在时覆盖。</param>
    /// <param name="sets">
    /// 要保存的向量集合字典。
    /// <list type="bullet">
    ///   <item><b>键</b>：类型名称（如 <c>"FaceFeature"</c>），用于标识集合。</item>
    ///   <item><b>值</b>：元组 <c>(Type, List&lt;object&gt;)</c>，包含实体的 CLR 类型及实体列表。</item>
    /// </list>
    /// </param>
    /// <returns>表示异步写入操作的任务。</returns>
    Task SaveAsync(string filePath, IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets);

    /// <summary>
    /// 从指定文件异步加载所有向量集合。
    /// </summary>
    /// <param name="filePath">要读取的文件路径。</param>
    /// <param name="typeMap">
    /// 类型名称到 CLR <see cref="Type"/> 的映射字典。
    /// <para>文件中存在但字典中缺失的类型将被跳过，确保前向兼容。</para>
    /// </param>
    /// <param name="migrationRules">
    /// 可选的 Schema 迁移规则字典。键为类型全名，值为对应的迁移规则。
    /// </param>
    /// <returns>
    /// 加载后的向量集合字典。键为类型名称，值为反序列化后的实体对象列表。
    /// </returns>
    Task<Dictionary<string, List<object>>> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null);
}

/// <summary>
/// 导出/导入存储提供者工厂，根据 <see cref="ExportFormat"/> 创建对应的
/// <see cref="IStorageProvider"/> 实现。
/// <para>
/// 仅供 <see cref="QuiverDbContext.ExportAsync"/> 和 <see cref="QuiverDbContext.ImportAsync"/> 使用。
/// </para>
/// </summary>
internal static class ExportStorageProviderFactory
{
    /// <summary>
    /// 根据导出格式创建对应的存储提供者实例。
    /// </summary>
    /// <param name="format">目标导出格式。</param>
    /// <param name="jsonOptions">JSON 序列化选项，仅 <see cref="ExportFormat.Json"/> 时有效。</param>
    /// <returns>与所选格式匹配的 <see cref="IStorageProvider"/> 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">当 <paramref name="format"/> 不是已知枚举值时抛出。</exception>
    public static IStorageProvider Create(ExportFormat format, System.Text.Json.JsonSerializerOptions? jsonOptions = null) =>
        format switch
        {
            ExportFormat.Json => new JsonExportProvider(jsonOptions ?? DefaultJsonOptions),
            ExportFormat.Xml  => new XmlExportProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    /// <summary>默认 JSON 导出选项：缩进 + 驼峰命名。</summary>
    internal static readonly System.Text.Json.JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
}
