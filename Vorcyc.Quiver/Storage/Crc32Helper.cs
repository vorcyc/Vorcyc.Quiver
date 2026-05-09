namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 静态 CRC32 工具，使用 IEEE 802.3 多项式并保持与 System.IO.Hashing.Crc32 相同的字节序。
/// </summary>
internal static class Crc32Helper
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>一次性计算字节区间的 CRC32（IEEE 802.3 多项式）。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var state = uint.MaxValue;
        AppendCore(ref state, data);
        return ~state;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static void AppendCore(ref uint state, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
    }

    /// <summary>
    /// 写入侧 CRC 跟踪流：透传所有写入到底层流，同时对写入的字节维护增量 CRC32。
    /// 通过 <see cref="Reset"/> 在段边界重置哈希器；通过 <see cref="GetCurrent"/> 取当前段的 CRC。
    /// </summary>
    internal sealed class CrcTrackingStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;
        private uint _state = uint.MaxValue;
        public void Reset() => _state = uint.MaxValue;
        public uint GetCurrent() => ~_state;
        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            AppendCore(ref _state, buffer.AsSpan(offset, count));
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            AppendCore(ref _state, buffer);
        }
        public override void WriteByte(byte value)
        {
            _inner.WriteByte(value);
            Span<byte> one = stackalloc byte[1] { value };
            AppendCore(ref _state, one);
        }
        protected override void Dispose(bool disposing)
        {
            // do not dispose inner — caller owns it
            base.Dispose(disposing);
        }
    }

    /// <summary>读取文件中 [offset, offset+length) 区间的 CRC32。</summary>
    public static uint ComputeFromStream(Stream stream, long offset, long length)
    {
        var savedPos = stream.Position;
        stream.Position = offset;
        try
        {
            var state = uint.MaxValue;
            Span<byte> buffer = stackalloc byte[8192];
            long remaining = length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer[..toRead]);
                if (read == 0) throw new EndOfStreamException("Unexpected end of stream while computing CRC32.");
                AppendCore(ref state, buffer[..read]);
                remaining -= read;
            }
            return ~state;
        }
        finally
        {
            stream.Position = savedPos;
        }
    }
}
