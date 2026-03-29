using System.Diagnostics;
using Vorcyc.Quiver;

var passed = 0;
var failed = 0;

void Assert(bool condition, string testName)
{
    if (condition)
    {
        Interlocked.Increment(ref passed);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✔ {testName}");
    }
    else
    {
        Interlocked.Increment(ref failed);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ✘ {testName}");
    }
    Console.ResetColor();
}

var random = new Random(42);
float[] RandomVector(int dim)
{
    var v = new float[dim];
    for (int i = 0; i < dim; i++) v[i] = random.NextSingle() * 2 - 1;
    return v;
}

float[] ThreadSafeRandomVector(int dim)
{
    var rng = Random.Shared;
    var v = new float[dim];
    for (int i = 0; i < dim; i++) v[i] = rng.NextSingle() * 2 - 1;
    return v;
}

StorageFormat[] formats = [StorageFormat.Json, StorageFormat.Xml, StorageFormat.Binary];
string[] extensions = [".json", ".xml", ".vdb"];

// ==================== 1. 单向量实体往返测试（1,000 条）====================
Console.WriteLine("\n═══ 1. 单向量实体往返测试（1,000 条）═══");

for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];
    var path = $"test_roundtrip{extensions[f]}";
    random = new Random(42);

    Console.WriteLine($"\n  ── {format} 格式 ──");

    const int entityCount = 1000;
    var baseTime = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
    var dbWrite = new MyFaceDb(path, format);

    var originals = new FaceFeature[entityCount];
    for (int i = 0; i < entityCount; i++)
    {
        originals[i] = new FaceFeature
        {
            PersonId = $"P{i:D5}",
            Name = $"用户_{i}",
            RegisterTime = baseTime.AddMinutes(i),
            Embedding = RandomVector(128)
        };
        dbWrite.Faces.Add(originals[i]);
    }

    var sw = Stopwatch.StartNew();
    await dbWrite.SaveAsync();
    var saveMs = sw.ElapsedMilliseconds;
    var fileSize = new FileInfo(path).Length;

    sw.Restart();
    var dbRead = new MyFaceDb(path, format);
    await dbRead.LoadAsync();
    var loadMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"  保存 {saveMs}ms / 加载 {loadMs}ms / 文件 {fileSize:N0} bytes");

    Assert(dbRead.Faces.Count == entityCount,
        $"[{format}] 实体数量：{dbRead.Faces.Count}/{entityCount}");

    var allMatch = true;
    for (int i = 0; i < entityCount; i++)
    {
        var orig = originals[i];
        var loaded = dbRead.Faces.Find(orig.PersonId);
        if (loaded == null || loaded.Name != orig.Name ||
            loaded.RegisterTime != orig.RegisterTime ||
            loaded.Embedding.Length != 128)
        { allMatch = false; break; }

        for (int j = 0; j < 128; j++)
        {
            if (MathF.Abs(loaded.Embedding[j] - orig.Embedding[j]) > 1e-6f)
            { allMatch = false; break; }
        }
        if (!allMatch) break;
    }
    Assert(allMatch, $"[{format}] 全部 {entityCount} 条逐字段+向量精度校验通过");

    File.Delete(path);
}

// ==================== 2. 多向量实体往返测试（2,000 条）====================
Console.WriteLine("\n═══ 2. 多向量实体往返测试（2,000 条 × 3 个向量字段）═══");

for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];
    var path = $"test_multi_vec{extensions[f]}";
    random = new Random(42);

    Console.WriteLine($"\n  ── {format} 格式 ──");

    const int entityCount = 2000;
    var dbWrite = new MyMultiVectorDb(path, format);

    var originals = new MultiVectorEntity[entityCount];
    for (int i = 0; i < entityCount; i++)
    {
        originals[i] = new MultiVectorEntity
        {
            Id = $"MV{i:D5}",
            Label = $"多向量实体_{i}",
            Score = random.NextDouble() * 100,
            IsActive = i % 3 != 0,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        };
        dbWrite.Items.Add(originals[i]);
    }

    var sw = Stopwatch.StartNew();
    await dbWrite.SaveAsync();
    var saveMs = sw.ElapsedMilliseconds;
    var fileSize = new FileInfo(path).Length;

    sw.Restart();
    var dbRead = new MyMultiVectorDb(path, format);
    await dbRead.LoadAsync();
    var loadMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"  保存 {saveMs}ms / 加载 {loadMs}ms / 文件 {fileSize:N0} bytes");

    Assert(dbRead.Items.Count == entityCount,
        $"[{format}] 多向量实体数量：{dbRead.Items.Count}/{entityCount}");

    // 逐字段+三组向量精度校验
    var allMatch = true;
    for (int i = 0; i < entityCount; i++)
    {
        var orig = originals[i];
        var loaded = dbRead.Items.Find(orig.Id);
        if (loaded == null || loaded.Label != orig.Label ||
            MathF.Abs((float)(loaded.Score - orig.Score)) > 1e-4f ||
            loaded.IsActive != orig.IsActive)
        { allMatch = false; break; }

        // 验证三组向量
        if (loaded.TextEmbedding.Length != 384 ||
            loaded.ImageEmbedding.Length != 512 ||
            loaded.AudioEmbedding.Length != 256)
        { allMatch = false; break; }

        static bool VectorsEqual(float[] a, float[] b)
        {
            for (int k = 0; k < a.Length; k++)
                if (MathF.Abs(a[k] - b[k]) > 1e-6f) return false;
            return true;
        }

        if (!VectorsEqual(loaded.TextEmbedding, orig.TextEmbedding) ||
            !VectorsEqual(loaded.ImageEmbedding, orig.ImageEmbedding) ||
            !VectorsEqual(loaded.AudioEmbedding, orig.AudioEmbedding))
        { allMatch = false; break; }
    }
    Assert(allMatch, $"[{format}] 全部 {entityCount} 条三组向量精度校验通过");

    File.Delete(path);
}

