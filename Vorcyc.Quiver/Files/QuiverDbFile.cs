using System.Reflection;
using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver.Files;

/// <summary>
/// Static utilities for <c>Quiver</c> v4 database files: merging multiple files, inspecting file structure, and reading diagnostic information.
/// <para>
/// All APIs operate at the file level — no <see cref="QuiverDbContext"/> instance is required and no data is loaded into memory.
/// Only <c>QDB\x04</c> files are supported; to upgrade from an older format, load the file with a context and call <c>SaveAsync</c> once first.
/// </para>
/// </summary>
public static class QuiverDbFile
{
    /// <summary>v4 写入版本魔术字节。</summary>
    private static readonly byte[] Magic = "QDB\x04"u8.ToArray();

    /// <summary>v4 footer 起始 magic（schema v1）。</summary>
    private static readonly byte[] FooterTopMagic = "QDBF"u8.ToArray();

    /// <summary>v4 footer 起始 magic（schema v2）。</summary>
    private static readonly byte[] FooterTopMagicV2 = "QDB2"u8.ToArray();

    /// <summary>v4 文件末尾 magic。</summary>
    private static readonly byte[] TrailerMagic = "QDBE"u8.ToArray();

    /// <summary>
    /// Merges multiple v4 files into a single new v4 file, using the <see cref="MergeOptions.ConflictPolicy"/>
    /// to determine segment deduplication strategy. Under <see cref="MergeConflictPolicy.Append"/> a pure
    /// byte-copy path is used — I/O complexity is O(total bytes) and no entities are decoded.
    /// </summary>
    /// <param name="sourceFiles">Input v4 file paths ordered by priority (lower-priority files first; under LWW later entries override earlier ones).</param>
    /// <param name="destinationFile">Output file path; overwritten if it already exists.</param>
    /// <param name="options">Merge options. May be <see langword="null"/> to use <see cref="MergeOptions"/> defaults.</param>
    /// <param name="typeMap">
    /// Required only for deduplication policies: maps type full name → CLR <see cref="Type"/>.
    /// Used to resolve the <see cref="QuiverKeyAttribute"/>-marked primary key property for identity comparison.
    /// </param>
    /// <exception cref="InvalidDataException">Any input file is not a v4 file, or a CRC mismatch is detected when <see cref="MergeOptions.VerifyCrc"/> is <see langword="true"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="typeMap"/> is not provided for a deduplication policy, or the source file sequence is empty.</exception>
    public static async Task MergeAsync(
        IEnumerable<string> sourceFiles,
        string destinationFile,
        MergeOptions? options = null,
        IReadOnlyDictionary<string, Type>? typeMap = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrEmpty(destinationFile);
        options ??= new MergeOptions();

        var files = sourceFiles.ToList();
        if (files.Count == 0)
            throw new ArgumentException("At least one source file is required.", nameof(sourceFiles));

        if (options.ConflictPolicy != MergeConflictPolicy.Append && typeMap is null)
            throw new ArgumentException(
                $"typeMap is required for {options.ConflictPolicy} policy (used to resolve primary keys).",
                nameof(typeMap));

        await Task.Run(() =>
        {
            if (options.ConflictPolicy == MergeConflictPolicy.Append)
                MergeAppend(files, destinationFile, options);
            else
                MergeDeduplicated(files, destinationFile, options, typeMap!);
        });
    }

    /// <summary>
    /// Reads diagnostic information from a v4 file: format version, segment list, per-segment CRC32 verification result, and per-type entity counts.
    /// No entity content is decoded. Time complexity is O(file size) when CRC verification is enabled because all segment bytes must be read.
    /// </summary>
    /// <param name="filePath">Path to the v4 file.</param>
    /// <param name="verifyCrc">Whether to compute and verify the CRC32 of every segment. Default is <see langword="true"/>.</param>
    public static async Task<QuiverFileInfo> InspectAsync(string filePath, bool verifyCrc = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            var fileSize = fs.Length;

            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4) throw new InvalidDataException("File too small.");
            int version =
                magic.SequenceEqual(Magic)        ? 4 :
                magic.SequenceEqual("QDB\x03"u8) ? 3 :
                magic.SequenceEqual("QDB\x02"u8) ? 2 :
                magic.SequenceEqual("QDB\x01"u8) ? 1 :
                throw new InvalidDataException("Not a Quiver database file.");

