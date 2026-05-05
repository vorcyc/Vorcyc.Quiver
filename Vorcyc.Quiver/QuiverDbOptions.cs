namespace Vorcyc.Quiver;

/// <summary>
/// Global configuration options for the vector database.
/// <para>
/// Use this class to control the database storage path, default distance metric, and various feature flags.
/// Pass an instance to <see cref="QuiverDbContext"/> during construction.
/// The database always persists using the compact binary format (QDB v3).
/// Use <see cref="QuiverDbContext.ExportAsync"/> to export to a human-readable JSON or XML format if needed.
/// </para>
/// <example>
/// Typical usage:
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
    /// The directory path where database files are stored.
    /// <para>
    /// When <see langword="null"/> or not set, the database operates in memory-only mode (no persistence).
    /// The directory is created automatically if it does not exist.
    /// </para>
    /// </summary>
    /// <value>An absolute or relative file system path. Default is <see langword="null"/>.</value>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// The default vector distance metric used to measure similarity between vectors.
    /// <para>
    /// Used when a <see cref="QuiverSet{T}"/> does not explicitly specify a metric.
    /// See <see cref="DistanceMetric"/> for available values.
    /// </para>
    /// </summary>
    /// <value>Default is <see cref="DistanceMetric.Cosine"/> (cosine similarity).</value>
    public DistanceMetric DefaultMetric { get; set; } = DistanceMetric.Cosine;

    // ── WAL (Write-Ahead Log) incremental persistence configuration ──

    /// <summary>
    /// Whether to enable WAL incremental persistence. When enabled:
    /// <list type="bullet">
    ///   <item><see cref="QuiverDbContext.SaveChangesAsync"/> appends only changes to the WAL file, O(Δ) complexity.</item>
    ///   <item><see cref="QuiverDbContext.SaveAsync"/> creates a full snapshot and clears the WAL.</item>
    ///   <item>When the WAL record count exceeds <see cref="WalCompactionThreshold"/>, a full snapshot is triggered automatically.</item>
    ///   <item>At load time, the snapshot is read first, then WAL incremental changes are replayed.</item>
    /// </list>
    /// <para>
    /// When disabled, <see cref="QuiverDbContext.SaveChangesAsync"/> behaves identically to <see cref="QuiverDbContext.SaveAsync"/>.
    /// </para>
    /// </summary>
    /// <value>Default is <see langword="false"/>.</value>
    public bool EnableWal { get; set; }

    /// <summary>
    /// When the WAL record count reaches this threshold, compaction is triggered automatically
    /// (creates a full snapshot and clears the WAL).
    /// <para>
    /// A very large WAL increases replay time at load and disk usage.
    /// Recommended range: 1,000–100,000, depending on the size of each record (vector dimensions) and load speed requirements.
    /// </para>
    /// </summary>
    /// <value>Default is 10,000 records.</value>
    public int WalCompactionThreshold { get; set; } = 10_000;

    /// <summary>
    /// Whether to call <c>fsync</c> after each WAL write to flush data to physical disk.
    /// <list type="bullet">
    ///   <item><see langword="true"/>: Strongest durability guarantee; data is safe after a process crash or power failure. Adds ~1ms write latency.</item>
    ///   <item><see langword="false"/>: Relies on OS buffer flushing for better performance, but recent changes may be lost on power failure.</item>
    /// </list>
    /// </summary>
    /// <value>Default is <see langword="true"/> (strongest durability).</value>
    public bool WalFlushToDisk { get; set; } = true;

    // ── Entity cache strategy configuration ──

    /// <summary>
    /// In-memory cache strategy for entity objects (<c>TEntity</c>).
    /// <para>
    /// <list type="bullet">
    ///   <item><see cref="EntityCacheMode.FullMemory"/> (default): All entities reside in an in-memory dictionary; lowest access latency, identical to prior behavior.</item>
    ///   <item><see cref="EntityCacheMode.LazyPaging"/>: Entities are loaded page-by-page on demand with LRU eviction; memory usage is bounded.
    ///   Suitable for large entity objects or very large datasets (millions of entries).
    ///   Requires <see cref="DatabasePath"/> to be set.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Note</b>: Vector index structures (HNSW/IVF etc.) are not affected by this setting and always remain in memory for search performance.
    /// </para>
    /// </summary>
    /// <value>Default is <see cref="EntityCacheMode.FullMemory"/>.</value>
    public EntityCacheMode EntityCache { get; set; } = EntityCacheMode.FullMemory;

    /// <summary>
    /// In lazy-loading mode, the maximum number of pages kept in memory per <see cref="QuiverSet{TEntity}"/>.
    /// When exceeded, the least recently used cold pages are evicted (dirty pages are written back to disk first).
    /// <para>
    /// Approximate memory upper bound: <c>MaxCachedPages × PageSize × per-entity memory size</c>.
    /// </para>
    /// </summary>
    /// <value>Default is 16 pages.</value>
    public int MaxCachedPages { get; set; } = 16;

    /// <summary>
    /// Maximum number of entities per page in lazy-loading mode.
    /// Larger pages provide coarser loading granularity (more data per I/O); smaller pages give finer memory control.
    /// Recommended range: 128–2048.
    /// </summary>
    /// <value>Default is 512 entities per page.</value>
    public int PageSize { get; set; } = 512;

    /// <summary>
    /// Validates the option combination. Called during <see cref="QuiverDbContext"/> construction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="EntityCache"/> is <see cref="EntityCacheMode.LazyPaging"/> but <see cref="DatabasePath"/> is not set.
    /// </exception>
    internal void Validate()
    {
        if (EntityCache == EntityCacheMode.LazyPaging && string.IsNullOrEmpty(DatabasePath))
            throw new InvalidOperationException(
                $"{nameof(EntityCache)}.{nameof(EntityCacheMode.LazyPaging)} requires a valid {nameof(DatabasePath)} " +
                $"for the page cache directory.");
    }
}