// ==================== 3. 多向量分字段搜索测试 ====================
Console.WriteLine("\n═══ 3. 多向量分字段搜索测试（1,000 条）═══");

for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];
    var path = $"test_multi_search{extensions[f]}";
    random = new Random(42);

    var db = new MyMultiVectorDb(path, format);
    for (int i = 0; i < 1000; i++)
    {
        db.Items.Add(new MultiVectorEntity
        {
            Id = $"MS{i:D4}",
            Label = $"搜索实体_{i}",
            Score = i,
            IsActive = true,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }
    await db.SaveAsync();

    var dbRead = new MyMultiVectorDb(path, format);
    await dbRead.LoadAsync();

    random = new Random(777);
    var textQuery = RandomVector(384);
    var imageQuery = RandomVector(512);
    var audioQuery = RandomVector(256);

    // 保存前各字段 Top-5
    var textBefore = db.Items.Search(e => e.TextEmbedding, textQuery, 5);
    var imageBefore = db.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
    var audioBefore = db.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

    // 加载后各字段 Top-5
    var textAfter = dbRead.Items.Search(e => e.TextEmbedding, textQuery, 5);
    var imageAfter = dbRead.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
    var audioAfter = dbRead.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

    static bool RankEqual<T>(List<QuiverSearchResult<T>> a, List<QuiverSearchResult<T>> b, Func<T, string> getId)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (getId(a[i].Entity) != getId(b[i].Entity)) return false;
        return true;
    }

    Assert(RankEqual(textBefore, textAfter, e => e.Id),
        $"[{format}] TextEmbedding(384d) Top-5 排名一致");
    Assert(RankEqual(imageBefore, imageAfter, e => e.Id),
        $"[{format}] ImageEmbedding(512d) Top-5 排名一致");
    Assert(RankEqual(audioBefore, audioAfter, e => e.Id),
        $"[{format}] AudioEmbedding(256d) Top-5 排名一致");

    // 三字段搜索结果应彼此不同（不同向量空间）
    var textTop1 = textAfter[0].Entity.Id;
    var imageTop1 = imageAfter[0].Entity.Id;
    var audioTop1 = audioAfter[0].Entity.Id;
    var anyDifferent = textTop1 != imageTop1 || imageTop1 != audioTop1;
    Assert(anyDifferent, $"[{format}] 三字段搜索结果互不相同（不同向量空间独立检索）");

    File.Delete(path);
}

// ==================== 4. 大批量 AddRange 测试（5,000 条多向量）====================
Console.WriteLine("\n═══ 4. 大批量 AddRange 测试（5,000 条多向量）═══");

for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];
    var path = $"test_batch{extensions[f]}";
    random = new Random(42);

    var db = new MyMultiVectorDb(path, format);
    var batch = Enumerable.Range(0, 5000).Select(i => new MultiVectorEntity
    {
        Id = $"B{i:D5}",
        Label = $"批量用户{i}",
        Score = i * 0.1,
        IsActive = i % 2 == 0,
        TextEmbedding = RandomVector(384),
        ImageEmbedding = RandomVector(512),
        AudioEmbedding = RandomVector(256)
    }).ToList();

    var sw = Stopwatch.StartNew();
    db.Items.AddRange(batch);
    var addMs = sw.ElapsedMilliseconds;

    sw.Restart();
    await db.SaveAsync();
    var saveMs = sw.ElapsedMilliseconds;

    sw.Restart();
    var dbRead = new MyMultiVectorDb(path, format);
    await dbRead.LoadAsync();
    var loadMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"  [{format}] AddRange {addMs}ms / 保存 {saveMs}ms / 加载 {loadMs}ms");

    Assert(dbRead.Items.Count == 5000, $"[{format}] 5000 条多向量加载正确");
    Assert(dbRead.Items.Find("B00000")?.Label == "批量用户0", $"[{format}] 首条正确");
    Assert(dbRead.Items.Find("B02500")?.Label == "批量用户2500", $"[{format}] 中间正确");
    Assert(dbRead.Items.Find("B04999")?.Label == "批量用户4999", $"[{format}] 尾条正确");

    // 验证向量维度
    var sample = dbRead.Items.Find("B01000")!;
    Assert(sample.TextEmbedding.Length == 384 &&
           sample.ImageEmbedding.Length == 512 &&
           sample.AudioEmbedding.Length == 256,
        $"[{format}] 抽样实体三组向量维度正确 (384/512/256)");

    File.Delete(path);
}