            if (version != 4)
            {
                // 旧版本：直接返回最小信息，段列表留空。
                return new QuiverFileInfo
                {
                    FilePath = filePath,
                    FormatVersion = version,
                    FileSize = fileSize,
                    Segments = Array.Empty<SegmentInfo>(),
                    CrcValid = true, // 旧版无段级 CRC
                    EntityCounts = new Dictionary<string, long>(),
                };
            }

            var (entries, _) = BinaryStorageProvider.ReadFooter(fs);
            var segments = new List<SegmentInfo>(entries.Count);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            var allValid = true;

            foreach (var e in entries)
            {
                uint actualCrc = e.Crc32;
                if (verifyCrc)
                {
                    actualCrc = Crc32Helper.ComputeFromStream(fs, e.Offset, e.Length);
                    if (actualCrc != e.Crc32) allValid = false;
                }
                segments.Add(new SegmentInfo(e.TypeName, e.Offset, e.Length, e.EntityCount, e.Crc32, actualCrc, (SegmentKind)(byte)e.Kind, e.FieldName, e.Dim));
                // 实体计数只在 Mixed / EntityMeta 段累加；VectorBlob / Blob / Tombstone 与同类型 EntityMeta 一一对应，避免重复计数。
                if (e.Kind == BinaryStorageProvider.SegmentKind.Mixed || e.Kind == BinaryStorageProvider.SegmentKind.EntityMeta)
                {
                    counts.TryGetValue(e.TypeName, out var c);
                    counts[e.TypeName] = c + e.EntityCount;
                }
            }

