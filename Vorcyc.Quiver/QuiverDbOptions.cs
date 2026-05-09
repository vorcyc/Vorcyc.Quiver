namespace Vorcyc.Quiver;

/// <summary>
/// Global configuration options for the vector database.
/// <para>
/// This type covers general settings such as the database path, default distance metric, background
/// merge behavior, and dispose behavior. Vector-payload and large-field-payload memory strategies are
/// placed in <see cref="Vectors"/> and <see cref="LargeFields"/> respectively, to avoid mixing two
/// semantically distinct configuration surfaces at the same level.
/// </para>
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

    /// <summary>
    /// Vector-payload configuration: controls the default memory strategy and mmap auto-switch
    /// for <see cref="QuiverVectorAttribute"/> fields.
    /// </summary>
    public QuiverVectorOptions Vectors { get; set; } = new();

    /// <summary>
    /// Large-field-payload configuration: controls the default memory strategy and cache capacity
    /// for <see cref="QuiverLargeFieldAttribute"/> fields.
    /// </summary>
    public QuiverLargeFieldOptions LargeFields { get; set; } = new();

    // ── Background merge / compaction ──

    /// <summary>
    /// Whether <see cref="QuiverDbContext.AppendAsync"/> may schedule a background <see cref="QuiverDbContext.SaveAsync"/>
    /// once the file has accumulated enough segments or tombstones. Default is <c>false</c>.
    /// </summary>
    public bool EnableBackgroundMerge { get; set; } = false;

    /// <summary>
    /// Maximum number of v4 segments allowed before background merge is triggered. Default is 32.
    /// </summary>
    public int AutoMergeMaxSegments { get; set; } = 32;

    /// <summary>
    /// Maximum tombstoned/live entity ratio allowed before background merge is triggered.
    /// Range 0.0–1.0; default 0.25 (25%).
    /// </summary>
    public double AutoMergeTombstoneRatio { get; set; } = 0.25;

    // ── Dispose behavior ──

    /// <summary>
    /// Whether <see cref="QuiverDbContext"/> automatically calls <see cref="QuiverDbContext.SaveAsync"/>
    /// (a full snapshot rewrite) when <c>Dispose</c>/<c>DisposeAsync</c> is called.
    /// <para>
    /// Default is <c>false</c>. Note: <see cref="QuiverDbContext.SaveAsync"/> performs a full rewrite
    /// from the current in-memory snapshot. In bulk-import scenarios that use
    /// <see cref="QuiverDbContext.AppendAsync"/> with periodic <c>Clear()</c> calls to release memory,
    /// enabling this option would overwrite all appended data on disk with the current (possibly empty)
    /// in-memory snapshot. For that reason it is off by default; call
    /// <see cref="QuiverDbContext.SaveAsync"/> explicitly to persist data.
    /// </para>
    /// </summary>
    public bool SaveOnDispose { get; set; } = false;

    /// <summary>
    /// Whether to skip the consistency check between the entity schema fingerprint (FNV-1a 64-bit)
    /// stored in the v4 file header and the current CLR entity declaration when opening a file.
    /// <para>
    /// The current version is still in a transition period: the v4 file header does not yet persist
    /// the schema fingerprint (the header field is empty), so this option currently only affects the
    /// runtime-computed in-memory comparison. A future version will write the fingerprint to the file
    /// header; at that point, a mismatch with this option set to <see langword="false"/> will throw
    /// <see cref="Migration.QuiverSchemaMismatchException"/>.
    /// </para>
    /// <para>Default is <see langword="false"/> (check enabled).</para>
    /// </summary>
    public bool IgnoreSchemaFingerprintMismatch { get; set; } = false;

    /// <summary>
    /// Controls how <see cref="QuiverDbContext.LoadAsync"/> handles segments for unregistered types
    /// found in an opened v4 file.
    /// <para>
    /// An unregistered type is one whose <c>TypeName</c> in the v4 footer has no corresponding CLR
    /// type among the <c>QuiverSet&lt;T&gt;</c> properties of the current <see cref="QuiverDbContext"/>
    /// (e.g. after a namespace refactor, or when a v3-upgraded file has a stored TypeName that was
    /// not remapped by <c>QuiverMigrator</c>).
    /// </para>
    /// <para>
    /// Default is <see cref="UnknownTypeHandling.Warn"/>. During development it is recommended to
    /// set this to <see cref="UnknownTypeHandling.Throw"/> to avoid the subtle situation where a load
    /// appears to succeed but the data is silently empty.
    /// </para>
    /// </summary>
    public UnknownTypeHandling UnknownTypeHandling { get; set; } = UnknownTypeHandling.Warn;

    /// <summary>
    /// Validates the option combination. Called during <see cref="QuiverDbContext"/> construction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a selected memory mode requires <see cref="DatabasePath"/> but it is not set.
    /// </exception>
    internal void Validate()
    {
        if (LargeFields.MemoryMode is GlobalLargeFieldMemoryMode.LazyLoad or GlobalLargeFieldMemoryMode.PagedCache
            && string.IsNullOrEmpty(DatabasePath))
            throw new InvalidOperationException(
                $"{nameof(LargeFields)}.{nameof(QuiverLargeFieldOptions.MemoryMode)}.{LargeFields.MemoryMode} requires a valid {nameof(DatabasePath)} " +
                $"to back file-backed large-field payloads.");

        if (LargeFields.MaxCachedPayloads <= 0)
            throw new InvalidOperationException(
                $"{nameof(LargeFields)}.{nameof(QuiverLargeFieldOptions.MaxCachedPayloads)} must be greater than 0.");

        if (Vectors.MemoryMode == GlobalVectorMemoryMode.MemoryMapped && string.IsNullOrEmpty(DatabasePath))
            throw new InvalidOperationException(
                $"{nameof(Vectors)}.{nameof(QuiverVectorOptions.MemoryMode)}.{nameof(GlobalVectorMemoryMode.MemoryMapped)} requires a valid {nameof(DatabasePath)} " +
                $"to back the memory-mapped vector regions.");
    }
}

