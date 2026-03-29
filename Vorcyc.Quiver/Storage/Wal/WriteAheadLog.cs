using System.IO.Hashing;

namespace Vorcyc.Quiver.Storage.Wal;

/// <summary>
/// WAL（Write-Ahead Log）文件读写引擎。
/// <para>
/// 采用自定义紧凑二进制格式，每条记录附带 CRC32 校验，确保崩溃后能检测并跳过损坏的尾部记录。
/// </para>
/// <para>
/// <b>二进制文件格式</b>：
/// <code>
/// [Header]
///   [4B] Magic = "WLOG"
///   [1B] Version = 0x01
///
/// [Record]*
///   [4B uint32] 记录数据长度（SeqNo 到 Payload，不含 CRC）
///   [8B int64]  序列号（单调递增）
///   [1B]        操作码（Add=1, Remove=2, Clear=3）
///   [string]    TypeName（BinaryWriter 长度前缀 UTF-8）
///   [string]    PayloadJson（BinaryWriter 长度前缀 UTF-8）
///   [4B uint32] CRC32（覆盖 SeqNo 到 Payload 的全部字节）
/// </code>
/// </para>
/// <para>
/// <b>线程安全</b>：所有写操作通过 <see cref="_writeLock"/> 串行化。
/// 读操作（<see cref="ReadAll"/>）使用独立的文件流，不与写操作竞争。
/// </para>
/// </summary>
internal sealed class WriteAheadLog : IDisposable
{
    /// <summary>文件头魔术字节 <c>"WLOG"</c>（4 字节）。</summary>
    private static readonly byte[] Magic = "WLOG"u8.ToArray();

    /// <summary>协议版本号。</summary>
    private const byte Version = 0x01;

    /// <summary>文件头总长度：4（Magic）+ 1（Version）= 5 字节。</summary>
    private const int HeaderSize = 5;

    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly Lock _writeLock = new();
    private long _nextSeqNo;
    private long _recordCount;
    private bool _disposed;

    /// <summary>WAL 文件路径。</summary>
    public string FilePath { get; }

    /// <summary>当前 WAL 中的有效记录数。线程安全读取。</summary>
    public long RecordCount => Volatile.Read(ref _recordCount);

    /// <summary>
    /// 打开或创建 WAL 文件。若文件已存在则扫描已有记录以恢复序列号和计数器。
    /// </summary>
    /// <param name="filePath">WAL 文件路径。</param>
    public WriteAheadLog(string filePath)
    {
        FilePath = filePath;

        // 确保目录存在
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var exists = File.Exists(filePath) && new FileInfo(filePath).Length >= HeaderSize;

        _stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536);
        _writer = new BinaryWriter(_stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (exists)
        {
            // 扫描已有记录，恢复 SeqNo 和记录计数
            (_nextSeqNo, _recordCount) = ScanExisting();
        }
        else
        {
            // 写入文件头
            _writer.Write(Magic);
            _writer.Write(Version);
            _writer.Flush();
        }
    }

