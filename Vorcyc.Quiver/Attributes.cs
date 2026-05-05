namespace Vorcyc.Quiver;

// ══════════════════════════════════════════════════════════════════
// Attribute definitions
// Used to declare vector database metadata on entity class properties:
// primary key, vector fields, and index configuration.
// QuiverSet<TEntity> scans these attributes via reflection during construction
// to automatically discover and register fields.
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Marks a property as a vector feature field.
/// <para>
/// The marked property type must be <c>float[]</c>. The dimensionality is specified at compile time
/// via <paramref name="dimensions"/> and is validated against the actual array length at write time.
/// </para>
/// <para>
/// An entity class may have multiple <see cref="QuiverVectorAttribute"/> properties
/// (e.g., holding both a text embedding and an image embedding).
/// Use the <c>vectorSelector</c> expression to specify the target field when searching.
/// </para>
/// </summary>
/// <param name="dimensions">
/// The fixed vector dimensionality. The actual array length must equal this value at write time,
/// otherwise an <see cref="ArgumentException"/> is thrown.
/// Common values: 128 (lightweight models), 384 (MiniLM), 768 (BERT), 1536 (OpenAI Ada).
/// </param>
/// <param name="metric">
/// The distance metric type that determines how similarity is computed. Default is <see cref="DistanceMetric.Cosine"/>.
/// <list type="bullet">
///   <item><see cref="DistanceMetric.Cosine"/>: Cosine similarity, suitable for text/semantic search (vectors are automatically pre-normalized).</item>
///   <item><see cref="DistanceMetric.DotProduct"/>: Dot product, suitable for pre-normalized vectors or maximum inner product search.</item>
///   <item><see cref="DistanceMetric.Euclidean"/>: Euclidean distance (converted to similarity), suitable for spatial coordinates.</item>
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
    /// <summary>The fixed vector dimensionality. The actual array length must match this value at write time.</summary>
    public int Dimensions { get; } = dimensions;

    /// <summary>The distance metric type that determines how similarity is computed and whether pre-normalization is enabled.</summary>
    public DistanceMetric Metric { get; } = metric;

    /// <summary>
    /// Whether the vector value is allowed to be <c>null</c>. Default is <c>false</c> (required).
    /// <para>
    /// When set to <c>true</c>, entities with a <c>null</c> vector can still be written,
    /// but will not be added to the index for this field and will not appear in search results.
    /// Useful when not all entities have this feature
    /// (e.g., a face embedding — not every image contains a face).
    /// </para>
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>
    /// Custom similarity computation type. When set, <see cref="Metric"/> is ignored.
    /// <para>
    /// The type must be a <see langword="struct"/> implementing <see cref="Similarity.ISimilarity{T}"/> (T is <see cref="float"/>)
    /// with a public parameterless constructor. When <see langword="null"/> (default), the built-in implementation for <see cref="Metric"/> is used.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// [QuiverVector(128, CustomSimilarity = typeof(ManhattanSimilarity))]
    /// public float[] Embedding { get; set; }
    /// </code>
    /// </example>
    public Type? CustomSimilarity { get; set; }
}

