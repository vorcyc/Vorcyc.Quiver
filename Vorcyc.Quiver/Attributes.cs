namespace Vorcyc.Quiver;

// ══════════════════════════════════════════════════════════════════
// 特性（Attribute）定义
// 用于在实体类的属性上声明向量数据库的元数据：主键、向量字段、索引配置。
// QuiverSet<TEntity> 构造时通过反射扫描这些特性来自动发现和注册字段。
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// 标记属性为向量特征字段。
/// <para>
/// 被标记的属性类型必须为 <c>float[]</c>，维度在编译时由 <paramref name="dimensions"/> 指定，
/// 运行时写入向量数据库时会校验实际维度是否匹配。
/// </para>
/// <para>
/// 一个实体类可标记多个 <see cref="QuiverVectorAttribute"/> 属性（如同时持有文本向量和图像向量），
/// 搜索时通过 <c>vectorSelector</c> 表达式指定目标字段。
/// </para>
/// </summary>
/// <param name="dimensions">
/// 向量维度（固定值）。写入时实际数组长度必须等于此值，否则抛出 <see cref="ArgumentException"/>。
/// 常见值：128（轻量模型）、384（MiniLM）、768（BERT）、1536（OpenAI Ada）。
/// </param>
/// <param name="metric">
/// 距离度量类型。决定相似度的计算方式。默认 <see cref="DistanceMetric.Cosine"/>。
/// <list type="bullet">
///   <item><see cref="DistanceMetric.Cosine"/>：余弦相似度，适合文本/语义搜索（向量自动预归一化）</item>
///   <item><see cref="DistanceMetric.DotProduct"/>：内积，适合已归一化的向量或最大内积搜索</item>
///   <item><see cref="DistanceMetric.Euclidean"/>：欧几里得距离（转换为相似度），适合空间坐标</item>
/// </list>
/// </param>
/// <example>
/// <code>
/// public class Document
/// {
///     [QuiverKey]
///     public string Id { get; set; }
///
///     [QuiverVector(384, DistanceMetric.Cosine)]
///     public float[] Embedding { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class QuiverVectorAttribute(int dimensions, DistanceMetric metric = DistanceMetric.Cosine) : Attribute
{
    /// <summary>向量维度（固定值），运行时校验实际数组长度须与此一致。</summary>
    public int Dimensions { get; } = dimensions;

    /// <summary>距离度量类型，决定相似度计算方式和是否启用预归一化优化。</summary>
    public DistanceMetric Metric { get; } = metric;

    /// <summary>
    /// 是否允许向量值为 <c>null</c>。默认 <c>false</c>（必填）。
    /// <para>
    /// 设为 <c>true</c> 时，向量为 <c>null</c> 的实体仍可写入，但不会加入该字段的索引，
    /// 搜索该字段时也不会返回这些实体。适用于并非所有实体都具有此特征的场景
    /// （如图片中的人脸向量——并非每张图都有人脸）。
    /// </para>
    /// </summary>
    public bool Optional { get; set; }
}

/// <summary>
/// 标记属性为实体主键。每个实体类必须有且仅有一个主键属性。
/// <para>
/// 主键用于实体的唯一标识、去重和 <see cref="QuiverSet{TEntity}.Find"/> 查找。
/// 支持任意类型（<c>string</c>、<c>int</c>、<c>Guid</c> 等），
/// 但运行时会装箱为 <c>object</c> 存储在内部字典中。
/// </para>
/// </summary>
/// <example>
/// <code>
/// [QuiverKey]
/// public string Id { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public class QuiverKeyAttribute : Attribute;

/// <summary>
/// 配置向量字段的索引类型及其参数。可选特性——未标记时默认使用 <see cref="VectorIndexType.Flat"/> 暴力搜索。
/// <para>
/// 与 <see cref="QuiverVectorAttribute"/> 标记在同一属性上使用，为该向量字段指定索引策略。
/// 不同的索引类型仅使用各自相关的参数，无关参数会被忽略。
/// </para>
/// </summary>
/// <param name="indexType">索引类型。默认 <see cref="VectorIndexType.Flat"/>。</param>
/// <example>
/// <code>
/// // HNSW 索引：高维向量的近似搜索首选
/// [QuiverVector(768)]
/// [QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
/// public float[] Embedding { get; set; }
///
/// // IVF 索引：大数据量场景
/// [QuiverVector(128)]
/// [QuiverIndex(VectorIndexType.IVF, NumClusters = 100, NumProbes = 15)]
/// public float[] Feature { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public class QuiverIndexAttribute(VectorIndexType indexType = VectorIndexType.Flat) : Attribute
{
    /// <summary>索引类型。决定搜索算法和性能特征。</summary>
    public VectorIndexType IndexType { get; } = indexType;

    // ── HNSW 专用参数 ──

    /// <summary>
    /// 每层最大邻居连接数（仅 <see cref="VectorIndexType.HNSW"/>）。
    /// 第 0 层自动设为 <c>M × 2</c>。增大提高召回率但增加内存和构建时间。
    /// <para>推荐范围：12~48。默认 16。</para>
    /// </summary>
    public int M { get; set; } = 16;

    /// <summary>
    /// 构建阶段的候选集大小（仅 <see cref="VectorIndexType.HNSW"/>）。
    /// 插入新节点时在每层搜索的候选邻居数量。越大 → 图质量越高、插入越慢。
    /// <para>推荐范围：100~500。默认 200。</para>
    /// </summary>
    public int EfConstruction { get; set; } = 200;

    /// <summary>
    /// 搜索阶段的候选集大小（仅 <see cref="VectorIndexType.HNSW"/>）。
    /// 越大 → 召回率越高、搜索越慢。须 ≥ topK。运行时可通过 <c>HnswIndex.EfSearch</c> 动态调整。
    /// <para>推荐范围：50~500。默认 50。</para>
    /// </summary>
    public int EfSearch { get; set; } = 50;

    // ── IVF 专用参数 ──

    /// <summary>
    /// K-Means 聚类数量（仅 <see cref="VectorIndexType.IVF"/>）。
    /// 为 0 时在首次构建索引时自动取 <c>√n</c>（n 为当时的向量数量）。
    /// <para>推荐范围：√n ~ 4√n。默认 0（自动计算）。</para>
    /// </summary>
    public int NumClusters { get; set; } = 0;

    /// <summary>
    /// 搜索时探测的聚类数量（仅 <see cref="VectorIndexType.IVF"/>）。
    /// 值越大召回率越高，但搜索越慢。设为聚类总数时等价于暴力搜索。
    /// <para>推荐范围：1~20。默认 10。</para>
    /// </summary>
    public int NumProbes { get; set; } = 10;
}

