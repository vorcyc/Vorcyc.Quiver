using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Vorcyc.Quiver.Migration;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// JSON 格式的存储提供者，实现 <see cref="IStorageProvider"/> 接口。
/// </summary>
internal class JsonExportProvider(JsonSerializerOptions jsonOptions) : IStorageProvider
{
    private readonly JsonSerializerOptions _jsonOptions = jsonOptions;

    /// <summary>在途实体 JSON 字节的软上限，用于限制 Channel 背压时的峰值内存。</summary>
    private const long MaxInFlightBytes = 64L * 1024 * 1024;

    public async Task SaveAsync(string filePath, IReadOnlyDictionary<string, (Type Type, List<object> Entities)> sets)
    {
        const int flushThreshold = 256 * 1024;

        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true);

        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = _jsonOptions.WriteIndented
        });

        writer.WriteStartObject();

        foreach (var (setName, (entityType, entities)) in sets)
        {
            writer.WritePropertyName(setName);
            writer.WriteStartArray();

            foreach (var entity in entities)
            {
                JsonSerializer.Serialize(writer, entity, entityType, _jsonOptions);

                if (writer.BytesPending >= flushThreshold)
                    await writer.FlushAsync();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        await writer.FlushAsync();
    }

    private readonly record struct RawEntityWork(
        string SetName,
        Type EntityType,
        SchemaMigrationRule? Rule,
        byte[] Buffer,
        int Length);

    public async Task<Dictionary<string, List<object>>> LoadAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules = null)
    {
        int workerCount = Math.Max(1, Environment.ProcessorCount);
        int channelCapacity = Math.Max(4, workerCount * 2);
        var channel = Channel.CreateBounded<RawEntityWork>(new BoundedChannelOptions(channelCapacity)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var bags = new ConcurrentDictionary<string, ConcurrentBag<object>>(StringComparer.Ordinal);
        long inFlightBytes = 0;

        using var cts = new CancellationTokenSource();

        var workerTasks = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
            workerTasks[i] = Task.Run(() => RunWorker(channel.Reader, bags, _jsonOptions, cts.Token,
                bytes => Interlocked.Add(ref inFlightBytes, -bytes)));

        Exception? scannerException = null;
        try
        {
            await ScanAsync(filePath, typeMap, migrationRules, channel.Writer, cts.Token,
                onPost: work => Interlocked.Add(ref inFlightBytes, work.Length),
                canPost: () => Interlocked.Read(ref inFlightBytes) < MaxInFlightBytes);
        }
        catch (JsonException ex)
        {
            long fileSize = 0;
            try { fileSize = new FileInfo(filePath).Length; } catch { /* best effort */ }
            scannerException = new InvalidDataException(
                $"JSON 文件不完整或已损坏（常见于导出中断）。文件: {filePath}，大小: {fileSize:N0} 字节。请重新 Export 或改用完整的 XML/二进制备份。",
                ex);
            cts.Cancel();
        }
        catch (Exception ex)
        {
            scannerException = ex;
            cts.Cancel();
        }
        finally
        {
            channel.Writer.TryComplete(scannerException);
        }

        try
        {
            await Task.WhenAll(workerTasks);
        }
        catch
        {
            if (scannerException != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(scannerException).Throw();
            throw;
        }

        if (scannerException != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(scannerException).Throw();

        var result = new Dictionary<string, List<object>>(bags.Count, StringComparer.Ordinal);
        foreach (var (key, bag) in bags)
            result[key] = [.. bag];

        return result;
    }

    private static async Task ScanAsync(
        string filePath,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        ChannelWriter<RawEntityWork> writer,
        CancellationToken ct,
        Action<RawEntityWork> onPost,
        Func<bool> canPost)
    {
        const int readBufferSize = 4 * 1024 * 1024;

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: readBufferSize,
            useAsync: true);

        var pipeReader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: readBufferSize));
        var jsonReaderState = new JsonReaderState();

        bool rootStarted = false;
        bool rootEnded = false;

        string? currentSetName = null;
        Type? currentType = null;
        SchemaMigrationRule? currentRule = null;

        var pending = new List<RawEntityWork>(64);

        try
        {
            var nextRead = pipeReader.ReadAsync(ct);

            while (!rootEnded)
            {
                ct.ThrowIfCancellationRequested();

                ReadResult readResult = await nextRead;
                ReadOnlySequence<byte> buffer = readResult.Buffer;

                pending.Clear();
                ScanBuffer(
                    buffer, readResult.IsCompleted,
                    ref jsonReaderState,
                    ref rootStarted, ref rootEnded,
                    ref currentSetName, ref currentType, ref currentRule,
                    typeMap, migrationRules, pending,
                    out long bytesConsumed);

                pipeReader.AdvanceTo(buffer.GetPosition(bytesConsumed), buffer.End);

                if (!readResult.IsCompleted && !rootEnded)
                    nextRead = pipeReader.ReadAsync(ct);

                foreach (var work in pending)
                {
                    while (!canPost())
                        await Task.Yield();

                    if (!writer.TryWrite(work))
                        await writer.WriteAsync(work, ct);

                    onPost(work);
                }

                if (readResult.IsCompleted)
                {
                    if (!rootStarted)
                        throw new InvalidDataException("JSON 文件为空，或未包含有效的根对象。");
                    if (!rootEnded)
                        throw new InvalidDataException("JSON 文件结构不完整，未正确结束。");
                    break;
                }
            }
        }
        finally
        {
            await pipeReader.CompleteAsync();
        }
    }

    private static void ScanBuffer(
        ReadOnlySequence<byte> buffer,
        bool isFinalBlock,
        ref JsonReaderState jsonReaderState,
        ref bool rootStarted,
        ref bool rootEnded,
        ref string? currentSetName,
        ref Type? currentType,
        ref SchemaMigrationRule? currentRule,
        IReadOnlyDictionary<string, Type> typeMap,
        IReadOnlyDictionary<string, SchemaMigrationRule>? migrationRules,
        List<RawEntityWork> pending,
        out long bytesConsumed)
    {
        var reader = new Utf8JsonReader(buffer, isFinalBlock, jsonReaderState);

        while (true)
        {
            var before = reader;

            if (!reader.Read())
                break;

            if (!rootStarted)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new InvalidDataException("JSON 根节点必须是对象。");
                rootStarted = true;
                continue;
            }

            if (currentSetName == null)
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    rootEnded = true;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new InvalidDataException("JSON 根对象中的成员名称无效。");

                currentSetName = reader.GetString()
                    ?? throw new InvalidDataException("集合名称不能为空。");

                if (!reader.Read())
                {
                    reader = before;
                    break;
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new InvalidDataException($"集合 '{currentSetName}' 的值不是数组。");

                if (!typeMap.TryGetValue(currentSetName, out currentType))
                    currentType = null;

                currentRule = null;
                if (currentType != null && migrationRules != null)
                    migrationRules.TryGetValue(currentSetName, out currentRule);

                continue;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                currentSetName = null;
                currentType = null;
                currentRule = null;
                continue;
            }

            if (currentType == null)
            {
                if (!TrySkip(ref reader)) { reader = before; break; }
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("集合数组中的元素必须是 JSON 对象。");

            long entityStart = (long)reader.TokenStartIndex;
            if (!TrySkip(ref reader)) { reader = before; break; }

            int entityLength = (int)((long)reader.BytesConsumed - entityStart);
            var rented = ArrayPool<byte>.Shared.Rent(entityLength);
            buffer.Slice(entityStart, entityLength).CopyTo(rented);
            pending.Add(new RawEntityWork(currentSetName!, currentType, currentRule, rented, entityLength));
        }

        jsonReaderState = reader.CurrentState;
        bytesConsumed = reader.BytesConsumed;
    }

    private static bool TrySkip(ref Utf8JsonReader reader) => reader.TrySkip();

    private static async Task RunWorker(
        ChannelReader<RawEntityWork> channelReader,
        ConcurrentDictionary<string, ConcurrentBag<object>> bags,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct,
        Action<int> releaseBytes)
    {
        await foreach (var work in channelReader.ReadAllAsync(ct))
        {
            try
            {
                var span = work.Buffer.AsSpan(0, work.Length);
                object? entity;

                if (work.Rule != null && work.Rule.PropertyRenames.Count > 0)
                {
                    using var doc = JsonDocument.Parse(work.Buffer.AsMemory(0, work.Length));
                    var node = JsonObject.Create(doc.RootElement)
                               ?? throw new InvalidDataException("JSON 元素不是有效对象。");
                    ApplyPropertyRenames(node, work.Rule, jsonOptions);
                    entity = node.Deserialize(work.EntityType, jsonOptions);
                }
                else
                {
                    entity = JsonSerializer.Deserialize(span, work.EntityType, jsonOptions);
                }

                if (entity != null)
                    bags.GetOrAdd(work.SetName, _ => new ConcurrentBag<object>()).Add(entity);
            }
            finally
            {
                releaseBytes(work.Length);
                ArrayPool<byte>.Shared.Return(work.Buffer);
            }
        }
    }

    private static void ApplyPropertyRenames(
        JsonObject node,
        SchemaMigrationRule rule,
        JsonSerializerOptions options)
    {
        var namingPolicy = options.PropertyNamingPolicy;

        foreach (var (oldName, newName) in rule.PropertyRenames)
        {
            var oldJsonName = namingPolicy?.ConvertName(oldName) ?? oldName;
            var newJsonName = namingPolicy?.ConvertName(newName) ?? newName;

            if (!node.ContainsKey(oldJsonName))
                continue;

            var value = node[oldJsonName];
            node.Remove(oldJsonName);
            node[newJsonName] = value?.DeepClone();
        }
    }
}