// ==================== 5. 边界条件测试 ====================
Console.WriteLine("\n═══ 5. 边界条件测试 ═══");

for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];

    // 空数据库
    var emptyPath = $"test_empty{extensions[f]}";
    var dbEmpty = new MyMultiVectorDb(emptyPath, format);
    await dbEmpty.SaveAsync();
    Assert(File.Exists(emptyPath), $"[{format}] 空数据库文件已创建");
    var dbEmptyRead = new MyMultiVectorDb(emptyPath, format);
    await dbEmptyRead.LoadAsync();
    Assert(dbEmptyRead.Items.Count == 0, $"[{format}] 空数据库加载后数量为 0");
    File.Delete(emptyPath);

    // 文件不存在
    var noFile = $"nonexistent{extensions[f]}";
    try
    {
        var dbNo = new MyMultiVectorDb(noFile, format);
        await dbNo.LoadAsync();
        Assert(dbNo.Items.Count == 0, $"[{format}] 文件不存在时静默返回");
    }
    catch (Exception ex)
    {
        Assert(false, $"[{format}] 文件不存在时不应抛异常：{ex.Message}");
    }
}

// ==================== 6. Upsert + 删除持久化测试 ====================
Console.WriteLine("\n═══ 6. Upsert + 删除持久化测试（多向量）═══");

