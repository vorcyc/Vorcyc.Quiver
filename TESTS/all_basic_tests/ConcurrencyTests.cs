using System.Diagnostics;
using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 7-9：并发读取 / 读写 / 批量写入 + 搜索。</summary>
public static class ConcurrencyTests
{
    public static async Task RunAsync()
    {
        await Test7_ConcurrentRead();
        await Test8_ConcurrentReadWrite();
        await Test9_ConcurrentBatchAndSearch();
    }

    // ==================== 7. 并发读取测试 ====================
    private static async Task Test7_ConcurrentRead()
    {
        Console.WriteLine("\n═══ 7. 并发读取测试（多线程同时搜索多向量字段）═══");

        var path = "test_concurrent_read.vdb";
        var random = new Random(42);

        var db = new MyMultiVectorDb(path, StorageFormat.Binary);
        for (int i = 0; i < 3000; i++)
        {
            db.Items.Add(new MultiVectorEntity
            {
                Id = $"CR{i:D5}",
                Label = $"并发读取{i}",
                Score = i,
                IsActive = true,
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
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
    private static async Task Test8_ConcurrentReadWrite()
    {
        Console.WriteLine("\n═══ 8. 并发读写测试（Upsert + Delete + 多字段 Search）═══");

        var path = "test_concurrent_rw.vdb";
        var random = new Random(42);

        var db = new MyMultiVectorDb(path, StorageFormat.Binary);
        for (int i = 0; i < 1000; i++)
        {
            db.Items.Add(new MultiVectorEntity
            {
                Id = $"RW{i:D5}",
                Label = $"基础{i}",
                Score = i,
                IsActive = true,
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
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
    private static async Task Test9_ConcurrentBatchAndSearch()
    {
        Console.WriteLine("\n═══ 9. 并发 AddRange + 多字段 Search ═══");

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
}