            return new QuiverFileInfo
            {
                FilePath = filePath,
                FormatVersion = 4,
                FileSize = fileSize,
                Segments = segments,
                CrcValid = allValid,
                EntityCounts = counts,
            };
        });
    }

    #region Append-policy merge (raw byte copy)

    /// <summary>Append 策略：原始字节拷贝所有段，footer 重建。</summary>
    private static void MergeAppend(List<string> files, string destinationFile, MergeOptions options)
    {
        var dir = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var dst = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        WriteHeader(dst);

        var newEntries = new List<FooterEntry>();
        int processedSegments = 0;
        long processedBytes = 0;
        int totalSegments = 0;

        // 预扫描得到总段数（用于进度条；轻量级，只读 footer）
        var perFileEntries = new List<(string Path, List<FooterEntry> Entries)>(files.Count);
        foreach (var f in files)
        {
            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            VerifyV4Magic(src, f);
            var (entries, _) = BinaryStorageProvider.ReadFooter(src);
            perFileEntries.Add((f, entries));
            totalSegments += entries.Count;
        }

        Span<byte> buffer = stackalloc byte[81920];
        var rentBuf = new byte[81920];

        foreach (var (path, entries) in perFileEntries)
        {
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            foreach (var e in entries)
            {
                if (options.VerifyCrc)
                {
                    var actual = Crc32Helper.ComputeFromStream(src, e.Offset, e.Length);
                    if (actual != e.Crc32)
                        throw new InvalidDataException(
                            $"CRC mismatch in source file '{path}' segment '{e.TypeName}' at offset {e.Offset}.");
                }

                var newOffset = dst.Position;
                CopyRange(src, dst, e.Offset, e.Length, rentBuf);
                newEntries.Add(new FooterEntry(e.TypeName, newOffset, e.Length, e.EntityCount, e.Crc32, e.Kind, e.FieldName, e.Dim, e.FirstId));

                processedSegments++;
                processedBytes += e.Length;
                options.Progress?.Report(new MergeProgress(processedSegments, totalSegments, processedBytes, path));
            }
        }

        WriteFooter(dst, newEntries);
    }

    /// <summary>从源流的 <paramref name="offset"/> 拷贝 <paramref name="length"/> 字节到目标流当前位置。</summary>
    private static void CopyRange(Stream src, Stream dst, long offset, long length, byte[] buffer)
    {
        src.Position = offset;
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = src.Read(buffer, 0, toRead);
            if (read == 0) throw new EndOfStreamException("Unexpected end of source file during merge copy.");
            dst.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    #endregion

    #region LWW / FWW policy merge (key-aware deduplication)

    /// <summary>LWW / FWW 策略：按 <see cref="QuiverKeyAttribute"/> 主键去重。</summary>
    /// <remarks>
    /// schema v2 起单个实体集合会被拆为 <c>EntityMeta</c> + 多个 <c>VectorBlob</c> 段。
    /// 这里通过 <see cref="BinaryStorageProvider"/> 的 <c>LoadAsync</c> 走完整解码路径整文件加载，
    /// 自动将向量回填到对应实体；未知类型仍按原始字节段透传到结果文件。
    /// </remarks>
    private static void MergeDeduplicated(
        List<string> files,
        string destinationFile,
        MergeOptions options,
        IReadOnlyDictionary<string, Type> typeMap)
    {
        var groups = new Dictionary<string, KeyedTypeGroup>(StringComparer.Ordinal);
        int totalSegments = 0;
        int processedSegments = 0;
        long processedBytes = 0;

        // 预统计 totalSegments 用于进度
        for (int idx = 0; idx < files.Count; idx++)
        {
            using var fsCount = new FileStream(files[idx], FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            VerifyV4Magic(fsCount, files[idx]);
            var (entriesCount, _) = BinaryStorageProvider.ReadFooter(fsCount);
            totalSegments += entriesCount.Count;
        }

        var provider = new BinaryStorageProvider();

        for (int idx = 0; idx < files.Count; idx++)
        {
            var path = files[idx];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            VerifyV4Magic(fs, path);
            var (entries, _) = BinaryStorageProvider.ReadFooter(fs);

            // CRC 校验（可选）
            if (options.VerifyCrc)
            {
                foreach (var e in entries)
                {
                    var actual = Crc32Helper.ComputeFromStream(fs, e.Offset, e.Length);
                    if (actual != e.Crc32)
                        throw new InvalidDataException(
                            $"CRC mismatch in source file '{path}' segment '{e.TypeName}' at offset {e.Offset}.");
                }
            }

            // 把未知类型按原始字节透传段收集起来
            foreach (var e in entries)
            {
                if (!typeMap.TryGetValue(e.TypeName, out _))
                {
                    if (!groups.TryGetValue(e.TypeName, out var passthrough))
                    {
                        passthrough = new KeyedTypeGroup(e.TypeName, clrType: null);
                        groups.Add(e.TypeName, passthrough);
                    }
                    passthrough.AddRawSegment(path, e);
                }
            }

            // 已知类型：用完整 Load 路径整文件加载（自动按 SegmentKind 配对回填向量）。
            fs.Dispose();
            var knownTypeMap = typeMap
                .Where(kv => entries.Any(e => e.TypeName == kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (knownTypeMap.Count > 0)
            {
                var loaded = provider.LoadAsync(path, knownTypeMap).GetAwaiter().GetResult();
                foreach (var (typeName, entities) in loaded)
                {
                    if (!typeMap.TryGetValue(typeName, out var clrType)) continue;
                    if (!groups.TryGetValue(typeName, out var group))
                    {
                        group = new KeyedTypeGroup(typeName, clrType);
                        groups.Add(typeName, group);
                    }
                    group.IngestEntities(entities, idx, options.ConflictPolicy);
                }
            }

            foreach (var e in entries)
            {
                processedSegments++;
                processedBytes += e.Length;
                options.Progress?.Report(new MergeProgress(processedSegments, totalSegments, processedBytes, path));
            }
        }

        // 写出
        var dir = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var dst = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        WriteHeader(dst);

        var newEntries = new List<FooterEntry>();
        var rentBuf = new byte[81920];
        foreach (var group in groups.Values)
        {
            if (group.IsPassthrough)
            {
                // 原样拷贝未知类型的所有段
                foreach (var (path, entry) in group.RawSegments)
                {
                    using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    var newOffset = dst.Position;
                    CopyRange(src, dst, entry.Offset, entry.Length, rentBuf);
                    newEntries.Add(new FooterEntry(entry.TypeName, newOffset, entry.Length, entry.EntityCount, entry.Crc32, entry.Kind, entry.FieldName, entry.Dim, entry.FirstId));
                }
            }
            else
            {
                var entities = group.MaterializeEntities();
                if (entities.Count == 0) continue;
                var written = BinaryStorageProvider.WriteSegmentPublic(dst, group.TypeName, group.ClrType!, entities);
                newEntries.AddRange(written);
            }
        }

        WriteFooter(dst, newEntries);
    }

    /// <summary>
    /// 按主键聚合单个类型在多个段中出现的实体集合。
    /// </summary>
    private sealed class KeyedTypeGroup(string typeName, Type? clrType)
    {
        public string TypeName { get; } = typeName;
        public Type? ClrType { get; } = clrType;
        public bool IsPassthrough => ClrType is null;

        private readonly Dictionary<object, (int SourceIndex, object Entity)> _byKey = new();
        private readonly List<(string Path, FooterEntry Entry)> _rawSegments = new();
        private PropertyInfo? _keyProp;

        public IReadOnlyList<(string Path, FooterEntry Entry)> RawSegments => _rawSegments;

        public void AddRawSegment(string path, FooterEntry entry)
            => _rawSegments.Add((path, entry));

        public void LoadFromSegment(FileStream fs, FooterEntry entry, int sourceIndex, MergeConflictPolicy policy)
        {
            _keyProp ??= ResolveKeyProperty(ClrType!);

            // 通过 BinaryStorageProvider 的解码器一次性把段内实体解出来。
            var typeMap = new Dictionary<string, Type>(StringComparer.Ordinal) { [TypeName] = ClrType! };
            var partial = new Dictionary<string, List<object>>();
            fs.Position = entry.Offset;
            using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
            BinaryStorageProvider.ReadSegmentPublic(br, typeMap, migrationRules: null, partial);
            if (!partial.TryGetValue(TypeName, out var entities)) return;

            IngestEntities(entities, sourceIndex, policy);
        }

        /// <summary>把已经解码好的实体集合按主键去重纳入分组（供 schema v2 整文件 Load 路径使用）。</summary>
        public void IngestEntities(IEnumerable<object> entities, int sourceIndex, MergeConflictPolicy policy)
        {
            _keyProp ??= ResolveKeyProperty(ClrType!);
            foreach (var entity in entities)
            {
                if (entity is null) continue; // tombstoned slot in source file
                var key = _keyProp.GetValue(entity);
                if (key is null) continue;
                if (_byKey.TryGetValue(key, out var existing))
                {
                    if (policy == MergeConflictPolicy.LastWriterWins && sourceIndex >= existing.SourceIndex)
                        _byKey[key] = (sourceIndex, entity);
                }
                else
                {
                    _byKey[key] = (sourceIndex, entity);
                }
            }
        }

        public List<object> MaterializeEntities()
            => _byKey.Values.Select(v => v.Entity).ToList();

        private static PropertyInfo ResolveKeyProperty(Type type)
        {
            var key = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .FirstOrDefault(p => p.GetCustomAttribute<QuiverKeyAttribute>() != null);
            return key ?? throw new InvalidOperationException(
                $"Type '{type.FullName}' has no [QuiverKey] property; cannot apply deduplication merge policy.");
        }
    }

    #endregion

    #region Footer / header helpers (mirrors of BinaryStorageProvider)

    private static void WriteHeader(Stream fs)
    {
        fs.Write(Magic);
        Span<byte> headerLen = stackalloc byte[4];
        BitConverter.TryWriteBytes(headerLen, 0);
        fs.Write(headerLen);
    }

    private static void WriteFooter(Stream fs, List<FooterEntry> entries)
    {
        var footerOffset = fs.Position;
        using var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(FooterTopMagicV2);
        bw.Write(entries.Count);
        foreach (var e in entries)
        {
            bw.Write(e.TypeName);
            bw.Write(e.Offset);
            bw.Write(e.Length);
            bw.Write(e.EntityCount);
            bw.Write(e.Crc32);
            bw.Write((byte)e.Kind);
            bw.Write(e.FieldName ?? string.Empty);
            bw.Write(e.Dim);
            bw.Write(e.FirstId);
        }
        bw.Write(footerOffset);
        bw.Write(TrailerMagic);
        bw.Flush();
    }

    private static void VerifyV4Magic(Stream fs, string filePath)
    {
        fs.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4 || !magic.SequenceEqual(Magic))
            throw new InvalidDataException($"File '{filePath}' is not a v4 (QDB\\x04) Quiver database.");
    }

    #endregion
}
