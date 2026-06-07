using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// Compact JSON encoding for <c>float[]</c> vector fields: Base64 of raw IEEE-754 bytes.
/// Reads legacy JSON number arrays for backward compatibility.
/// </summary>
internal sealed class FloatArrayJsonConverter : JsonConverter<float[]>
{
    public override float[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            byte[]? rented = null;
            try
            {
                return JsonUtf8Base64.DecodeFloatArray(JsonUtf8Base64.GetUtf8String(ref reader, ref rented));
            }
            finally
            {
                JsonUtf8Base64.ReturnRented(rented);
            }
        }

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected Base64 string or JSON array for float[].");

        var list = new List<float>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return [.. list];
                case JsonTokenType.Number:
                    list.Add(reader.GetSingle());
                    break;
                default:
                    throw new JsonException("Invalid token in legacy float[] array.");
            }
        }

        throw new JsonException("Unexpected end of JSON while reading float[].");
    }

    public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteBase64StringValue(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}

/// <summary>
/// Compact JSON encoding for <c>Half[]</c> vector fields: Base64 of raw fp16 bytes.
/// Reads legacy JSON number arrays for backward compatibility.
/// </summary>
internal sealed class HalfArrayJsonConverter : JsonConverter<Half[]>
{
    public override Half[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            byte[]? rented = null;
            try
            {
                return JsonUtf8Base64.DecodeHalfArray(JsonUtf8Base64.GetUtf8String(ref reader, ref rented));
            }
            finally
            {
                JsonUtf8Base64.ReturnRented(rented);
            }
        }

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected Base64 string or JSON array for Half[].");

        var list = new List<Half>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return [.. list];
                case JsonTokenType.Number:
                    list.Add((Half)reader.GetSingle());
                    break;
                default:
                    throw new JsonException("Invalid token in legacy Half[] array.");
            }
        }

        throw new JsonException("Unexpected end of JSON while reading Half[].");
    }

    public override void Write(Utf8JsonWriter writer, Half[] value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteBase64StringValue(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
