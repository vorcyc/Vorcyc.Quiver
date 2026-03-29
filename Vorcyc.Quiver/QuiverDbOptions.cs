namespace Vorcyc.Quiver;

using System.Text.Json;

/// <summary>
/// 向量数据库的全局配置选项。
/// <para>
/// 通过此类可以控制数据库的存储路径、默认距离度量方式、持久化格式以及 JSON 序列化行为。
/// 在创建 <see cref="QuiverDbContext"/> 时传入。
/// </para>
/// <example>
/// 典型用法：
/// <code>
/// var options = new QuiverDbOptions
/// {
///     DatabasePath = @"C:\Data\MyVectorDb",
///     DefaultMetric = DistanceMetric.Cosine,
///     StorageFormat = StorageFormat.Binary,
///     EnableWal = true
/// };
/// </code>
/// </example>
/// </summary>
/// <seealso cref="QuiverDbContext"/>
/// <seealso cref="DistanceMetric"/>
/// <seealso cref="StorageFormat"/>
public class QuiverDbOptions
{
    /// <summary>
    /// 数据库文件的存储目录路径。
    /// <para>
    /// 若为 <see langword="null"/> 或未设置，则使用内存模式（不持久化）。
    /// 目录不存在时会自动创建。
    /// </para>
    /// </summary>
    /// <value>绝对或相对文件系统路径，默认为 <see langword="null"/>。</value>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// 默认的向量距离度量方式，用于衡量向量之间的相似度。
    /// <para>
    /// 当 <see cref="QuiverSet{T}"/> 未显式指定度量时，将使用此默认值。
    /// 可选值参见 <see cref="DistanceMetric"/>。
    /// </para>
    /// </summary>
    /// <value>默认为 <see cref="DistanceMetric.Cosine"/>（余弦相似度）。</value>
    public DistanceMetric DefaultMetric { get; set; } = DistanceMetric.Cosine;

    /// <summary>
    /// 数据的持久化存储格式。
    /// <para>
    /// 决定向量数据以何种格式写入磁盘。
    /// <list type="bullet">
    ///   <item><see cref="StorageFormat.Json"/>：可读性好，适合开发调试。</item>
    ///   <item><see cref="StorageFormat.Xml"/>：可读性好，向量使用 Base64 编码。</item>
    ///   <item><see cref="StorageFormat.Binary"/>：体积最小、性能最优，适合生产环境。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <value>默认为 <see cref="StorageFormat.Json"/>。</value>
    public StorageFormat StorageFormat { get; set; } = StorageFormat.Json;

    /// <summary>
    /// 当 <see cref="StorageFormat"/> 为 <see cref="StorageFormat.Json"/> 时使用的序列化选项。
    /// <para>
    /// 默认启用缩进输出（<see cref="JsonSerializerOptions.WriteIndented"/> = <see langword="true"/>）
    /// 并使用驼峰命名策略（<see cref="JsonNamingPolicy.CamelCase"/>），
    /// 以生成更易读且符合前端惯例的 JSON 文件。
    /// </para>
    /// </summary>
    /// <value>预配置的 <see cref="JsonSerializerOptions"/> 实例。</value>
    public JsonSerializerOptions JsonOptions { get; set; } = new()
    {
        // 启用缩进格式，提升 JSON 文件可读性
        WriteIndented = true,
        // 使用驼峰命名（camelCase），与 JavaScript/前端生态保持一致
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── WAL（Write-Ahead Log）增量持久化配置 ──

    /// <summary>
    /// 是否启用 WAL 增量持久化。启用后：
    /// <list type="bullet">
    ///   <item><see cref="QuiverDbContext.SaveChangesAsync"/> 仅将变更追加到 WAL 文件，复杂度 O(Δ)</item>
    ///   <item><see cref="QuiverDbContext.SaveAsync"/> 创建全量快照并清空 WAL</item>
    ///   <item>WAL 记录数超过 <see cref="WalCompactionThreshold"/> 时自动执行全量快照</item>
    ///   <item>加载时先读取快照，再回放 WAL 中的增量变更</item>
    /// </list>
    /// <para>
    /// 未启用时，<see cref="QuiverDbContext.SaveChangesAsync"/> 行为等同于 <see cref="QuiverDbContext.SaveAsync"/>。
    /// </para>
    /// </summary>
    /// <value>默认为 <see langword="false"/>。</value>
    public bool EnableWal { get; set; }

    /// <summary>
    /// WAL 记录数量达到此阈值时自动触发压缩（创建全量快照 + 清空 WAL）。
    /// <para>
    /// 过大的 WAL 会增加加载时的回放时间和磁盘占用。
    /// 推荐范围：1,000~100,000，取决于单条记录的大小（向量维度）和对加载速度的要求。
    /// </para>
    /// </summary>
    /// <value>默认为 10,000 条记录。</value>
    public int WalCompactionThreshold { get; set; } = 10_000;

    /// <summary>
    /// WAL 写入后是否立即执行 <c>fsync</c> 将数据刷新到物理磁盘。
    /// <list type="bullet">
    ///   <item><see langword="true"/>：最强持久性保证，进程崩溃或断电后数据不丢失。写入延迟约增加 ~1ms。</item>
    ///   <item><see langword="false"/>：依赖操作系统缓冲区刷新，性能更好，但断电时可能丢失最近的少量变更。</item>
    /// </list>
    /// </summary>
    /// <value>默认为 <see langword="true"/>（最强持久性）。</value>
    public bool WalFlushToDisk { get; set; } = true;
}