for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];
    var path = $"test_crud{extensions[f]}";
    random = new Random(42);

    var db = new MyMultiVectorDb(path, format);

    for (int i = 0; i < 200; i++)
    {
        db.Items.Add(new MultiVectorEntity
        {
            Id = $"C{i:D3}",
            Label = $"原始_{i}",
            Score = i,
            IsActive = true,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }

    // Upsert 前 100 条
    for (int i = 0; i < 100; i++)
    {
        db.Items.Upsert(new MultiVectorEntity
        {
            Id = $"C{i:D3}",
            Label = $"已更新_{i}",
            Score = i + 1000,
            IsActive = false,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }

    // 删除后 50 条（C150~C199）
    for (int i = 150; i < 200; i++)
        db.Items.RemoveByKey($"C{i:D3}");

    Assert(db.Items.Count == 150, $"[{format}] CRUD 后内存数量 150");

    await db.SaveAsync();
    var dbRead = new MyMultiVectorDb(path, format);
    await dbRead.LoadAsync();

    Assert(dbRead.Items.Count == 150, $"[{format}] 持久化后加载数量 150");

    var upserted = dbRead.Items.Find("C000");
    Assert(upserted?.Label == "已更新_0" && upserted.Score > 999,
        $"[{format}] Upsert 数据持久化正确");

    var untouched = dbRead.Items.Find("C100");
    Assert(untouched?.Label == "原始_100" && untouched.IsActive,
        $"[{format}] 未 Upsert 的数据不变");

    Assert(dbRead.Items.Find("C199") == null, $"[{format}] 已删除数据不存在");

    File.Delete(path);
}

// ==================== 7. 并发读取测试 ====================
Console.WriteLine("\n═══ 7. 并发读取测试（多线程同时搜索多向量字段）═══");
{
    var path = "test_concurrent_read.vdb";
    random = new Random(42);

    var db = new MyMultiVectorDb(path, StorageFormat.Binary);
    for (int i = 0; i < 3000; i++)
    {
        db.Items.Add(new MultiVectorEntity
        {
            Id = $"CR{i:D5}",
            Label = $"并发读取{i}",
            Score = i,
            IsActive = true,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }

    const int readerCount = 24;
    const int searchesPerReader = 100;
    var errors = 0;
    var totalSearches = 0;

    var sw = Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, readerCount).Select(t => Task.Run(() =>
    {
        for (int s = 0; s < searchesPerReader; s++)
        {
            try
            {
                // 每个线程轮流搜索三个不同向量字段
                switch (s % 3)
                {
                    case 0:
                        db.Items.Search(e => e.TextEmbedding, ThreadSafeRandomVector(384), 5);
                        break;
                    case 1:
                        db.Items.Search(e => e.ImageEmbedding, ThreadSafeRandomVector(512), 5);
                        break;
                    case 2:
                        db.Items.Search(e => e.AudioEmbedding, ThreadSafeRandomVector(256), 5);
                        break;
                }
                Interlocked.Increment(ref totalSearches);
            }
            catch { Interlocked.Increment(ref errors); }
        }
    })).ToArray();

    await Task.WhenAll(tasks);
    var elapsed = sw.ElapsedMilliseconds;

    Console.WriteLine($"  {readerCount} 线程 × {searchesPerReader} 次 = {totalSearches} 次搜索（3 字段轮询），耗时 {elapsed}ms");
    Assert(errors == 0, $"并发多字段搜索 {totalSearches} 次零异常");
    Assert(totalSearches == readerCount * searchesPerReader, "全部搜索任务已完成");

    File.Delete(path);
}

// ==================== 8. 并发读写测试 ====================
Console.WriteLine("\n═══ 8. 并发读写测试（Upsert + Delete + 多字段 Search）═══");
{
    var path = "test_concurrent_rw.vdb";
    random = new Random(42);

    var db = new MyMultiVectorDb(path, StorageFormat.Binary);
    for (int i = 0; i < 1000; i++)
    {
        db.Items.Add(new MultiVectorEntity
        {
            Id = $"RW{i:D5}",
            Label = $"基础{i}",
            Score = i,
            IsActive = true,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }

    var writeErrors = 0;
    var readErrors = 0;
    var writeCount = 0;
    var readCount = 0;
    var cts = new CancellationTokenSource();

    // 4 个写线程：持续 Upsert 多向量实体
    var writers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
    {
        var id = 1000 + t * 5000;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                db.Items.Upsert(new MultiVectorEntity
                {
                    Id = $"RW{id:D5}",
                    Label = $"线程{t}_实体{id}",
                    Score = id,
                    IsActive = true,
                    TextEmbedding = ThreadSafeRandomVector(384),
                    ImageEmbedding = ThreadSafeRandomVector(512),
                    AudioEmbedding = ThreadSafeRandomVector(256)
                });
                Interlocked.Increment(ref writeCount);
                id++;
            }
            catch { Interlocked.Increment(ref writeErrors); }
        }
    })).ToArray();

    // 8 个读线程：轮流搜索三个向量字段
    var readers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
    {
        var round = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                switch (round++ % 3)
                {
                    case 0: db.Items.Search(e => e.TextEmbedding, ThreadSafeRandomVector(384), 5); break;
                    case 1: db.Items.Search(e => e.ImageEmbedding, ThreadSafeRandomVector(512), 5); break;
                    case 2: db.Items.Search(e => e.AudioEmbedding, ThreadSafeRandomVector(256), 5); break;
                }
                Interlocked.Increment(ref readCount);
            }
            catch { Interlocked.Increment(ref readErrors); }
        }
    })).ToArray();

    // 2 个删除线程
    var deleters = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
    {
        var rng = Random.Shared;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                db.Items.RemoveByKey($"RW{rng.Next(0, 1000):D5}");
                Thread.Sleep(1);
            }
            catch { Interlocked.Increment(ref writeErrors); }
        }
    })).ToArray();

    await Task.Delay(3000);
    cts.Cancel();
    await Task.WhenAll([.. writers, .. readers, .. deleters]);

    Console.WriteLine($"  写入 {writeCount} / 搜索 {readCount} / 写异常 {writeErrors} / 读异常 {readErrors}");
    Assert(writeErrors == 0, $"并发读写：写操作零异常（{writeCount} 次）");
    Assert(readErrors == 0, $"并发读写：读操作零异常（{readCount} 次）");

    await db.SaveAsync();
    var dbRead = new MyMultiVectorDb(path, StorageFormat.Binary);
    await dbRead.LoadAsync();
    Assert(dbRead.Items.Count > 0, $"并发操作后持久化成功，加载 {dbRead.Items.Count} 条");

    File.Delete(path);
}

// ==================== 9. 并发批量写入 + 搜索 ====================
Console.WriteLine("\n═══ 9. 并发 AddRange + 多字段 Search ═══");
{
    var db = new MyMultiVectorDb("test_concurrent_batch.vdb", StorageFormat.Binary);
    var batchErrors = 0;
    var searchErrors = 0;
    var batchCount = 0;
    var searchCount = 0;
    var cts = new CancellationTokenSource();

    var writers = Enumerable.Range(0, 3).Select(t => Task.Run(() =>
    {
        var batchId = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var batch = Enumerable.Range(0, 50).Select(i => new MultiVectorEntity
                {
                    Id = $"CB_T{t}_{batchId}_{i}",
                    Label = $"批量并发_{t}_{batchId}_{i}",
                    Score = i,
                    IsActive = true,
                    TextEmbedding = ThreadSafeRandomVector(384),
                    ImageEmbedding = ThreadSafeRandomVector(512),
                    AudioEmbedding = ThreadSafeRandomVector(256)
                }).ToList();
                db.Items.AddRange(batch);
                Interlocked.Increment(ref batchCount);
                batchId++;
            }
            catch (InvalidOperationException) { /* 主键冲突，预期行为 */ }
            catch { Interlocked.Increment(ref batchErrors); }
        }
    })).ToArray();

    var readers = Enumerable.Range(0, 6).Select(t => Task.Run(() =>
    {
        var round = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                if (db.Items.Count > 0)
                {
                    switch (round++ % 3)
                    {
                        case 0: db.Items.Search(e => e.TextEmbedding, ThreadSafeRandomVector(384), 10); break;
                        case 1: db.Items.Search(e => e.ImageEmbedding, ThreadSafeRandomVector(512), 10); break;
                        case 2: db.Items.Search(e => e.AudioEmbedding, ThreadSafeRandomVector(256), 10); break;
                    }
                    Interlocked.Increment(ref searchCount);
                }
            }
            catch { Interlocked.Increment(ref searchErrors); }
        }
    })).ToArray();

    await Task.Delay(3000);
    cts.Cancel();
    await Task.WhenAll([.. writers, .. readers]);

    Console.WriteLine($"  批量写入 {batchCount} 批（每批 50 × 3 向量）/ 搜索 {searchCount} 次");
    Console.WriteLine($"  最终数据量：{db.Items.Count} 条");
    Assert(batchErrors == 0, $"并发 AddRange 零异常（{batchCount} 批）");
    Assert(searchErrors == 0, $"并发多字段 Search 零异常（{searchCount} 次）");
    Assert(db.Items.Count > 0, $"并发写入后数据量 > 0");

    File.Delete("test_concurrent_batch.vdb");
}

