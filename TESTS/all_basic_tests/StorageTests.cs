using System.Diagnostics;
using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 10-11：Export/Import 功能 + 新增属性类型往返测试。</summary>
public static class StorageTests
{
    public static async Task RunAsync()
    {
        await Test10_ExportImport();
        await Test11_RichTypeRoundTrip();
    }

    // ==================== 10. Export / Import 测试 ====================
    private static async Task Test10_ExportImport()
    {
        Console.WriteLine("\n═══ 10. Export / Import（JSON & XML 导出往返）═══");

        var random = new Random(42);

        // 构造原始数据
        var originals = Enumerable.Range(0, 100).Select(i => new FaceFeature
        {
            PersonId = $"EI{i:D4}",
            Name = $"Person{i}",
            RegisterTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            Embedding = RandomVector(random, 128)
        }).ToList();

        foreach (var format in ExportFormats)
        {
            var ext = format == ExportFormat.Json ? ".json" : ".xml";
            var srcPath = Path.GetTempFileName() + ".vdb";
            var expPath = srcPath + ext;
            var impPath = Path.GetTempFileName() + ".vdb";

            try
            {
                // 写入主存储
                await using var src = new MyFaceDb(srcPath);
                await src.LoadAsync();
                src.Faces.AddRange(originals);
                await src.SaveAsync();

                // 导出
                await src.ExportAsync(expPath, format);
                Assert(File.Exists(expPath), $"[{format}] 导出文件已生成");
                Assert(new FileInfo(expPath).Length > 0, $"[{format}] 导出文件非空");

                // 导入到新库
                await using var dst = new MyFaceDb(impPath);
                await dst.LoadAsync();
                await dst.ImportAsync(expPath, format);

                Assert(dst.Faces.Count == originals.Count,
                    $"[{format}] Import 后 Count == {originals.Count}");

                // 抽样校验数据完整性
                var sample = dst.Faces.Find("EI0050");
                Assert(sample is not null, $"[{format}] Import 后 Find(EI0050) 成功");
                Assert(sample?.Name == "Person50", $"[{format}] Import 后 Name 正确");

                // 向量精度（JSON 有精度损耗，仅校验近似）
                if (format == ExportFormat.Json)
                {
                    var orig50 = originals.First(e => e.PersonId == "EI0050");
                    var maxDiff = sample!.Embedding.Zip(orig50.Embedding, (a, b) => MathF.Abs(a - b)).Max();
                    Assert(maxDiff < 1e-5f, $"[{format}] 向量精度损失 < 1e-5（实际 {maxDiff:G3}）");
                }

                // 搜索仍可正常使用
                var query = dst.Faces.Find("EI0001")!.Embedding;
                var hits = dst.Faces.Search(query, topK: 3);
                Assert(hits.Count == 3, $"[{format}] Import 后 Search topK=3 返回 3 条");
                Assert(hits[0].Entity.PersonId == "EI0001", $"[{format}] Import 后 Search 最近邻为自身");
            }
            finally
            {
                foreach (var f in new[] { srcPath, expPath, impPath })
                    if (File.Exists(f)) File.Delete(f);
            }
        }

        // 导出/导入大小对比（信息性输出，不断言）
        Console.WriteLine("\n  ─── 导出文件大小对比（2,000 条 × 3 向量）───");
        var rng2 = new Random(42);
        var bigPath = Path.GetTempFileName() + ".vdb";
        try
        {
            await using var bigDb = new MyMultiVectorDb(bigPath);
            await bigDb.LoadAsync();
            for (int i = 0; i < 2000; i++)
                bigDb.Items.Add(new MultiVectorEntity
                {
                    Id = $"Z{i:D4}",
                    Label = $"用户{i}",
                    Score = i,
                    IsActive = true,
                    TextEmbedding = RandomVector(rng2, 384),
                    ImageEmbedding = RandomVector(rng2, 512),
                    AudioEmbedding = RandomVector(rng2, 256)
                });
            await bigDb.SaveAsync();

            var binSize = new FileInfo(bigPath).Length;
            Console.WriteLine($"    Binary（主存储）:  {binSize,14:N0} bytes");

            foreach (var format in ExportFormats)
            {
                var ext = format == ExportFormat.Json ? ".json" : ".xml";
                var expPath = bigPath + ext;
                await bigDb.ExportAsync(expPath, format);
                var sz = new FileInfo(expPath).Length;
                Console.WriteLine($"    {format,-6}（导出）:     {sz,14:N0} bytes  ({(double)sz / binSize:F1}x Binary)");
                File.Delete(expPath);
            }
        }
        finally
        {
            if (File.Exists(bigPath)) File.Delete(bigPath);
        }
    }

    // ==================== 11. 新增属性类型往返测试（Binary 格式）====================
    private static async Task Test11_RichTypeRoundTrip()
    {
        Console.WriteLine("\n═══ 11. 新增属性类型往返测试（byte/short/Half/DateTimeOffset/TimeSpan/byte[]/double[]）═══");

        var path = Path.GetTempFileName() + ".vdb";
        var random = new Random(42);

        const int entityCount = 500;
        var dbWrite = new MyRichTypeDb(path);

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
                Weights = Enumerable.Range(0, 64).Select(_ => random.NextDouble() * 2 - 1).ToArray(),
                Embedding = RandomVector(random, 128)
            };
            dbWrite.RichItems.Add(originals[i]);
        }

        var sw = Stopwatch.StartNew();
        await dbWrite.SaveAsync();
        var saveMs = sw.ElapsedMilliseconds;
        var fileSize = new FileInfo(path).Length;

        sw.Restart();
        var dbRead = new MyRichTypeDb(path);
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

            if (!loaded.Blob.AsSpan().SequenceEqual(orig.Blob))
            { allMatch = false; break; }

            if (loaded.Weights.Length != orig.Weights.Length)
            { allMatch = false; break; }
            for (int j = 0; j < orig.Weights.Length; j++)
                if (loaded.Weights[j] != orig.Weights[j])
                { allMatch = false; break; }
            if (!allMatch) break;

            for (int j = 0; j < 128; j++)
                if (MathF.Abs(loaded.Embedding[j] - orig.Embedding[j]) > 1e-6f)
                { allMatch = false; break; }
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

        var dbVerify = new MyRichTypeDb(path);
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