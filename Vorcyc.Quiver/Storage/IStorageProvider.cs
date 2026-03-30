namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 向量数据库的存储提供者接口，定义持久化读写的统一契约。
/// <para>
/// 所有存储格式（JSON、XML、Binary）均需实现此接口，以支持通过
/// <see cref="StorageProviderFactory"/> 进行策略切换。
/// </para>
/// </summary>
/// <seealso cref="JsonStorageProvider"/>
/// <seealso cref="XmlStorageProvider"/>
/// <seealso cref="BinaryStorageProvider"/>
/// <seealso cref="StorageProviderFactory"/>
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
    /// <para>包含属性重命名映射，供加载时将旧属性名映射到新属性名。</para>
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
/// 存储提供者的简单工厂，根据 <see cref="QuiverDbOptions.StorageFormat"/> 创建对应的
/// <see cref="IStorageProvider"/> 实现。
/// <para>
/// 在 <see cref="QuiverDbContext"/> 构造时调用，实现存储策略与业务逻辑的解耦。
/// </para>
/// </summary>
/// <seealso cref="IStorageProvider"/>
/// <seealso cref="QuiverDbOptions"/>
internal static class StorageProviderFactory
{
    /// <summary>
    /// 根据配置选项创建对应格式的存储提供者实例。
    /// </summary>
    /// <param name="options">数据库配置选项，其中 <see cref="QuiverDbOptions.StorageFormat"/> 决定创建哪种提供者。</param>
    /// <returns>与所选存储格式匹配的 <see cref="IStorageProvider"/> 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 当 <paramref name="options"/> 的 <see cref="QuiverDbOptions.StorageFormat"/>
    /// 不是已知的 <see cref="StorageFormat"/> 枚举值时抛出。
    /// </exception>
    public static IStorageProvider Create(QuiverDbOptions options) => options.StorageFormat switch
    {
        // JSON 格式：传入序列化选项以控制缩进、命名策略等
        StorageFormat.Json => new JsonStorageProvider(options.JsonOptions),
        // XML 格式：无需额外配置
        StorageFormat.Xml => new XmlStorageProvider(),
        // 二进制格式：零拷贝高性能，适合生产环境
        StorageFormat.Binary => new BinaryStorageProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(options.StorageFormat))
    };
}