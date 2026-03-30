using System.Diagnostics;
using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 10-11：文件大小对比 + 新增属性类型往返测试。</summary>
public static class StorageTests
{
    public static async Task RunAsync()
    {
        await Test10_FileSizeComparison();
        await Test11_RichTypeRoundTrip();
    }

    // ==================== 10. 文件大小对比 ====================
    private static async Task Test10_FileSizeComparison()
    {
        Console.WriteLine("\n═══ 10. 三种格式文件大小对比（2,000 条 × 3 向量）═══");

        var sizes = new long[Formats.Length];
        for (int f = 0; f < Formats.Length; f++)
        {
            var format = Formats[f];
            var path = $"test_size{Extensions[f]}";
            var random = new Random(42);

            var db = new MyMultiVectorDb(path, format);
            for (int i = 0; i < 2000; i++)
            {
                db.Items.Add(new MultiVectorEntity
                {
                    Id = $"Z{i:D4}",
                    Label = $"用户{i}",
                    Score = i,
                    IsActive = true,
                    TextEmbedding = RandomVector(random, 384),
                    ImageEmbedding = RandomVector(random, 512),
                    AudioEmbedding = RandomVector(random, 256)
                });
            }
            await db.SaveAsync();
            sizes[f] = new FileInfo(path).Length;
            File.Delete(path);
        }

        Console.WriteLine($"  JSON:   {sizes[0],14:N0} bytes");
        Console.WriteLine($"  XML:    {sizes[1],14:N0} bytes");
        Console.WriteLine($"  Binary: {sizes[2],14:N0} bytes");
        Console.WriteLine($"  压缩比：JSON/Binary = {(double)sizes[0] / sizes[2]:F1}x, XML/Binary = {(double)sizes[1] / sizes[2]:F1}x");
        Assert(sizes[2] < sizes[0] && sizes[2] < sizes[1], "Binary 格式文件体积最小");
    }

