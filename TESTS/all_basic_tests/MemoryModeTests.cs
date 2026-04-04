using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// MemoryMode 功能测试：验证 FullMemory（默认）和 MemoryMapped 两种内存模式
/// 在 CRUD、搜索、持久化往返、多向量、WAL、槽位复用、容量增长等场景下的正确性。
/// </summary>
public static class MemoryModeTests
{
    public static async Task RunAsync()
    {
        await Test_Validate_MemoryMapped_RequiresDatabasePath();
        await Test_MemoryMapped_BasicCrudAndSearch();
        await Test_MemoryMapped_PersistenceRoundTrip();
        await Test_MemoryMapped_MultiVector();
        await Test_MemoryMapped_ArenaFileCreation();
        await Test_MemoryMapped_SlotReuse();
        await Test_MemoryMapped_CapacityGrowth();
        await Test_MemoryMapped_WalRoundTrip();
        await Test_FullMemory_SearchConsistencyWithMmap();
    }

    /// <summary>测试 21：MemoryMapped 模式无 DatabasePath 时 Validate 抛出异常。</summary>
    private static Task Test_Validate_MemoryMapped_RequiresDatabasePath()
    {
        Console.WriteLine("\n═══ 21. MemoryMapped 模式 Validate 校验 ═══");

        var threw = false;
        try
        {
            // MemoryMapped 模式未设置 DatabasePath 应抛出异常
            _ = new QuiverDbOptions
            {
                MemoryMode = MemoryMode.MemoryMapped,
                DatabasePath = null
            };
            // Validate 是 internal 方法，通过 QuiverDbContext 构造触发
            // 直接构建一个匿名上下文类来触发
            var db = new MyMmapFaceDb(null!, StorageFormat.Binary);
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert(threw, "MemoryMapped 无 DatabasePath 时构造抛出异常");

        // FullMemory 模式无 DatabasePath 不抛异常
        var noThrow = false;
        try
        {
            _ = new MyFaceDb(null!, StorageFormat.Json);
            noThrow = true;
        }
        catch { }

        // FullMemory 允许 null path（内存模式，不需要持久化路径来创建 arena）
        // 注意：如果要 SaveAsync 则仍需 path，但构造不抛异常
        Assert(noThrow, "FullMemory 无 DatabasePath 时构造不抛异常");

        return Task.CompletedTask;
    }

    /// <summary>测试 22：MemoryMapped 模式基础 CRUD + 搜索。</summary>
    private static Task Test_MemoryMapped_BasicCrudAndSearch()
    {
        Console.WriteLine("\n═══ 22. MemoryMapped 基础 CRUD + 搜索 ═══");

        var path = "test_mmap_crud.vdb";
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        var db = new MyMmapFaceDb(path, StorageFormat.Binary);
        var random = new Random(42);

        // Add 100 条
        for (int i = 0; i < 100; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"P{i:D3}",
                Name = $"人物_{i}",
                RegisterTime = DateTime.Now,
                Embedding = RandomVector(random, 128)
            });
        }

        Assert(db.Faces.Count == 100, "Mmap 添加 100 条后数量正确");

        // Search Top-5
        var query = RandomVector(random, 128);
        var results = db.Faces.Search(e => e.Embedding, query, 5);
        Assert(results.Count == 5, "Mmap 搜索 Top-5 返回 5 条");
        Assert(results[0].Similarity >= results[4].Similarity, "Mmap 搜索结果按相似度降序");

        // Find
        var found = db.Faces.Find("P050");
        Assert(found?.Name == "人物_50", "Mmap Find 按主键查找正确");

        // Upsert
        db.Faces.Upsert(new FaceFeature
        {
            PersonId = "P000",
            Name = "已更新_0",
            RegisterTime = DateTime.Now,
            Embedding = RandomVector(random, 128)
        });
        Assert(db.Faces.Find("P000")?.Name == "已更新_0", "Mmap Upsert 更新正确");