// ══════════════════════════════════════════════════════════════════
// 枚举定义
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// 向量索引类型。不同类型在搜索速度、精确度、内存占用之间有不同的权衡。
/// <list type="table">
///   <listheader><term>类型</term><description>特征</description></listheader>
///   <item><term>Flat</term><description>暴力搜索，100% 精确，适合小数据量（&lt;10K）</description></item>
///   <item><term>HNSW</term><description>近似搜索，高召回率，通用首选（10K~10M）</description></item>
///   <item><term>IVF</term><description>近似搜索，大数据量批量查询（100K+）</description></item>
///   <item><term>KDTree</term><description>精确搜索，仅适合低维（&lt;20 维）</description></item>
/// </list>
/// </summary>
public enum VectorIndexType
{
    /// <summary>
    /// 暴力搜索（Flat / Brute-Force）。
    /// 遍历所有向量计算相似度，结果 100% 精确。
    /// <para>时间复杂度：O(n × d)。超过 10,000 条数据时自动并行化。</para>
    /// </summary>
    Flat,

    /// <summary>
    /// HNSW（Hierarchical Navigable Small World）分层可导航小世界图。
    /// 多层近邻图结构，近似搜索的通用首选。
    /// <para>时间复杂度：O(log n)。可通过 M、efConstruction、efSearch 调优精度与速度。</para>
    /// </summary>
    HNSW,

    /// <summary>
    /// IVF（Inverted File Index）倒排文件索引。
    /// 基于 K-Means 聚类划分向量空间，搜索时只探测最近的几个聚类。
    /// <para>时间复杂度：O(n/k × d)。适合大数据量 + 批量查询场景。</para>
    /// </summary>
    IVF,

    /// <summary>
    /// KD-Tree（K-Dimensional Tree）空间二叉划分树。
    /// 精确搜索，但仅在低维（&lt;20 维）下有效，高维退化为 O(n)。
    /// <para>时间复杂度：O(log n)（低维），O(n)（高维）。</para>
    /// </summary>
    KDTree
}

/// <summary>
/// 距离度量类型。决定向量相似度的计算方式。
/// <para>
/// 选择指南：
/// <list type="bullet">
///   <item><see cref="Cosine"/>：最常用，适合文本嵌入、语义搜索。方向相似性，不关心向量长度</item>
///   <item><see cref="Euclidean"/>：适合空间坐标、物理距离。关心绝对距离</item>
///   <item><see cref="DotProduct"/>：适合已归一化的向量或最大内积搜索（MIPS）</item>
/// </list>
/// </para>
/// </summary>
public enum DistanceMetric
{
    /// <summary>
    /// 余弦相似度：<c>cos(θ) = (a·b) / (‖a‖ × ‖b‖)</c>。值域 [-1, 1]。
    /// <para>
    /// 启用预归一化优化：写入时向量自动 L2 归一化，搜索时用点积（Dot）替代余弦计算，
    /// 因为归一化向量的 <c>Dot(a, b) = CosineSimilarity(a, b)</c>。
    /// </para>
    /// </summary>
    Cosine,

    /// <summary>
    /// 欧几里得距离（转换为相似度）：<c>similarity = 1 / (1 + ‖a - b‖₂)</c>。值域 (0, 1]。
    /// <para>距离为 0 时相似度为 1（完全相同），距离趋向无穷时相似度趋向 0。</para>
    /// </summary>
    Euclidean,

    /// <summary>
    /// 内积（点积）：<c>a·b = Σ(aᵢ × bᵢ)</c>。值域取决于向量长度。
    /// <para>归一化向量的点积等价于余弦相似度。适合最大内积搜索（MIPS）场景。</para>
    /// </summary>
    DotProduct
}

/// <summary>
/// 向量数据库的持久化存储格式。
/// </summary>
/// <seealso cref="QuiverDbOptions"/>
public enum StorageFormat
{
    /// <summary>
    /// JSON 格式。可读性好，便于调试和手动编辑。文件体积较大。
    /// <para>使用 <c>System.Text.Json</c> 序列化。</para>
    /// </summary>
    Json,

    /// <summary>
    /// XML 格式。可读性好，向量数据使用 Base64 编码。
    /// <para>使用 <c>System.Xml.Linq</c> 序列化。</para>
    /// </summary>
    Xml,

    /// <summary>
    /// 二进制格式。文件体积最小、读写最快，但不可人工阅读。
    /// <para>
    /// 向量数据直接写入 <c>byte[]</c>（零拷贝 <c>MemoryMarshal.AsBytes</c>），
    /// 无精度损失，适合生产环境。
    /// </para>
    /// </summary>
    Binary
}