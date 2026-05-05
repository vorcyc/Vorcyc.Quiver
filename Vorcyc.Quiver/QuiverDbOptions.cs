namespace Vorcyc.Quiver;

/// <summary>
/// 向量数据库的全局配置选项。
/// <para>
/// 通过此类可以控制数据库的存储路径、默认距离度量方式和各项功能开关。
/// 在创建 <see cref="QuiverDbContext"/> 时传入。
/// 数据库始终使用紧凑二进制格式（QDB v3）持久化，若需要可读格式请使用
/// <see cref="QuiverDbContext.ExportAsync"/> 导出为 JSON 或 XML。
/// </para>
/// <example>
/// 典型用法：
/// <code>
/// var options = new QuiverDbOptions
/// {
///     DatabasePath = @"C:\Data\MyVectorDb",
///     DefaultMetric = DistanceMetric.Cosine,
///     EnableWal = true
/// };
/// </code>
/// </example>
/// </summary>
/// <seealso cref="QuiverDbContext"/>
/// <seealso cref="DistanceMetric"/>
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

    // ── 实体缓存策略配置 ──

    /// <summary>
    /// 实体对象（<c>TEntity</c>）的内存缓存策略。
    /// <para>
    /// <list type="bullet">
    ///   <item><see cref="EntityCacheMode.FullMemory"/>（默认）：所有实体常驻内存字典，访问延迟最低，与旧版行为一致。</item>
    ///   <item><see cref="EntityCacheMode.LazyPaging"/>：实体按页按需加载，LRU 策略淘汰冷页，内存上限可控。
    ///   适用于实体对象本身占用内存较大或数据集超大（百万级）的场景。
    ///   要求设置 <see cref="DatabasePath"/>。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>注意</b>：向量索引结构（HNSW/IVF 等）不受此设置影响，始终常驻内存以保证搜索性能。
    /// </para>
    /// </summary>
    /// <value>默认为 <see cref="EntityCacheMode.FullMemory"/>。</value>
    public EntityCacheMode EntityCache { get; set; } = EntityCacheMode.FullMemory;

    /// <summary>
    /// 懒加载模式下，每个 <see cref="QuiverSet{TEntity}"/> 在内存中最多保留的页数。
    /// 超出时使用 LRU 策略淘汰最久未使用的冷页（脏页先写回磁盘）。
    /// <para>
    /// 实际内存占用上限约为：<c>MaxCachedPages × PageSize × 单实体内存大小</c>。
    /// </para>
    /// </summary>
    /// <value>默认为 16 页。</value>
    public int MaxCachedPages { get; set; } = 16;

    /// <summary>
    /// 懒加载模式下每个分页最多容纳的实体数量。
    /// 页越大则加载粒度越粗（单次 I/O 读取更多数据），页越小则内存更精细可控。
    /// 推荐范围：128 ~ 2048。
    /// </summary>
    /// <value>默认为 512 条实体/页。</value>
    public int PageSize { get; set; } = 512;

    /// <summary>
    /// 验证选项组合的合法性。在 <see cref="QuiverDbContext"/> 构造时调用。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="EntityCache"/> 为 <see cref="EntityCacheMode.LazyPaging"/> 但未设置 <see cref="DatabasePath"/> 时抛出。
    /// </exception>
    internal void Validate()
    {
        if (EntityCache == EntityCacheMode.LazyPaging && string.IsNullOrEmpty(DatabasePath))
            throw new InvalidOperationException(
                $"{nameof(EntityCache)}.{nameof(EntityCacheMode.LazyPaging)} requires a valid {nameof(DatabasePath)} " +
                $"for the page cache directory.");
    }
}