/// <summary>
/// Marks a property as the entity primary key. Each entity class must have exactly one primary key property.
/// <para>
/// The primary key is used for unique identification, deduplication, and <see cref="QuiverSet{TEntity}.Find"/> lookups.
/// Any type is supported (<c>string</c>, <c>int</c>, <c>Guid</c>, etc.),
/// but values are boxed as <c>object</c> and stored in an internal dictionary at runtime.
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
/// Configures the index type and its parameters for a vector field.
/// This attribute is optional — if not present, <see cref="VectorIndexType.Flat"/> brute-force search is used by default.
/// <para>
/// Used on the same property as <see cref="QuiverVectorAttribute"/> to specify the indexing strategy for that vector field.
/// Different index types only use their relevant parameters; unrelated parameters are ignored.
/// </para>
/// </summary>
/// <param name="indexType">The index type. Default is <see cref="VectorIndexType.Flat"/>.</param>
/// <example>
/// <code>
/// // HNSW index: preferred for approximate search on high-dimensional vectors
/// [QuiverVector(768)]
/// [QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
/// public float[] Embedding { get; set; }
///
/// // IVF index: suitable for large-scale data scenarios
/// [QuiverVector(128)]
/// [QuiverIndex(VectorIndexType.IVF, NumClusters = 100, NumProbes = 15)]
/// public float[] Feature { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public class QuiverIndexAttribute(VectorIndexType indexType = VectorIndexType.Flat) : Attribute
{
    /// <summary>The index type. Determines the search algorithm and performance characteristics.</summary>
    public VectorIndexType IndexType { get; } = indexType;

    // ── HNSW-specific parameters ──

    /// <summary>
    /// Maximum number of neighbor connections per layer (HNSW only).
    /// Layer 0 is automatically set to <c>M × 2</c>. Increasing this improves recall but uses more memory and build time.
    /// <para>Recommended range: 12–48. Default: 16.</para>
    /// </summary>
    public int M { get; set; } = 16;

    /// <summary>
    /// Candidate set size during construction (HNSW only).
    /// The number of candidate neighbors searched per layer when inserting a new node.
    /// Larger values produce higher graph quality but slower insertions.
    /// <para>Recommended range: 100–500. Default: 200.</para>
    /// </summary>
    public int EfConstruction { get; set; } = 200;

    /// <summary>
    /// Candidate set size during search (HNSW only).
    /// Larger values increase recall but slow down search. Must be ≥ topK.
    /// Can be adjusted at runtime via <c>HnswIndex.EfSearch</c>.
    /// <para>Recommended range: 50–500. Default: 50.</para>
    /// </summary>
    public int EfSearch { get; set; } = 50;

    // ── IVF-specific parameters ──

    /// <summary>
    /// Number of K-Means clusters (IVF only).
    /// When 0, automatically set to <c>√n</c> (where n is the number of vectors at index build time).
    /// <para>Recommended range: √n to 4√n. Default: 0 (auto-computed).</para>
    /// </summary>
    public int NumClusters { get; set; } = 0;

    /// <summary>
    /// Number of clusters to probe during search (IVF only).
    /// Higher values increase recall but slow down search. Setting this equal to the total number of clusters is equivalent to brute-force search.
    /// <para>Recommended range: 1–20. Default: 10.</para>
    /// </summary>
    public int NumProbes { get; set; } = 10;
}

// ══════════════════════════════════════════════════════════════════
// Enum definitions
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Vector index type. Different types represent different trade-offs among search speed, accuracy, and memory usage.
/// <list type="table">
///   <listheader><term>Type</term><description>Characteristics</description></listheader>
///   <item><term>Flat</term><description>Brute-force search, 100% exact, suitable for small datasets (&lt;10K)</description></item>
///   <item><term>HNSW</term><description>Approximate search, high recall, general-purpose choice (10K–10M)</description></item>
///   <item><term>IVF</term><description>Approximate search, large-scale batch queries (100K+)</description></item>
///   <item><term>KDTree</term><description>Exact search, only effective at low dimensions (&lt;20 dims)</description></item>
/// </list>
/// </summary>
public enum VectorIndexType
{
    /// <summary>
    /// Brute-force (Flat) search.
    /// Computes similarity against every vector; results are 100% exact.
    /// <para>Time complexity: O(n × d). Automatically parallelized when there are more than 10,000 entries.</para>
    /// </summary>
    Flat,

    /// <summary>
    /// HNSW (Hierarchical Navigable Small World) graph.
    /// A multi-layer proximity graph structure — the general-purpose choice for approximate search.
    /// <para>Time complexity: O(log n). Tunable via M, efConstruction, and efSearch.</para>
    /// </summary>
    HNSW,