        // Remove
        db.Faces.RemoveByKey("P099");
        Assert(db.Faces.Count == 99, "Mmap 删除后数量正确（100 - 1 = 99）");
        Assert(db.Faces.Find("P099") == null, "Mmap 删除后 Find 返回 null");

        // 删除后搜索仍正常
        var results2 = db.Faces.Search(e => e.Embedding, query, 5);
        Assert(results2.Count == 5, "Mmap 删除后搜索 Top-5 仍返回 5 条");

        // Clear
        db.Faces.Clear();
        Assert(db.Faces.Count == 0, "Mmap Clear 后数量为 0");

        db.Dispose();
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        return Task.CompletedTask;
    }

    /// <summary>测试 23：MemoryMapped 模式持久化往返。</summary>
    private static async Task Test_MemoryMapped_PersistenceRoundTrip()
    {
        Console.WriteLine("\n═══ 23. MemoryMapped 持久化往返 ═══");

        var path = "test_mmap_roundtrip.vdb";
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        var random = new Random(42);
        var query = RandomVector(new Random(777), 128);

        // 写入
        var db = new MyMmapFaceDb(path, StorageFormat.Binary);
        for (int i = 0; i < 500; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"RT{i:D3}",
                Name = $"往返_{i}",
                RegisterTime = DateTime.Now,
                Embedding = RandomVector(random, 128)
            });
        }

        var beforeSave = db.Faces.Search(e => e.Embedding, query, 10);
        await db.SaveAsync();
        db.Dispose();

        // 读取（新 DbContext，新 MmapVectorStore 实例）
        CleanupMmapArenaFiles(path, "FaceFeature", ["Embedding"]); // arena 文件在 Dispose 后可删除
        var dbRead = new MyMmapFaceDb(path, StorageFormat.Binary);
        await dbRead.LoadAsync();

        Assert(dbRead.Faces.Count == 500, "Mmap 往返后数量正确：500");

        var first = dbRead.Faces.Find("RT000");
        Assert(first?.Name == "往返_0", "Mmap 往返后首条数据正确");

        var last = dbRead.Faces.Find("RT499");
        Assert(last?.Name == "往返_499", "Mmap 往返后尾条数据正确");

        // 搜索排名一致性（加载后重建索引 + store，排名应与保存前一致）
        var afterLoad = dbRead.Faces.Search(e => e.Embedding, query, 10);
        Assert(afterLoad.Count == 10, "Mmap 往返后搜索 Top-10 返回 10 条");

        var rankMatch = true;
        for (int i = 0; i < beforeSave.Count && i < afterLoad.Count; i++)
        {
            if (beforeSave[i].Entity.PersonId != afterLoad[i].Entity.PersonId)
            {
                rankMatch = false;
                break;
            }
        }
        Assert(rankMatch, "Mmap 往返后搜索 Top-10 排名一致");

        dbRead.Dispose();
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);
    }

    /// <summary>测试 24：MemoryMapped 模式多向量字段。</summary>
    private static async Task Test_MemoryMapped_MultiVector()
    {
        Console.WriteLine("\n═══ 24. MemoryMapped 多向量字段 ═══");

        var path = "test_mmap_multi.vdb";
        CleanupMmapFiles(path, "MultiVectorEntity", ["TextEmbedding", "ImageEmbedding", "AudioEmbedding"]);

        var random = new Random(42);
        var db = new MyMmapMultiVectorDb(path, StorageFormat.Binary);

        for (int i = 0; i < 300; i++)
        {
            db.Items.Add(new MultiVectorEntity
            {
                Id = $"MV{i:D3}",
                Label = $"多向量_{i}",
                Score = i,
                IsActive = true,
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
            });
        }

        Assert(db.Items.Count == 300, "Mmap 多向量添加 300 条");

        // 三字段分别搜索
        var textQuery = RandomVector(new Random(111), 384);
        var imageQuery = RandomVector(new Random(222), 512);
        var audioQuery = RandomVector(new Random(333), 256);

        var textResults = db.Items.Search(e => e.TextEmbedding, textQuery, 5);
        var imageResults = db.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
        var audioResults = db.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

        Assert(textResults.Count == 5, "Mmap TextEmbedding 搜索 Top-5 正确");
        Assert(imageResults.Count == 5, "Mmap ImageEmbedding 搜索 Top-5 正确");
        Assert(audioResults.Count == 5, "Mmap AudioEmbedding 搜索 Top-5 正确");

        // 三字段搜索结果应各不相同（不同向量空间）
        var anyDiff = textResults[0].Entity.Id != imageResults[0].Entity.Id
                   || imageResults[0].Entity.Id != audioResults[0].Entity.Id;
        Assert(anyDiff, "Mmap 三字段搜索结果互不相同");

        // 持久化往返
        await db.SaveAsync();
        db.Dispose();

        CleanupMmapArenaFiles(path, "MultiVectorEntity", ["TextEmbedding", "ImageEmbedding", "AudioEmbedding"]);
        var dbRead = new MyMmapMultiVectorDb(path, StorageFormat.Binary);
        await dbRead.LoadAsync();

        Assert(dbRead.Items.Count == 300, "Mmap 多向量往返后数量正确");

        var textAfter = dbRead.Items.Search(e => e.TextEmbedding, textQuery, 5);
        Assert(textAfter.Count == 5, "Mmap 多向量往返后搜索正常");

        dbRead.Dispose();
        CleanupMmapFiles(path, "MultiVectorEntity", ["TextEmbedding", "ImageEmbedding", "AudioEmbedding"]);
    }

    /// <summary>测试 25：MemoryMapped 模式 arena 文件创建验证。</summary>
    private static Task Test_MemoryMapped_ArenaFileCreation()
    {
        Console.WriteLine("\n═══ 25. MemoryMapped arena 文件创建 ═══");

        var path = "test_mmap_arena.vdb";
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        var db = new MyMmapFaceDb(path, StorageFormat.Binary);

        // 构造后即创建 arena 文件（MmapVectorStore 构造函数调用 CreateMapping）
        var arenaPath = $"{path}.FaceFeature.Embedding.vec";
        Assert(File.Exists(arenaPath), "Mmap arena 文件在构造后即创建");

        // 写入数据后 arena 文件大小 > 0
        db.Faces.Add(new FaceFeature
        {
            PersonId = "A001",
            Name = "测试",
            Embedding = RandomVector(new Random(42), 128)
        });

        var fileSize = new FileInfo(arenaPath).Length;
        Assert(fileSize > 0, $"Mmap arena 文件大小 > 0（{fileSize} bytes）");

        db.Dispose();
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        return Task.CompletedTask;
    }

    /// <summary>测试 26：MemoryMapped 槽位复用（删除后重新添加）。</summary>
    private static Task Test_MemoryMapped_SlotReuse()
    {
        Console.WriteLine("\n═══ 26. MemoryMapped 槽位复用 ═══");

        var path = "test_mmap_reuse.vdb";
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        var db = new MyMmapFaceDb(path, StorageFormat.Binary);
        var random = new Random(42);

        // 添加 50 条
        for (int i = 0; i < 50; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"R{i:D3}",
                Name = $"复用_{i}",
                Embedding = RandomVector(random, 128)
            });
        }

        // 删除前 25 条（释放 25 个槽位）
        for (int i = 0; i < 25; i++)
            db.Faces.RemoveByKey($"R{i:D3}");

        Assert(db.Faces.Count == 25, "Mmap 删除 25 条后剩 25 条");

        // 重新添加 25 条（应复用释放的槽位）
        for (int i = 50; i < 75; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"R{i:D3}",
                Name = $"复用_{i}",
                Embedding = RandomVector(random, 128)
            });
        }

        Assert(db.Faces.Count == 50, "Mmap 复用后数量恢复 50");

        // 搜索新添加的数据
        var query = RandomVector(random, 128);
        var results = db.Faces.Search(e => e.Embedding, query, 10);
        Assert(results.Count == 10, "Mmap 槽位复用后搜索 Top-10 正常");

        // 验证旧数据确实不存在
        Assert(db.Faces.Find("R000") == null, "Mmap 已删除的旧数据不存在");

        // 验证新数据存在
        Assert(db.Faces.Find("R050")?.Name == "复用_50", "Mmap 新添加的数据正确");

        db.Dispose();
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        return Task.CompletedTask;
    }

    /// <summary>测试 27：MemoryMapped 容量增长（超过 InitialCapacity=1024）。</summary>
    private static Task Test_MemoryMapped_CapacityGrowth()
    {
        Console.WriteLine("\n═══ 27. MemoryMapped 容量增长（2,000 条，超过初始 1,024 槽位）═══");

        var path = "test_mmap_grow.vdb";
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        var db = new MyMmapFaceDb(path, StorageFormat.Binary);
        var random = new Random(42);

        // 添加 2000 条，触发至少一次 Grow
        for (int i = 0; i < 2000; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"G{i:D4}",
                Name = $"增长_{i}",
                Embedding = RandomVector(random, 128)
            });
        }

        Assert(db.Faces.Count == 2000, "Mmap 容量增长后数量 2000");

        // 搜索仍正常（扩容后旧数据完整）
        var query = RandomVector(random, 128);
        var results = db.Faces.Search(e => e.Embedding, query, 10);
        Assert(results.Count == 10, "Mmap 容量增长后搜索 Top-10 正常");

        // 验证首条和尾条数据完整
        Assert(db.Faces.Find("G0000")?.Name == "增长_0", "Mmap 扩容后首条数据完整");
        Assert(db.Faces.Find("G1999")?.Name == "增长_1999", "Mmap 扩容后尾条数据完整");

        // arena 文件大小应 > 初始容量（1024 × 128 × 4 = 524,288 bytes）
        var arenaPath = $"{path}.FaceFeature.Embedding.vec";
        var fileSize = new FileInfo(arenaPath).Length;
        Assert(fileSize > 524_288, $"Mmap arena 文件已扩容（{fileSize:N0} bytes > 524,288）");

        db.Dispose();
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);

        return Task.CompletedTask;
    }

    /// <summary>测试 28：MemoryMapped + WAL 增量持久化往返。</summary>
    private static async Task Test_MemoryMapped_WalRoundTrip()
    {
        Console.WriteLine("\n═══ 28. MemoryMapped + WAL 增量持久化 ═══");

        var path = "test_mmap_wal.vdb";
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);
        if (File.Exists(path + ".wal")) File.Delete(path + ".wal");

        var random = new Random(42);

        // 第 1 轮：写入 100 条，SaveChangesAsync
        var db = new MyMmapWalDb(path);
        for (int i = 0; i < 100; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"W{i:D3}",
                Name = $"WAL_{i}",
                Embedding = RandomVector(random, 128)
            });
        }
        await db.SaveChangesAsync();
        Assert(File.Exists(path + ".wal"), "Mmap+WAL 文件已创建");

        // 第 2 轮：删除 10 条 + Upsert 5 条
        for (int i = 90; i < 100; i++)
            db.Faces.RemoveByKey($"W{i:D3}");

        for (int i = 0; i < 5; i++)
        {
            db.Faces.Upsert(new FaceFeature
            {
                PersonId = $"W{i:D3}",
                Name = $"WAL_已更新_{i}",
                Embedding = RandomVector(random, 128)
            });
        }
        await db.SaveChangesAsync();

        Assert(db.Faces.Count == 90, "Mmap+WAL 操作后内存数量 90");
        db.Dispose();

        // 回放验证
        CleanupMmapArenaFiles(path, "FaceFeature", ["Embedding"]);
        var dbReplay = new MyMmapWalDb(path);
        await dbReplay.LoadAsync();

        Assert(dbReplay.Faces.Count == 90, $"Mmap+WAL 回放后数量正确：{dbReplay.Faces.Count}/90");

        var upserted = dbReplay.Faces.Find("W000");
        Assert(upserted?.Name == "WAL_已更新_0", "Mmap+WAL Upsert 数据回放正确");

        Assert(dbReplay.Faces.Find("W095") == null, "Mmap+WAL 已删除数据回放正确");

        // 搜索正常
        var query = RandomVector(new Random(777), 128);
        var results = dbReplay.Faces.Search(e => e.Embedding, query, 5);
        Assert(results.Count == 5, "Mmap+WAL 回放后搜索 Top-5 正常");

        dbReplay.Dispose();
        CleanupMmapFiles(path, "FaceFeature", ["Embedding"]);
        if (File.Exists(path + ".wal")) File.Delete(path + ".wal");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>测试 29：FullMemory 与 MemoryMapped 搜索结果一致性。</summary>
    private static Task Test_FullMemory_SearchConsistencyWithMmap()
    {
        Console.WriteLine("\n═══ 29. FullMemory vs MemoryMapped 搜索一致性 ═══");

        var heapPath = "test_mode_heap.vdb";
        var mmapPath = "test_mode_mmap.vdb";
        CleanupMmapFiles(mmapPath, "FaceFeature", ["Embedding"]);

        var dbHeap = new MyFaceDb(heapPath, StorageFormat.Binary);
        var dbMmap = new MyMmapFaceDb(mmapPath, StorageFormat.Binary);
        var random = new Random(42);

        // 写入相同数据
        for (int i = 0; i < 500; i++)
        {
            var vec = RandomVector(random, 128);
            var entity1 = new FaceFeature
            {
                PersonId = $"X{i:D3}",
                Name = $"对比_{i}",
                Embedding = vec
            };
            // 深拷贝向量，避免预归一化互相干扰
            var entity2 = new FaceFeature
            {
                PersonId = $"X{i:D3}",
                Name = $"对比_{i}",
                Embedding = (float[])vec.Clone()
            };
            dbHeap.Faces.Add(entity1);
            dbMmap.Faces.Add(entity2);
        }

        // 相同查询向量
        var query = RandomVector(new Random(999), 128);

        var heapResults = dbHeap.Faces.Search(e => e.Embedding, query, 10);
        var mmapResults = dbMmap.Faces.Search(e => e.Embedding, query, 10);

        Assert(heapResults.Count == mmapResults.Count, "Heap vs Mmap 返回数量一致");

        var rankMatch = true;
        for (int i = 0; i < heapResults.Count; i++)
        {
            if (heapResults[i].Entity.PersonId != mmapResults[i].Entity.PersonId)
            {
                rankMatch = false;
                break;
            }
        }
        Assert(rankMatch, "Heap vs Mmap Top-10 排名完全一致");

        // 相似度数值一致（浮点精度允许微小差异）
        var simMatch = true;
        for (int i = 0; i < heapResults.Count; i++)
        {
            if (MathF.Abs(heapResults[i].Similarity - mmapResults[i].Similarity) > 1e-5f)
            {
                simMatch = false;
                break;
            }
        }
        Assert(simMatch, "Heap vs Mmap Top-10 相似度数值一致（精度 1e-5）");

        dbHeap.Dispose();
        dbMmap.Dispose();
        if (File.Exists(heapPath)) File.Delete(heapPath);
        CleanupMmapFiles(mmapPath, "FaceFeature", ["Embedding"]);

        return Task.CompletedTask;
    }

    // ── 辅助方法 ──

    /// <summary>清理 arena 文件和数据库文件。</summary>
    private static void CleanupMmapFiles(string dbPath, string typeName, string[] fields)
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);
        CleanupMmapArenaFiles(dbPath, typeName, fields);
    }

    /// <summary>仅清理 arena 文件（保留数据库文件，用于加载测试）。</summary>
    private static void CleanupMmapArenaFiles(string dbPath, string typeName, string[] fields)
    {
        foreach (var field in fields)
        {
            var arenaPath = $"{dbPath}.{typeName}.{field}.vec";
            if (File.Exists(arenaPath)) File.Delete(arenaPath);
        }
    }
}
