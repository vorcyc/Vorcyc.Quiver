using System.Diagnostics;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 12-20：WAL 增量持久化系列测试。</summary>
public static class WalTests
{
    public static async Task RunAsync()
    {
        await Test12_WalBasic();
        await Test13_WalCrud();
        await Test14_WalClear();
        await Test15_WalHybrid();
        await Test16_WalAutoCompaction();
        await Test17_WalDisposeAsync();
        await Test18_WalMultiRound();
        await Test19_WalPerformanceComparison();
        await Test20_WalMultiVector();
    }

    // ==================== 12. WAL 增量持久化基础测试 ====================
    private static async Task Test12_WalBasic()
    {
        Console.WriteLine("\n═══ 12. WAL 增量持久化基础测试 ═══");

        var path = "test_wal_basic.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        // 写入 100 条，通过 SaveChangesAsync 仅追加 WAL
        var db = new MyWalDb(path);
        for (int i = 0; i < 100; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"W{i:D4}",
                Name = $"WAL用户_{i}",
                RegisterTime = DateTime.UtcNow,
                Embedding = RandomVector(random, 128)
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
        var results = dbRead.Faces.Search(e => e.Embedding, RandomVector(random, 128), 5);
        Assert(results.Count == 5, "WAL 回放后搜索 Top-5 正常");
        Assert(results[0].Similarity >= results[^1].Similarity, "WAL 回放后搜索结果按相似度降序");

        dbRead.Dispose();
        File.Delete(path);
        File.Delete(walPath);
    }

    // ==================== 13. WAL 删除 + Upsert 回放测试 ====================
    private static async Task Test13_WalCrud()
    {
        Console.WriteLine("\n═══ 13. WAL 删除 + Upsert 回放测试 ═══");

        var path = "test_wal_crud.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        var db = new MyWalDb(path);

        // 添加 200 条
        for (int i = 0; i < 200; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"WC{i:D4}",
                Name = $"原始_{i}",
                RegisterTime = DateTime.UtcNow,
                Embedding = RandomVector(random, 128)
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
                Embedding = RandomVector(random, 128)
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
    private static async Task Test14_WalClear()
    {
        Console.WriteLine("\n═══ 14. WAL Clear 回放测试 ═══");

        var path = "test_wal_clear.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        var db = new MyWalDb(path);
        for (int i = 0; i < 50; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"CL{i:D3}",
                Name = $"清空测试_{i}",
                RegisterTime = DateTime.UtcNow,
                Embedding = RandomVector(random, 128)
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
                Embedding = RandomVector(random, 128)
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
    private static async Task Test15_WalHybrid()
    {
        Console.WriteLine("\n═══ 15. WAL + 快照混合（SaveAsync + SaveChangesAsync）═══");

        var path = "test_wal_hybrid.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        // 阶段 1：写入 100 条 → 全量快照
        var db = new MyWalDb(path);
        for (int i = 0; i < 100; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"H{i:D4}",
                Name = $"快照用户_{i}",
                RegisterTime = DateTime.UtcNow,
                Embedding = RandomVector(random, 128)
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
                Embedding = RandomVector(random, 128)
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
    private static async Task Test16_WalAutoCompaction()
    {
        Console.WriteLine("\n═══ 16. WAL 自动压缩测试（阈值 = 50）═══");

        var path = "test_wal_compact.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

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
                Embedding = RandomVector(random, 128)
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
    private static async Task Test17_WalDisposeAsync()
    {
        Console.WriteLine("\n═══ 17. WAL DisposeAsync 自动保存测试 ═══");

        var path = "test_wal_dispose.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

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
                    Embedding = RandomVector(random, 128)
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
    private static async Task Test18_WalMultiRound()
    {
        Console.WriteLine("\n═══ 18. WAL 多轮增量累积测试 ═══");

        var path = "test_wal_multi_round.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        var db = new MyWalDb(path);

        // 第 1 轮：Add 50
        for (int i = 0; i < 50; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"MR{i:D4}",
                Name = $"轮次1_{i}",
                RegisterTime = DateTime.UtcNow,
                Embedding = RandomVector(random, 128)
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
                Embedding = RandomVector(random, 128)
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
                Embedding = RandomVector(random, 128)
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
    private static async Task Test19_WalPerformanceComparison()
    {
        Console.WriteLine("\n═══ 19. WAL SaveChangesAsync vs SaveAsync 性能对比 ═══");

        var path = "test_wal_perf.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        // 先写入 2000 条基础数据
        var db = new MyWalDb(path);
        var batch = Enumerable.Range(0, 2000).Select(i => new FaceFeature
        {
            PersonId = $"P{i:D5}",
            Name = $"性能测试_{i}",
            RegisterTime = DateTime.UtcNow,
            Embedding = RandomVector(random, 128)
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
                Embedding = RandomVector(random, 128)
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
            Embedding = RandomVector(random, 128)
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
    private static async Task Test20_WalMultiVector()
    {
        Console.WriteLine("\n═══ 20. WAL 多向量实体增量持久化测试 ═══");

        var path = "test_wal_multi_vec.vdb";
        var walPath = path + ".wal";
        var random = new Random(42);

        var db = new MyWalMultiVecDb(path);
        for (int i = 0; i < 200; i++)
        {
            db.Items.Add(new MultiVectorEntity
            {
                Id = $"WM{i:D4}",
                Label = $"WAL多向量_{i}",
                Score = i * 1.5,
                IsActive = i % 2 == 0,
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
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
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
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
        var textResults = dbRead.Items.Search(e => e.TextEmbedding, RandomVector(random, 384), 5);
        var imageResults = dbRead.Items.Search(e => e.ImageEmbedding, RandomVector(random, 512), 5);
        var audioResults = dbRead.Items.Search(e => e.AudioEmbedding, RandomVector(random, 256), 5);
        Assert(textResults.Count == 5 && imageResults.Count == 5 && audioResults.Count == 5,
            "WAL 回放后三字段搜索均返回 Top-5");

        dbRead.Dispose();
        File.Delete(path);
        File.Delete(walPath);
    }
}