    /// <summary>
    /// IVF (Inverted File Index).
    /// Partitions the vector space via K-Means clustering; only the nearest clusters are probed during search.
    /// <para>Time complexity: O(n/k × d). Best for large datasets with batch queries.</para>
    /// </summary>
    IVF,

    /// <summary>
    /// KD-Tree (K-Dimensional Tree) spatial binary partitioning tree.
    /// Exact search, but only effective at low dimensions (&lt;20 dims); degrades to O(n) at high dimensions.
    /// <para>Time complexity: O(log n) (low-dim), O(n) (high-dim).</para>
    /// </summary>
    KDTree
}

/// <summary>
/// Distance metric type. Determines how vector similarity is computed.
/// <para>
/// Selection guide:
/// <list type="bullet">
///   <item><see cref="Cosine"/>: Most common; suitable for text embeddings and semantic search. Measures directional similarity, ignoring vector magnitude.</item>
///   <item><see cref="Euclidean"/>: Suitable for spatial coordinates and physical distances. Cares about absolute distance.</item>
///   <item><see cref="DotProduct"/>: Suitable for pre-normalized vectors or maximum inner product search (MIPS).</item>
///   <item><see cref="Manhattan"/>: L1 distance; suitable for sparse features and recommendation systems.</item>
///   <item><see cref="Chebyshev"/>: L∞ distance; detects the largest per-dimension deviation.</item>
///   <item><see cref="Pearson"/>: Pearson correlation; suitable for text embeddings (cosine after mean-centering).</item>
///   <item><see cref="Hamming"/>: Hamming distance; suitable for binary hash fingerprints.</item>
///   <item><see cref="Jaccard"/>: Generalized Jaccard; suitable for BoW/TF-IDF sparse text features.</item>
///   <item><see cref="Canberra"/>: Canberra distance; suitable for sparse data (magnitude-sensitive).</item>
/// </list>
/// </para>
/// </summary>
public enum DistanceMetric
{
    /// <summary>
    /// Cosine similarity: <c>cos(θ) = (a·b) / (‖a‖ × ‖b‖)</c>. Range: [-1, 1].
    /// <para>
    /// Enables pre-normalization optimization: vectors are automatically L2-normalized at write time,
    /// and dot product (Dot) is used instead of cosine during search,
    /// since for normalized vectors <c>Dot(a, b) = CosineSimilarity(a, b)</c>.
    /// </para>
    /// </summary>
    Cosine,

    /// <summary>
    /// Euclidean distance (converted to similarity): <c>similarity = 1 / (1 + ‖a - b‖₂)</c>. Range: (0, 1].
    /// <para>Similarity is 1 when distance is 0 (identical); approaches 0 as distance approaches infinity.</para>
    /// </summary>
    Euclidean,

    /// <summary>
    /// Dot product (inner product): <c>a·b = Σ(aᵢ × bᵢ)</c>. Range depends on vector magnitude.
    /// <para>For normalized vectors, dot product equals cosine similarity. Suitable for maximum inner product search (MIPS).</para>
    /// </summary>
    DotProduct,

    /// <summary>
    /// Manhattan distance (L1 norm) converted to similarity: <c>similarity = 1 / (1 + Σ|aᵢ - bᵢ|)</c>. Range: (0, 1].
    /// <para>Less sensitive to outlier dimensions than Euclidean (no squaring). Suitable for sparse features and recommendation systems.</para>
    /// </summary>
    Manhattan,

    /// <summary>
    /// Chebyshev distance (L∞ norm) converted to similarity: <c>similarity = 1 / (1 + max|aᵢ - bᵢ|)</c>. Range: (0, 1].
    /// <para>Only considers the maximum per-dimension difference. Suitable for feature deviation detection and chessboard distances.</para>
    /// </summary>
    Chebyshev,

    /// <summary>
    /// Pearson correlation coefficient: cosine similarity after mean-centering. Range: [-1, 1].
    /// <para>
    /// Eliminates the effect of overall vector offset; measures only the linear correlation pattern across dimensions.
    /// Suitable for text embeddings (removing document-length bias), TF-IDF document comparison, and recommendation score vectors.
    /// </para>
    /// </summary>
    Pearson,