// ==================== 10. 文件大小对比 ====================
Console.WriteLine("\n═══ 10. 三种格式文件大小对比（2,000 条 × 3 向量）═══");

var sizes = new long[formats.Length];
for (int f = 0; f < formats.Length; f++)
{
    var format = formats[f];
    var path = $"test_size{extensions[f]}";
    random = new Random(42);

    var db = new MyMultiVectorDb(path, format);
    for (int i = 0; i < 2000; i++)
    {
        db.Items.Add(new MultiVectorEntity
        {
            Id = $"Z{i:D4}",
            Label = $"用户{i}",
            Score = i,
            IsActive = true,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
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

// ==================== 11. 新增属性类型往返测试（Binary 格式）====================
Console.WriteLine("\n═══ 11. 新增属性类型往返测试（byte/short/Half/DateTimeOffset/TimeSpan/byte[]/double[]）═══");
{
    var path = "test_rich_types.vdb";
    random = new Random(42);

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
            Embedding = RandomVector(128)
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
        Embedding = RandomVector(128)
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
    var queryVec = RandomVector(128);
    var searchResults = dbVerify.RichItems.Search(e => e.Embedding, queryVec, 5);
    Assert(searchResults.Count == 5, "[Binary] 新类型实体搜索 Top-5 返回正确数量");
    Assert(searchResults[0].Similarity >= searchResults[^1].Similarity,
        "[Binary] 新类型实体搜索结果按相似度降序排列");

    File.Delete(path);
}

// ==================== 12. WAL 增量持久化基础测试 ====================
Console.WriteLine("\n═══ 12. WAL 增量持久化基础测试 ═══");
{
    var path = "test_wal_basic.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    // 写入 100 条，通过 SaveChangesAsync 仅追加 WAL
    var db = new MyWalDb(path);
    for (int i = 0; i < 100; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"W{i:D4}",
            Name = $"WAL用户_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }

    await db.SaveChangesAsync();
    Assert(File.Exists(walPath), "WAL 文件已创建");
    Assert(!File.Exists(path), "仅 SaveChangesAsync 不生成快照文件");

    // 释放第一个上下文的 WAL 文件锁
    db.Dispose();

    // 新上下文加载 — 纯 WAL 回放（无快照）
    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 100, $"WAL 回放后实体数量正确：{dbRead.Faces.Count}/100");
    Assert(dbRead.Faces.Find("W0000")?.Name == "WAL用户_0", "WAL 回放首条数据正确");
    Assert(dbRead.Faces.Find("W0099")?.Name == "WAL用户_99", "WAL 回放尾条数据正确");

    // 搜索在 WAL 回放后可正常工作
    random = new Random(777);
    var results = dbRead.Faces.Search(e => e.Embedding, RandomVector(128), 5);
    Assert(results.Count == 5, "WAL 回放后搜索 Top-5 正常");
    Assert(results[0].Similarity >= results[^1].Similarity, "WAL 回放后搜索结果按相似度降序");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
}

// ==================== 13. WAL 删除 + Upsert 回放测试 ====================
Console.WriteLine("\n═══ 13. WAL 删除 + Upsert 回放测试 ═══");
{
    var path = "test_wal_crud.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    var db = new MyWalDb(path);

    // 添加 200 条
    for (int i = 0; i < 200; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"WC{i:D4}",
            Name = $"原始_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveChangesAsync();

    // Upsert 前 50 条
    for (int i = 0; i < 50; i++)
    {
        db.Faces.Upsert(new FaceFeature
        {
            PersonId = $"WC{i:D4}",
            Name = $"已更新_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }

    // 删除后 50 条（WC0150~WC0199）
    for (int i = 150; i < 200; i++)
        db.Faces.RemoveByKey($"WC{i:D4}");

    Assert(db.Faces.Count == 150, $"CRUD 后内存数量 150：{db.Faces.Count}");
    await db.SaveChangesAsync();

    db.Dispose();

    // 新上下文回放全部 WAL
    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 150, $"WAL 回放 CRUD 后数量正确：{dbRead.Faces.Count}/150");
    Assert(dbRead.Faces.Find("WC0000")?.Name == "已更新_0", "Upsert 数据通过 WAL 回放正确");
    Assert(dbRead.Faces.Find("WC0100")?.Name == "原始_100", "未修改数据通过 WAL 回放正确");
    Assert(dbRead.Faces.Find("WC0199") == null, "已删除数据通过 WAL 回放不存在");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
}

// ==================== 14. WAL Clear 回放测试 ====================
Console.WriteLine("\n═══ 14. WAL Clear 回放测试 ═══");
{
    var path = "test_wal_clear.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    var db = new MyWalDb(path);
    for (int i = 0; i < 50; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"CL{i:D3}",
            Name = $"清空测试_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveChangesAsync();

    // Clear 后再添加 10 条
    db.Faces.Clear();
    for (int i = 0; i < 10; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"NEW{i:D3}",
            Name = $"清空后新增_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveChangesAsync();

    db.Dispose();

    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 10, $"Clear 后 WAL 回放数量正确：{dbRead.Faces.Count}/10");
    Assert(dbRead.Faces.Find("CL000") == null, "Clear 前的数据已不存在");
    Assert(dbRead.Faces.Find("NEW000")?.Name == "清空后新增_0", "Clear 后新增数据正确");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
}

// ==================== 15. WAL + 快照混合测试 ====================
Console.WriteLine("\n═══ 15. WAL + 快照混合（SaveAsync + SaveChangesAsync）═══");
{
    var path = "test_wal_hybrid.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    // 阶段 1：写入 100 条 → 全量快照
    var db = new MyWalDb(path);
    for (int i = 0; i < 100; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"H{i:D4}",
            Name = $"快照用户_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveAsync(); // 全量快照 + 清空 WAL
    Assert(File.Exists(path), "快照文件已创建");

    // 阶段 2：增量写入 50 条 → 仅 WAL
    for (int i = 100; i < 150; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"H{i:D4}",
            Name = $"增量用户_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }

    // 删除快照中的 10 条
    for (int i = 0; i < 10; i++)
        db.Faces.RemoveByKey($"H{i:D4}");

    await db.SaveChangesAsync(); // 仅追加 WAL（60 条记录：50 Add + 10 Remove）
    Assert(db.Faces.Count == 140, $"混合操作后内存数量 140：{db.Faces.Count}");

    db.Dispose();

    // 新上下文：加载快照 100 条 + 回放 WAL（+50, -10）= 140
    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 140, $"快照+WAL 加载后数量正确：{dbRead.Faces.Count}/140");
    Assert(dbRead.Faces.Find("H0000") == null, "快照中被 WAL 删除的实体不存在");
    Assert(dbRead.Faces.Find("H0010")?.Name == "快照用户_10", "快照中未删除的实体正确");
    Assert(dbRead.Faces.Find("H0149")?.Name == "增量用户_149", "WAL 新增的实体正确");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
    File.Delete(path + ".tmp");
}

// ==================== 16. WAL 自动压缩测试 ====================
Console.WriteLine("\n═══ 16. WAL 自动压缩测试（阈值 = 50）═══");
{
    var path = "test_wal_compact.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    // 使用低阈值触发自动压缩
    var db = new MyWalDbLowThreshold(path);

    // 写入 60 条（超过阈值 50）→ SaveChangesAsync 应触发自动压缩
    for (int i = 0; i < 60; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"AC{i:D4}",
            Name = $"自动压缩_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveChangesAsync();

    // 自动压缩后应生成快照文件
    Assert(File.Exists(path), "自动压缩已生成快照文件");

    db.Dispose();

    // 验证数据完整性
    var dbRead = new MyWalDbLowThreshold(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 60, $"自动压缩后加载数量正确：{dbRead.Faces.Count}/60");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
    File.Delete(path + ".tmp");
}

// ==================== 17. WAL DisposeAsync 自动保存测试 ====================
Console.WriteLine("\n═══ 17. WAL DisposeAsync 自动保存测试 ═══");
{
    var path = "test_wal_dispose.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    // await using 自动调用 DisposeAsync → SaveChangesAsync
    await using (var db = new MyWalDb(path))
    {
        for (int i = 0; i < 30; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"D{i:D3}",
                Name = $"Dispose测试_{i}",
                RegisterTime = DateTime.UtcNow,
                Embedding = RandomVector(128)
            });
        }
        // 不手动调用 Save — 依赖 DisposeAsync
    }
    // await using 结束时已自动 Dispose，WAL 文件锁已释放

    // DisposeAsync 后数据应已持久化到 WAL
    Assert(File.Exists(walPath), "DisposeAsync 后 WAL 文件存在");

    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 30, $"DisposeAsync 自动保存后加载正确：{dbRead.Faces.Count}/30");
    Assert(dbRead.Faces.Find("D000")?.Name == "Dispose测试_0", "DisposeAsync 数据内容正确");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
}

// ==================== 18. WAL 多轮增量累积测试 ====================
Console.WriteLine("\n═══ 18. WAL 多轮增量累积测试 ═══");
{
    var path = "test_wal_multi_round.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    var db = new MyWalDb(path);

    // 第 1 轮：Add 50
    for (int i = 0; i < 50; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"MR{i:D4}",
            Name = $"轮次1_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveChangesAsync();

    // 第 2 轮：Add 30 + Remove 10
    for (int i = 50; i < 80; i++)
    {
        db.Faces.Add(new FaceFeature
        {
            PersonId = $"MR{i:D4}",
            Name = $"轮次2_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    for (int i = 0; i < 10; i++)
        db.Faces.RemoveByKey($"MR{i:D4}");
    await db.SaveChangesAsync();

    // 第 3 轮：Upsert 20
    for (int i = 10; i < 30; i++)
    {
        db.Faces.Upsert(new FaceFeature
        {
            PersonId = $"MR{i:D4}",
            Name = $"轮次3_已更新_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }
    await db.SaveChangesAsync();

    // 预期：50 - 10 + 30 = 70
    Assert(db.Faces.Count == 70, $"三轮操作后内存数量 70：{db.Faces.Count}");

    db.Dispose();

    // 全新上下文回放三轮 WAL
    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 70, $"三轮 WAL 回放后数量正确：{dbRead.Faces.Count}/70");
    Assert(dbRead.Faces.Find("MR0005") == null, "第 2 轮删除的实体不存在");
    Assert(dbRead.Faces.Find("MR0015")?.Name == "轮次3_已更新_15", "第 3 轮 Upsert 数据正确");
    Assert(dbRead.Faces.Find("MR0050")?.Name == "轮次2_50", "第 2 轮新增数据正确");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
}

// ==================== 19. WAL 与非 WAL SaveAsync 对比测试 ====================
Console.WriteLine("\n═══ 19. WAL SaveChangesAsync vs SaveAsync 性能对比 ═══");
{
    var path = "test_wal_perf.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    // 先写入 2000 条基础数据
    var db = new MyWalDb(path);
    var batch = Enumerable.Range(0, 2000).Select(i => new FaceFeature
    {
        PersonId = $"P{i:D5}",
        Name = $"性能测试_{i}",
        RegisterTime = DateTime.UtcNow,
        Embedding = RandomVector(128)
    }).ToList();
    db.Faces.AddRange(batch);
    await db.SaveAsync(); // 先创建快照

    // 增量修改 10 条
    for (int i = 0; i < 10; i++)
    {
        db.Faces.Upsert(new FaceFeature
        {
            PersonId = $"P{i:D5}",
            Name = $"已修改_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(128)
        });
    }

    // 测试 SaveChangesAsync 耗时（仅 WAL 增量）
    var sw = Stopwatch.StartNew();
    await db.SaveChangesAsync();
    var walMs = sw.ElapsedMilliseconds;

    // 测试 SaveAsync 耗时（全量快照 2000 条）
    db.Faces.Upsert(new FaceFeature
    {
        PersonId = "P00000",
        Name = "再次修改",
        RegisterTime = DateTime.UtcNow,
        Embedding = RandomVector(128)
    });
    sw.Restart();
    await db.SaveAsync();
    var fullMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"  SaveChangesAsync（10 条增量）：{walMs}ms");
    Console.WriteLine($"  SaveAsync（2000 条全量）：{fullMs}ms");
    Assert(true, $"WAL 增量 {walMs}ms vs 全量 {fullMs}ms（基准对比，非严格断言）");

    db.Dispose();

    // 验证两种保存方式后数据一致性
    var dbRead = new MyWalDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Faces.Count == 2000, $"混合保存后加载数量正确：{dbRead.Faces.Count}/2000");
    Assert(dbRead.Faces.Find("P00000")?.Name == "再次修改", "最后一次全量保存数据正确");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
    File.Delete(path + ".tmp");
}

// ==================== 20. WAL 多向量实体测试 ====================
Console.WriteLine("\n═══ 20. WAL 多向量实体增量持久化测试 ═══");
{
    var path = "test_wal_multi_vec.vdb";
    var walPath = path + ".wal";
    random = new Random(42);

    var db = new MyWalMultiVecDb(path);
    for (int i = 0; i < 200; i++)
    {
        db.Items.Add(new MultiVectorEntity
        {
            Id = $"WM{i:D4}",
            Label = $"WAL多向量_{i}",
            Score = i * 1.5,
            IsActive = i % 2 == 0,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }
    await db.SaveChangesAsync();

    // 删除 50 条 + Upsert 30 条
    for (int i = 0; i < 50; i++)
        db.Items.RemoveByKey($"WM{i:D4}");
    for (int i = 50; i < 80; i++)
    {
        db.Items.Upsert(new MultiVectorEntity
        {
            Id = $"WM{i:D4}",
            Label = $"WAL多向量_已更新_{i}",
            Score = i + 1000,
            IsActive = true,
            TextEmbedding = RandomVector(384),
            ImageEmbedding = RandomVector(512),
            AudioEmbedding = RandomVector(256)
        });
    }
    await db.SaveChangesAsync();

    db.Dispose();

    // 回放验证
    var dbRead = new MyWalMultiVecDb(path);
    await dbRead.LoadAsync();
    Assert(dbRead.Items.Count == 150, $"WAL 多向量回放数量正确：{dbRead.Items.Count}/150");
    Assert(dbRead.Items.Find("WM0000") == null, "WAL 多向量删除回放正确");
    Assert(dbRead.Items.Find("WM0060")?.Label == "WAL多向量_已更新_60", "WAL 多向量 Upsert 回放正确");

    // 三字段搜索验证
    random = new Random(777);
    var textResults = dbRead.Items.Search(e => e.TextEmbedding, RandomVector(384), 5);
    var imageResults = dbRead.Items.Search(e => e.ImageEmbedding, RandomVector(512), 5);
    var audioResults = dbRead.Items.Search(e => e.AudioEmbedding, RandomVector(256), 5);
    Assert(textResults.Count == 5 && imageResults.Count == 5 && audioResults.Count == 5,
        "WAL 回放后三字段搜索均返回 Top-5");

    dbRead.Dispose();
    File.Delete(path);
    File.Delete(walPath);
}

// ==================== 汇总 ====================
Console.WriteLine($"\n{"",3}══════════════════════════════════════════════════");
Console.ForegroundColor = passed > 0 && failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
Console.WriteLine($"  测试完成：{passed} 通过，{failed} 失败，共 {passed + failed} 项");
Console.ResetColor();

return failed == 0 ? 0 : 1;

// ═══════════════════════════════════════════════════════════════════
// 实体定义
// ═══════════════════════════════════════════════════════════════════

/// <summary>单向量实体：面部特征。</summary>
public class FaceFeature
{
    [QuiverKey]
    public string PersonId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime RegisterTime { get; set; }

    [QuiverVector(128)]
    public float[] Embedding { get; set; } = [];
}

/// <summary>
/// 多向量实体：同时持有文本、图像、音频三组不同维度的向量。
/// 用于验证多字段独立索引、分字段搜索、持久化往返等场景。
/// </summary>
public class MultiVectorEntity
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Score { get; set; }
    public bool IsActive { get; set; }

    [QuiverVector(384, DistanceMetric.Cosine)]
    public float[] TextEmbedding { get; set; } = [];

    [QuiverVector(512, DistanceMetric.Euclidean)]
    public float[] ImageEmbedding { get; set; } = [];

    [QuiverVector(256, DistanceMetric.DotProduct)]
    public float[] AudioEmbedding { get; set; } = [];
}

/// <summary>
/// 富类型实体：覆盖 BinaryStorageProvider 新增的 7 种 TypeCode。
/// 包含 byte、short、Half、DateTimeOffset、TimeSpan、byte[]、double[] 属性，
/// 用于验证二进制序列化往返的完整性和精度。
/// </summary>
public class RichTypeEntity
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    public byte ByteVal { get; set; }
    public short ShortVal { get; set; }
    public Half HalfVal { get; set; }
    public DateTimeOffset OffsetTime { get; set; }
    public TimeSpan Duration { get; set; }
    public byte[] Blob { get; set; } = [];
    public double[] Weights { get; set; } = [];

    [QuiverVector(128)]
    public float[] Embedding { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════
// 数据库上下文定义
// ═══════════════════════════════════════════════════════════════════

/// <summary>单向量数据库上下文。</summary>
public class MyFaceDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>多向量数据库上下文，包含单个多字段集合。</summary>
public class MyMultiVectorDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}

/// <summary>富类型数据库上下文，用于测试新增属性类型。</summary>
public class MyRichTypeDb(string path, StorageFormat format) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = format,
    DefaultMetric = DistanceMetric.Cosine
})
{
    public QuiverSet<RichTypeEntity> RichItems { get; set; } = null!;
}

/// <summary>WAL 模式数据库上下文（默认阈值 10,000）。</summary>
public class MyWalDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = StorageFormat.Binary,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>WAL 模式数据库上下文（低压缩阈值，用于测试自动压缩）。</summary>
public class MyWalDbLowThreshold(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = StorageFormat.Binary,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalCompactionThreshold = 50,
    WalFlushToDisk = true
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}

/// <summary>WAL 模式多向量数据库上下文。</summary>
public class MyWalMultiVecDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    StorageFormat = StorageFormat.Binary,
    DefaultMetric = DistanceMetric.Cosine,
    EnableWal = true,
    WalFlushToDisk = true
})
{
    public QuiverSet<MultiVectorEntity> Items { get; set; } = null!;
}