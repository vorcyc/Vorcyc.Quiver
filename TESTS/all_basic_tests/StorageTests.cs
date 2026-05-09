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
        Console.WriteLine("\n═══ 11. 新增属性类型往返测试（标量 byte/short/Half/DateTimeOffset/TimeSpan/ushort/uint/ulong/sbyte/char/DateOnly/TimeOnly + 数组 byte[]/double[]/ushort[]/uint[]/ulong[]/sbyte[]/DateOnly[]/TimeOnly[]/short[]/int[]/long[]/bool[]/Half[]）═══");

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
                UShortVal = (ushort)(i * 7 % ushort.MaxValue),
                UIntVal = (uint)i * 100_003u,
                ULongVal = (ulong)i * 1_000_000_007UL,
                SByteVal = (sbyte)(i % 127 - 63),
                CharVal = (char)('A' + i % 26),
                DateVal = new DateOnly(2025, 1, 1).AddDays(i),
                TimeVal = new TimeOnly(0, 0, 0).Add(TimeSpan.FromSeconds(i * 13)),
                UShortArr = Enumerable.Range(0, 4).Select(j => (ushort)(i + j)).ToArray(),
                UIntArr = Enumerable.Range(0, 4).Select(j => (uint)(i + j) * 13u).ToArray(),
                ULongArr = Enumerable.Range(0, 4).Select(j => (ulong)(i + j) * 17UL).ToArray(),
                SByteArr = Enumerable.Range(0, 4).Select(j => (sbyte)((i + j) % 127 - 63)).ToArray(),
                DateArr = Enumerable.Range(0, 3).Select(j => new DateOnly(2025, 1, 1).AddDays(i + j)).ToArray(),
                TimeArr = Enumerable.Range(0, 3).Select(j => new TimeOnly(0, 0, 0).Add(TimeSpan.FromSeconds(i + j))).ToArray(),
                ShortArr = Enumerable.Range(0, 4).Select(j => (short)(i + j - 250)).ToArray(),
                IntArr = Enumerable.Range(0, 4).Select(j => (i + j) * 100_003).ToArray(),
                LongArr = Enumerable.Range(0, 4).Select(j => (long)(i + j) * 1_000_000_007L).ToArray(),
                BoolArr = Enumerable.Range(0, 5).Select(j => (i + j) % 2 == 0).ToArray(),
                HalfArr = Enumerable.Range(0, 4).Select(j => (Half)((i + j) * 0.25f)).ToArray(),
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

            if (loaded.UShortVal != orig.UShortVal ||
                loaded.UIntVal != orig.UIntVal ||
                loaded.ULongVal != orig.ULongVal ||
                loaded.SByteVal != orig.SByteVal ||
                loaded.CharVal != orig.CharVal ||
                loaded.DateVal != orig.DateVal ||
                loaded.TimeVal != orig.TimeVal)
            { allMatch = false; break; }

            if (!loaded.Blob.AsSpan().SequenceEqual(orig.Blob))
            { allMatch = false; break; }

            if (!loaded.UShortArr.AsSpan().SequenceEqual(orig.UShortArr) ||
                !loaded.UIntArr.AsSpan().SequenceEqual(orig.UIntArr) ||
                !loaded.ULongArr.AsSpan().SequenceEqual(orig.ULongArr) ||
                !loaded.SByteArr.AsSpan().SequenceEqual(orig.SByteArr) ||
                !loaded.DateArr.AsSpan().SequenceEqual(orig.DateArr) ||
                !loaded.TimeArr.AsSpan().SequenceEqual(orig.TimeArr) ||
                !loaded.ShortArr.AsSpan().SequenceEqual(orig.ShortArr) ||
                !loaded.IntArr.AsSpan().SequenceEqual(orig.IntArr) ||
                !loaded.LongArr.AsSpan().SequenceEqual(orig.LongArr) ||
                !loaded.BoolArr.AsSpan().SequenceEqual(orig.BoolArr) ||
                !loaded.HalfArr.AsSpan().SequenceEqual(orig.HalfArr))
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
        Assert(allMatch, $"[Binary] 全部 {entityCount} 条扩展类型字段（含 ushort/uint/ulong/sbyte/char/DateOnly/TimeOnly 及数组）逐字段精度校验通过");

        // ── 新增类型边界值验证 ──
        var rt0 = dbRead.RichItems.Find("RT00000")!;
        Assert(rt0.DateVal == new DateOnly(2025, 1, 1) && rt0.TimeVal == new TimeOnly(0, 0, 0),
            "[Binary] 边界值：DateOnly=2025-01-01, TimeOnly=00:00:00");
        Assert(rt0.CharVal == 'A' && rt0.SByteVal == (sbyte)(-63),
            "[Binary] 边界值：char='A', sbyte=-63");
        Assert(rt0.IntArr.AsSpan().SequenceEqual([0, 100_003, 200_006, 300_009]) &&
               rt0.BoolArr.AsSpan().SequenceEqual([true, false, true, false, true]),
            "[Binary] int[] / bool[] 往返正确（RT00000）");

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
            UShortVal = ushort.MaxValue,
            UIntVal = uint.MaxValue,
            ULongVal = ulong.MaxValue,
            SByteVal = sbyte.MinValue,
            CharVal = '\uFFFF',
            DateVal = DateOnly.MaxValue,
            TimeVal = TimeOnly.MaxValue,
            UShortArr = [ushort.MinValue, ushort.MaxValue],
            UIntArr = [uint.MinValue, uint.MaxValue],
            ULongArr = [ulong.MinValue, ulong.MaxValue],
            SByteArr = [sbyte.MinValue, 0, sbyte.MaxValue],
            DateArr = [DateOnly.MinValue, DateOnly.MaxValue],
            TimeArr = [TimeOnly.MinValue, TimeOnly.MaxValue],
            ShortArr = [short.MinValue, 0, short.MaxValue],
            IntArr = [int.MinValue, 0, int.MaxValue],
            LongArr = [long.MinValue, 0, long.MaxValue],
            BoolArr = [true, false, true],
            HalfArr = [Half.MinValue, (Half)0f, Half.MaxValue],
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
        Assert(upserted.UShortVal == ushort.MaxValue && upserted.UIntVal == uint.MaxValue && upserted.ULongVal == ulong.MaxValue,
            "[Binary] Upsert 后无符号整型极值（ushort/uint/ulong.MaxValue）往返正确");
        Assert(upserted.SByteVal == sbyte.MinValue && upserted.CharVal == '\uFFFF',
            "[Binary] Upsert 后 sbyte.MinValue / char=U+FFFF 往返正确");
        Assert(upserted.DateVal == DateOnly.MaxValue && upserted.TimeVal == TimeOnly.MaxValue,
            "[Binary] Upsert 后 DateOnly.MaxValue / TimeOnly.MaxValue 往返正确");
        Assert(upserted.ULongArr.AsSpan().SequenceEqual([ulong.MinValue, ulong.MaxValue]) &&
               upserted.DateArr.AsSpan().SequenceEqual([DateOnly.MinValue, DateOnly.MaxValue]),
            "[Binary] Upsert 后新增数组类型（ulong[]/DateOnly[]）极值往返正确");
        Assert(upserted.IntArr.AsSpan().SequenceEqual([int.MinValue, 0, int.MaxValue]) &&
               upserted.LongArr.AsSpan().SequenceEqual([long.MinValue, 0L, long.MaxValue]) &&
               upserted.ShortArr.AsSpan().SequenceEqual((short[])[short.MinValue, 0, short.MaxValue]),
            "[Binary] Upsert 后 short[]/int[]/long[] 极值往返正确");
        Assert(upserted.BoolArr.AsSpan().SequenceEqual([true, false, true]) &&
               upserted.HalfArr.AsSpan().SequenceEqual((Half[])[Half.MinValue, (Half)0f, Half.MaxValue]),
            "[Binary] Upsert 后 bool[]/Half[] 极值往返正确");

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