/// <summary>
/// Global configuration options for vector payloads.
/// </summary>
public sealed class QuiverVectorOptions
{
    /// <summary>
    /// The default memory strategy for <see cref="QuiverVectorAttribute"/> fields.
    /// </summary>
    public GlobalVectorMemoryMode MemoryMode { get; set; } = GlobalVectorMemoryMode.InMemory;

    /// <summary>
    /// Under <see cref="GlobalVectorMemoryMode.Auto"/>, the file-size threshold (in bytes) that
    /// triggers a switch to memory-mapped storage. The threshold is evaluated against the length of
    /// the existing database file at open time. When the file is at least this large,
    /// <see cref="QuiverSet{TEntity}"/> construction automatically selects
    /// <see cref="VectorMemoryMode.MemoryMapped"/>; otherwise it falls back to
    /// <see cref="VectorMemoryMode.InMemory"/>.
    /// <para>
    /// This is an <b>open-time</b> threshold and only applies when <see cref="MemoryMode"/> is
    /// <see cref="GlobalVectorMemoryMode.Auto"/>. It differs from the runtime managed-heap budget
    /// <see cref="MaxInMemoryBytes"/>.
    /// </para>
    /// Default is <c>256 MiB</c>.
    /// </summary>
    public long MemoryMapThresholdBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Runtime managed-heap vector byte budget. When the sum of
    /// <see cref="QuiverSet{TEntity}.HeapVectorBytes"/> across all sets exceeds this value
    /// and <see cref="AutoPromoteToMemoryMapped"/> is <c>true</c>, the next call to
    /// <see cref="QuiverDbContext.SaveAsync"/> will rewrite and rebind the current in-memory storage
    /// as memory-mapped.
    /// <para>
    /// This is a <b>runtime</b> memory-protection threshold measured against the current process
    /// heap vector usage; it differs from <see cref="MemoryMapThresholdBytes"/>, which is evaluated
    /// against the database file size at open time.
    /// </para>
    /// </summary>
    public long MaxInMemoryBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether to allow the runtime to automatically promote a <see cref="VectorMemoryMode.InMemory"/>
    /// store to <see cref="VectorMemoryMode.MemoryMapped"/>. Only takes effect when
    /// <see cref="QuiverDbOptions.DatabasePath"/> is set. Default is <c>false</c>.
    /// </summary>
    public bool AutoPromoteToMemoryMapped { get; set; } = false;
}

/// <summary>
/// Global configuration options for large-field payloads.
/// </summary>
public sealed class QuiverLargeFieldOptions
{
    /// <summary>
    /// The default memory strategy for <see cref="QuiverLargeFieldAttribute"/> fields.
    /// </summary>
    public GlobalLargeFieldMemoryMode MemoryMode { get; set; } = GlobalLargeFieldMemoryMode.InMemory;

    /// <summary>
    /// When <see cref="GlobalLargeFieldMemoryMode.PagedCache"/> is active, the maximum number of
    /// large-field payloads cached per <see cref="QuiverSet{TEntity}"/>.
    /// Default is <c>128</c>.
    /// </summary>
    public int MaxCachedPayloads { get; set; } = 128;
}
