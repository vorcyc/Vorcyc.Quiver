using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Vorcyc.Quiver.Storage;

/// <summary>UTF-8 Base64 helpers for <see cref="System.Text.Json"/> vector converters.</summary>
internal static class JsonUtf8Base64
{
    private const int StackScratchLimit = 4096;

    internal static ReadOnlySpan<byte> GetUtf8String(ref Utf8JsonReader reader, ref byte[]? rented)
    {
        if (!reader.HasValueSequence)
            return reader.ValueSpan;

        int length = checked((int)reader.ValueSequence.Length);
        rented = ArrayPool<byte>.Shared.Rent(length);
        reader.ValueSequence.CopyTo(rented);
        return rented.AsSpan(0, length);
    }

    internal static void ReturnRented(byte[]? rented)
    {
        if (rented != null)
            ArrayPool<byte>.Shared.Return(rented);
    }

    internal static float[] DecodeFloatArray(ReadOnlySpan<byte> base64Utf8)
    {
        int maxBytes = Base64.GetMaxDecodedFromUtf8Length(base64Utf8.Length);
        Span<byte> scratch = maxBytes <= StackScratchLimit
            ? stackalloc byte[maxBytes]
            : new byte[maxBytes];

        OperationStatus status = Base64.DecodeFromUtf8(base64Utf8, scratch, out _, out int bytesWritten);
        if (status != OperationStatus.Done)
            throw new JsonException("Invalid Base64 float[] payload.");

        var arr = new float[bytesWritten / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(scratch[..bytesWritten]).CopyTo(arr);
        return arr;
    }

    internal static Half[] DecodeHalfArray(ReadOnlySpan<byte> base64Utf8)
    {
        int maxBytes = Base64.GetMaxDecodedFromUtf8Length(base64Utf8.Length);
        Span<byte> scratch = maxBytes <= StackScratchLimit
            ? stackalloc byte[maxBytes]
            : new byte[maxBytes];

        OperationStatus status = Base64.DecodeFromUtf8(base64Utf8, scratch, out _, out int bytesWritten);
        if (status != OperationStatus.Done)
            throw new JsonException("Invalid Base64 Half[] payload.");

        return MemoryMarshal.Cast<byte, Half>(scratch[..bytesWritten]).ToArray();
    }
}