    /// <summary>
    /// Hamming similarity: <c>similarity = 1 - (number of unequal elements / total dimensions)</c>. Range: [0, 1].
    /// <para>
    /// Suitable for binary vectors, LSH binary hash codes, and SimHash/MinHash text fingerprint comparison.
    /// Generally meaningless for continuous floating-point vectors; use only with binarized or quantized vectors.
    /// </para>
    /// </summary>
    Hamming,

    /// <summary>
    /// Generalized Jaccard similarity: <c>similarity = Σmin(aᵢ,bᵢ) / Σmax(aᵢ,bᵢ)</c>. Range: [0, 1].
    /// <para>
    /// Degenerates to the standard set Jaccard coefficient <c>|A∩B| / |A∪B|</c> for binary vectors.
    /// Suitable for BoW/TF-IDF sparse text features and histogram feature comparison. Requires non-negative elements.
    /// </para>
    /// </summary>
    Jaccard,

    /// <summary>
    /// Canberra distance converted to similarity: <c>similarity = 1 - (1/n) × Σ|aᵢ-bᵢ|/(|aᵢ|+|bᵢ|)</c>. Range: [0, 1].
    /// <para>
    /// A weighted L1 distance — each dimension is normalized by its magnitude, making it very sensitive to values near zero.
    /// Suitable for sparse text features, chemical fingerprints, and mixed features with large magnitude differences.
    /// </para>
    /// </summary>
    Canberra
}

/// <summary>
/// File format for data export/import.
/// <para>
/// Used only with <see cref="QuiverDbContext.ExportAsync"/> and <see cref="QuiverDbContext.ImportAsync"/>;
/// does not affect the primary storage format (which is always the compact binary format).
/// </para>
/// </summary>
/// <seealso cref="QuiverDbContext"/>
public enum ExportFormat
{
    /// <summary>
    /// JSON format. Human-readable; suitable for debugging and exchanging data with external systems.
    /// <para>Serialized using <c>System.Text.Json</c> with indentation and camelCase naming by default.</para>
    /// </summary>
    Json,

    /// <summary>
    /// XML format. Human-readable; vector data is Base64-encoded.
    /// <para>Serialized using <c>System.Xml.Linq</c>.</para>
    /// </summary>
    Xml,
}

/// <summary>
/// Controls the in-memory cache strategy for <b>entity objects</b> (<c>TEntity</c>).
/// <para>
/// Configured via <see cref="QuiverDbOptions.EntityCache"/>.
/// </para>
/// </summary>
/// <seealso cref="QuiverDbOptions"/>
public enum EntityCacheMode
{
    /// <summary>
    /// Full memory (default). All entity objects reside in an in-memory dictionary; access latency is minimal and behavior is identical to prior versions.
    /// <para>
    /// Suitable for small entity objects or datasets with fewer than one million entries.
    /// </para>
    /// </summary>
    FullMemory,

    /// <summary>
    /// Lazy paging cache. Entity objects are managed in pages (<see cref="QuiverDbOptions.PageSize"/> entities/page).
    /// At most <see cref="QuiverDbOptions.MaxCachedPages"/> pages are kept in memory;
    /// cold pages are evicted via LRU and serialized to <c>.qvpg</c> page files, loaded back on demand.
    /// <para>
    /// Vector index structures (HNSW/IVF etc.) are not affected and always remain in memory to ensure search performance.
    /// </para>
    /// <para>
    /// Suitable for large entity objects or datasets exceeding one million entries with localized access patterns.
    /// </para>
    /// <para>
    /// Requires: <see cref="QuiverDbOptions.DatabasePath"/> must be set.
    /// Page files are stored in the <c>{DatabasePath}.pages\{EntityTypeName}\</c> directory.
    /// </para>
    /// </summary>
    LazyPaging
}