    // ==================== 11. 新增属性类型往返测试（Binary 格式）====================
    private static async Task Test11_RichTypeRoundTrip()
    {
        Console.WriteLine("\n═══ 11. 新增属性类型往返测试（byte/short/Half/DateTimeOffset/TimeSpan/byte[]/double[]）═══");

        var path = "test_rich_types.vdb";
        var random = new Random(42);

        const int entityCount = 500;
        var dbWrite = new MyRichTypeDb(path, StorageFormat.Binary);

        var originals = new RichTypeEntity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            originals[i] = new RichTypeEntity
            {
                Id = $"RT{i:D5}",
                ByteVal = (byte)(i % 256),
                ShortVal = (short)(i - 250),
                HalfVal = (Half)(i * 0.1f),
                OffsetTime = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.FromHours(i % 24 - 12)),
                Duration = TimeSpan.FromMinutes(i * 1.5),
                Blob = Enumerable.Range(0, 32).Select(j => (byte)((i + j) % 256)).ToArray(),
                Weights = Enumerable.Range(0, 64).Select(j => random.NextDouble() * 2 - 1).ToArray(),
                Embedding = RandomVector(random, 128)
            };
            dbWrite.RichItems.Add(originals[i]);
        }

        var sw = Stopwatch.StartNew();
        await dbWrite.SaveAsync();
        var saveMs = sw.ElapsedMilliseconds;
        var fileSize = new FileInfo(path).Length;

        sw.Restart();
        var dbRead = new MyRichTypeDb(path, StorageFormat.Binary);
        await dbRead.LoadAsync();
        var loadMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"  保存 {saveMs}ms / 加载 {loadMs}ms / 文件 {fileSize:N0} bytes");

        Assert(dbRead.RichItems.Count == entityCount,
            $"[Binary] 新类型实体数量：{dbRead.RichItems.Count}/{entityCount}");

        // ── 逐实体、逐字段精度校验 ──
        var allMatch = true;
        for (int i = 0; i < entityCount; i++)
        {
            var orig = originals[i];
            var loaded = dbRead.RichItems.Find(orig.Id);
            if (loaded == null ||
                loaded.ByteVal != orig.ByteVal ||
                loaded.ShortVal != orig.ShortVal ||
                loaded.HalfVal != orig.HalfVal ||
                loaded.OffsetTime != orig.OffsetTime ||
                loaded.Duration != orig.Duration)
            { allMatch = false; break; }

            // byte[] 精确匹配
            if (!loaded.Blob.AsSpan().SequenceEqual(orig.Blob))
            { allMatch = false; break; }

            // double[] 零拷贝往返应 bit-for-bit 一致
            if (loaded.Weights.Length != orig.Weights.Length)
            { allMatch = false; break; }
            for (int j = 0; j < orig.Weights.Length; j++)
            {
                if (loaded.Weights[j] != orig.Weights[j])
                { allMatch = false; break; }
            }
            if (!allMatch) break;

            // float[] 向量
            for (int j = 0; j < 128; j++)
            {
                if (MathF.Abs(loaded.Embedding[j] - orig.Embedding[j]) > 1e-6f)
                { allMatch = false; break; }
            }
            if (!allMatch) break;
        }
        Assert(allMatch, $"[Binary] 全部 {entityCount} 条七种新类型字段逐字段精度校验通过");

        // ── 边界值验证 ──
        var first = dbRead.RichItems.Find("RT00000")!;
        Assert(first.ByteVal == 0 && first.ShortVal == -250 && first.HalfVal == (Half)0f,
            "[Binary] 边界值：byte=0, short=-250, Half=0");

        var e255 = dbRead.RichItems.Find("RT00255")!;
        Assert(e255.ByteVal == 255, "[Binary] byte=255 上界正确");

        var last = originals[^1];
        var loadedLast = dbRead.RichItems.Find(last.Id)!;
        Assert(loadedLast.OffsetTime.Offset == last.OffsetTime.Offset,
            $"[Binary] DateTimeOffset 时区偏移保留正确（{last.OffsetTime.Offset}）");
        Assert(loadedLast.Duration == last.Duration,
            $"[Binary] TimeSpan 精度正确（{last.Duration}）");

        // ── Upsert 新类型实体 ──
        random = new Random(42);
        dbRead.RichItems.Upsert(new RichTypeEntity
        {
            Id = "RT00000",
            ByteVal = 128,
            ShortVal = short.MaxValue,
            HalfVal = Half.MaxValue,
            OffsetTime = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromDays(365),
            Blob = [0xFF, 0x00, 0xAB],
            Weights = [double.MinValue, 0, double.MaxValue],
            Embedding = RandomVector(random, 128)
        });
        await dbRead.SaveAsync();

        var dbVerify = new MyRichTypeDb(path, StorageFormat.Binary);
        await dbVerify.LoadAsync();
        var upserted = dbVerify.RichItems.Find("RT00000")!;
        Assert(upserted.ByteVal == 128 && upserted.ShortVal == short.MaxValue && upserted.HalfVal == Half.MaxValue,
            "[Binary] Upsert 后极值（byte=128, short.MaxValue, Half.MaxValue）往返正确");
        Assert(upserted.Weights.Length == 3 && upserted.Weights[0] == double.MinValue && upserted.Weights[2] == double.MaxValue,
            "[Binary] Upsert 后 double[] 极值往返正确");
        Assert(upserted.Blob.Length == 3 && upserted.Blob[0] == 0xFF && upserted.Blob[2] == 0xAB,
            "[Binary] Upsert 后 byte[] 往返正确");

        // ── 搜索仍可正常工作 ──
        random = new Random(777);
        var queryVec = RandomVector(random, 128);
        var searchResults = dbVerify.RichItems.Search(e => e.Embedding, queryVec, 5);
        Assert(searchResults.Count == 5, "[Binary] 新类型实体搜索 Top-5 返回正确数量");
        Assert(searchResults[0].Similarity >= searchResults[^1].Similarity,
            "[Binary] 新类型实体搜索结果按相似度降序排列");

        File.Delete(path);
    }
}