    /// <summary>
    /// 批量追加 WAL 记录。所有记录在单次锁内写入并 flush，保证批次原子性。
    /// </summary>
    /// <param name="entries">要追加的记录列表。空列表时直接返回。</param>
    /// <param name="flushToDisk">是否 fsync 到磁盘（<c>true</c> = 最强持久性）。</param>
    public void Append(IReadOnlyList<WalEntry> entries, bool flushToDisk = true)
    {
        if (entries.Count == 0) return;

        lock (_writeLock)
        {
            foreach (var entry in entries)
            {
                // 先将记录数据写入临时 MemoryStream，用于计算长度和 CRC
                using var ms = new MemoryStream(256);
                using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(_nextSeqNo);
                    bw.Write((byte)entry.Operation);
                    bw.Write(entry.TypeName);
                    bw.Write(entry.PayloadJson);
                    bw.Flush();
                }

                var data = ms.ToArray();
                var crc = Crc32.HashToUInt32(data);

                // 写入：[数据长度][数据][CRC32]
                _writer.Write(data.Length);
                _writer.Write(data);
                _writer.Write(crc);

                _nextSeqNo++;
                Interlocked.Increment(ref _recordCount);
            }

            _writer.Flush();
            if (flushToDisk)
                _stream.Flush(flushToDisk: true); // fsync
        }
    }

    /// <summary>
    /// 从 WAL 文件中读取所有有效记录。遇到 CRC 校验失败或截断的记录时停止读取（崩溃恢复安全）。
    /// <para>
    /// 使用独立的只读文件流，不影响写操作。
    /// </para>
    /// </summary>
    /// <param name="filePath">WAL 文件路径。</param>
    /// <returns>有效的 WAL 记录列表，按写入顺序排列。文件不存在时返回空列表。</returns>
    public static List<WalEntry> ReadAll(string filePath)
    {
        if (!File.Exists(filePath) || new FileInfo(filePath).Length < HeaderSize)
            return [];

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        // 校验文件头
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) < 4 || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("无效的 WAL 文件头。");
        var version = br.ReadByte();
        if (version != Version)
            throw new InvalidDataException($"不支持的 WAL 版本 {version}。");

        var entries = new List<WalEntry>();

        while (fs.Position + 4 <= fs.Length) // 至少需要 4 字节的长度前缀
        {
            var startPos = fs.Position;
            try
            {
                var dataLen = br.ReadInt32();
                if (dataLen <= 0 || fs.Position + dataLen + 4 > fs.Length)
                    break; // 记录不完整（崩溃截断）

                var data = br.ReadBytes(dataLen);
                var crc = br.ReadUInt32();

                // CRC32 校验：损坏的记录及之后的所有记录都丢弃
                if (Crc32.HashToUInt32(data) != crc)
                    break;

                // 反序列化记录数据
                using var ms = new MemoryStream(data);
                using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);

                _ = reader.ReadInt64(); // seqNo（当前仅用于排序保证，不返回）
                var op = (WalOperation)reader.ReadByte();
                var typeName = reader.ReadString();
                var payload = reader.ReadString();

                entries.Add(new WalEntry(op, typeName, payload));
            }
            catch (EndOfStreamException)
            {
                break; // 写入过程中崩溃导致的截断记录
            }
        }

        return entries;
    }

    /// <summary>
    /// 清空 WAL 文件，仅保留文件头。用于压缩完成后重置 WAL。
    /// </summary>
    public void Truncate()
    {
        lock (_writeLock)
        {
            _stream.SetLength(0);
            _stream.Position = 0;
            _writer.Write(Magic);
            _writer.Write(Version);
            _writer.Flush();
            _stream.Flush(flushToDisk: true);
            _nextSeqNo = 0;
            Interlocked.Exchange(ref _recordCount, 0);
        }
    }

    /// <summary>
    /// 扫描已有 WAL 文件中的有效记录，恢复序列号计数器和记录总数。
    /// 扫描完成后将文件指针定位到末尾，准备追加写入。
    /// </summary>
    private (long NextSeqNo, long RecordCount) ScanExisting()
    {
        _stream.Position = 0;
        using var br = new BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // 跳过文件头
        Span<byte> magic = stackalloc byte[4];
        _stream.Read(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("无效的 WAL 文件头。");
        br.ReadByte(); // version

        long maxSeqNo = -1;
        long count = 0;
        long lastValidPos = _stream.Position;

        while (_stream.Position + 4 <= _stream.Length)
        {
            try
            {
                var dataLen = br.ReadInt32();
                if (dataLen <= 0 || _stream.Position + dataLen + 4 > _stream.Length)
                    break;

                var data = br.ReadBytes(dataLen);
                var crc = br.ReadUInt32();

                if (Crc32.HashToUInt32(data) != crc)
                    break; // CRC 失败，之后的记录不可信

                // 仅读取 SeqNo
                maxSeqNo = BitConverter.ToInt64(data, 0);
                count++;
                lastValidPos = _stream.Position;
            }
            catch (EndOfStreamException)
            {
                break;
            }
        }

        // 截断尾部损坏数据，定位到最后一条有效记录之后
        _stream.SetLength(lastValidPos);
        _stream.Position = lastValidPos;

        return (maxSeqNo + 1, count);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _writer.Dispose();
            _stream.Dispose();
            _disposed = true;
        }
    }
}