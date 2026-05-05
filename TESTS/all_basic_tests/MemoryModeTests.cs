using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// StorageMode 功能测试：验证默认 Heap 存储模式在 CRUD、搜索、持久化往返、多向量、WAL 等场景下的正确性。
/// </summary>
public static class MemoryModeTests
{
    public static async Task RunAsync()
    {
        await Test_Validate_LazyPaging_RequiresDatabasePath();
        await Test_BasicCrudAndSearch();
        await Test_PersistenceRoundTrip();
        await Test_MultiVector();
        await Test_SlotReuse();
        await Test_CapacityGrowth();
        await Test_WalRoundTrip();
        await Test_FullMemory_SearchConsistency();
    }

    /// <summary>测试 21：LazyPaging 模式无 DatabasePath 时 Validate 抛出异常。</summary>
    private static Task Test_Validate_LazyPaging_RequiresDatabasePath()
    {
        Console.WriteLine("\n═══ 21. LazyPaging 模式 Validate 校验 ═══");

        var threw = false;
        try
        {
            // LazyPaging 未设置 DatabasePath 应抛出异常
            _ = new MyLazyLoadDb(null!);
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert(threw, "LazyPaging 无 DatabasePath 时构造抛出异常");

        // FullMemory 模式无 DatabasePath 不抛异常
        var noThrow = false;
        try
        {
            _ = new MyFaceDb(null!);
            noThrow = true;
        }
        catch { }

        Assert(noThrow, "FullMemory 无 DatabasePath 时构造不抛异常");

        return Task.CompletedTask;
    }

    /// <summary>测试 22：基础 CRUD + 搜索。</summary>
    private static Task Test_BasicCrudAndSearch()
    {
        Console.WriteLine("\n═══ 22. 基础 CRUD + 搜索 ═══");

        var path = "test_crud.vdb";
        if (File.Exists(path)) File.Delete(path);

        var db = new MyMmapFaceDb(path);
        var random = new Random(42);

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

        Assert(db.Faces.Count == 100, "添加 100 条后数量正确");

        var query = RandomVector(random, 128);
        var results = db.Faces.Search(e => e.Embedding, query, 5);
        Assert(results.Count == 5, "搜索 Top-5 返回 5 条");
        Assert(results[0].Similarity >= results[4].Similarity, "搜索结果按相似度降序");

        var found = db.Faces.Find("P050");
        Assert(found?.Name == "人物_50", "Find 按主键查找正确");

        db.Faces.Upsert(new FaceFeature
        {
            PersonId = "P000",
            Name = "已更新_0",
            RegisterTime = DateTime.Now,
            Embedding = RandomVector(random, 128)
        });
        Assert(db.Faces.Find("P000")?.Name == "已更新_0", "Upsert 更新正确");

        db.Faces.RemoveByKey("P099");
        Assert(db.Faces.Count == 99, "删除后数量正确（100 - 1 = 99）");
        Assert(db.Faces.Find("P099") == null, "删除后 Find 返回 null");

        var results2 = db.Faces.Search(e => e.Embedding, query, 5);
        Assert(results2.Count == 5, "删除后搜索 Top-5 仍返回 5 条");

        db.Faces.Clear();
        Assert(db.Faces.Count == 0, "Clear 后数量为 0");

        db.Dispose();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>测试 23：持久化往返。</summary>
    private static async Task Test_PersistenceRoundTrip()
    {
        Console.WriteLine("\n═══ 23. 持久化往返 ═══");

        var path = "test_roundtrip.vdb";
        if (File.Exists(path)) File.Delete(path);

        var random = new Random(42);
        var query = RandomVector(new Random(777), 128);

        var db = new MyMmapFaceDb(path);
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

        var dbRead = new MyMmapFaceDb(path);
        await dbRead.LoadAsync();

        Assert(dbRead.Faces.Count == 500, "往返后数量正确：500");
        Assert(dbRead.Faces.Find("RT000")?.Name == "往返_0", "往返后首条数据正确");
        Assert(dbRead.Faces.Find("RT499")?.Name == "往返_499", "往返后尾条数据正确");

        var afterLoad = dbRead.Faces.Search(e => e.Embedding, query, 10);
        Assert(afterLoad.Count == 10, "往返后搜索 Top-10 返回 10 条");

        var rankMatch = true;
        for (int i = 0; i < beforeSave.Count && i < afterLoad.Count; i++)
        {
            if (beforeSave[i].Entity.PersonId != afterLoad[i].Entity.PersonId)
            {
                rankMatch = false;
                break;
            }
        }
        Assert(rankMatch, "往返后搜索 Top-10 排名一致");

        dbRead.Dispose();
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>测试 24：多向量字段。</summary>
    private static async Task Test_MultiVector()
    {
        Console.WriteLine("\n═══ 24. 多向量字段 ═══");

        var path = "test_multi.vdb";
        if (File.Exists(path)) File.Delete(path);

        var random = new Random(42);
        var db = new MyMmapMultiVectorDb(path);

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

        Assert(db.Items.Count == 300, "多向量添加 300 条");

        var textQuery = RandomVector(new Random(111), 384);
        var imageQuery = RandomVector(new Random(222), 512);
        var audioQuery = RandomVector(new Random(333), 256);

        var textResults = db.Items.Search(e => e.TextEmbedding, textQuery, 5);
        var imageResults = db.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
        var audioResults = db.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

        Assert(textResults.Count == 5, "TextEmbedding 搜索 Top-5 正确");
        Assert(imageResults.Count == 5, "ImageEmbedding 搜索 Top-5 正确");
        Assert(audioResults.Count == 5, "AudioEmbedding 搜索 Top-5 正确");

        var anyDiff = textResults[0].Entity.Id != imageResults[0].Entity.Id
                   || imageResults[0].Entity.Id != audioResults[0].Entity.Id;
        Assert(anyDiff, "三字段搜索结果互不相同");

        await db.SaveAsync();
        db.Dispose();

        var dbRead = new MyMmapMultiVectorDb(path);
        await dbRead.LoadAsync();

        Assert(dbRead.Items.Count == 300, "多向量往返后数量正确");
        var textAfter = dbRead.Items.Search(e => e.TextEmbedding, textQuery, 5);
        Assert(textAfter.Count == 5, "多向量往返后搜索正常");

        dbRead.Dispose();
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>测试 25（原 26）：槽位复用（删除后重新添加）。</summary>
    private static Task Test_SlotReuse()
    {
        Console.WriteLine("\n═══ 25. 槽位复用 ═══");

        var path = "test_reuse.vdb";
        if (File.Exists(path)) File.Delete(path);

        var db = new MyMmapFaceDb(path);
        var random = new Random(42);

        for (int i = 0; i < 50; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"R{i:D3}",
                Name = $"复用_{i}",
                Embedding = RandomVector(random, 128)
            });
        }

        for (int i = 0; i < 25; i++)
            db.Faces.RemoveByKey($"R{i:D3}");

        Assert(db.Faces.Count == 25, "删除 25 条后剩 25 条");

        for (int i = 50; i < 75; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"R{i:D3}",
                Name = $"复用_{i}",
                Embedding = RandomVector(random, 128)
            });
        }

        Assert(db.Faces.Count == 50, "复用后数量恢复 50");

        var query = RandomVector(random, 128);
        var results = db.Faces.Search(e => e.Embedding, query, 10);
        Assert(results.Count == 10, "复用后搜索 Top-10 正常");
        Assert(db.Faces.Find("R000") == null, "已删除的旧数据不存在");
        Assert(db.Faces.Find("R050")?.Name == "复用_50", "新添加的数据正确");

        db.Dispose();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>测试 26（原 27）：大量数据增长（2,000 条）。</summary>
    private static Task Test_CapacityGrowth()
    {
        Console.WriteLine("\n═══ 26. 容量增长（2,000 条）═══");

        var path = "test_grow.vdb";
        if (File.Exists(path)) File.Delete(path);

        var db = new MyMmapFaceDb(path);
        var random = new Random(42);

        for (int i = 0; i < 2000; i++)
        {
            db.Faces.Add(new FaceFeature
            {
                PersonId = $"G{i:D4}",
                Name = $"增长_{i}",
                Embedding = RandomVector(random, 128)
            });
        }

        Assert(db.Faces.Count == 2000, "容量增长后数量 2000");

        var query = RandomVector(random, 128);
        var results = db.Faces.Search(e => e.Embedding, query, 10);
        Assert(results.Count == 10, "容量增长后搜索 Top-10 正常");
        Assert(db.Faces.Find("G0000")?.Name == "增长_0", "扩容后首条数据完整");
        Assert(db.Faces.Find("G1999")?.Name == "增长_1999", "扩容后尾条数据完整");

        db.Dispose();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>测试 27（原 28）：WAL 增量持久化往返。</summary>
    private static async Task Test_WalRoundTrip()
    {
        Console.WriteLine("\n═══ 27. WAL 增量持久化 ═══");

        var path = "test_wal2.vdb";
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".wal")) File.Delete(path + ".wal");

        var random = new Random(42);

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
        Assert(File.Exists(path + ".wal"), "WAL 文件已创建");

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

        Assert(db.Faces.Count == 90, "WAL 操作后内存数量 90");
        db.Dispose();

        var dbReplay = new MyMmapWalDb(path);
        await dbReplay.LoadAsync();

        Assert(dbReplay.Faces.Count == 90, $"WAL 回放后数量正确：{dbReplay.Faces.Count}/90");
        Assert(dbReplay.Faces.Find("W000")?.Name == "WAL_已更新_0", "WAL Upsert 数据回放正确");
        Assert(dbReplay.Faces.Find("W095") == null, "WAL 已删除数据回放正确");

        var query = RandomVector(new Random(777), 128);
        var results = dbReplay.Faces.Search(e => e.Embedding, query, 5);
        Assert(results.Count == 5, "WAL 回放后搜索 Top-5 正常");

        dbReplay.Dispose();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".wal")) File.Delete(path + ".wal");
    }

    /// <summary>测试 28（原 29）：两个独立上下文搜索结果一致性。</summary>
    private static Task Test_FullMemory_SearchConsistency()
    {
        Console.WriteLine("\n═══ 28. 两个上下文搜索一致性 ═══");

        var path1 = "test_mode_a.vdb";
        var path2 = "test_mode_b.vdb";

        var dbA = new MyFaceDb(path1);
        var dbB = new MyMmapFaceDb(path2);
        var random = new Random(42);

        for (int i = 0; i < 500; i++)
        {
            var vec = RandomVector(random, 128);
            var entityA = new FaceFeature { PersonId = $"X{i:D3}", Name = $"对比_{i}", Embedding = vec };
            var entityB = new FaceFeature { PersonId = $"X{i:D3}", Name = $"对比_{i}", Embedding = (float[])vec.Clone() };
            dbA.Faces.Add(entityA);
            dbB.Faces.Add(entityB);
        }

        var query = RandomVector(new Random(999), 128);
        var resultsA = dbA.Faces.Search(e => e.Embedding, query, 10);
        var resultsB = dbB.Faces.Search(e => e.Embedding, query, 10);

        Assert(resultsA.Count == resultsB.Count, "两个上下文返回数量一致");

        var rankMatch = true;
        for (int i = 0; i < resultsA.Count; i++)
        {
            if (resultsA[i].Entity.PersonId != resultsB[i].Entity.PersonId)
            {
                rankMatch = false;
                break;
            }
        }
        Assert(rankMatch, "两个上下文 Top-10 排名完全一致");

        var simMatch = true;
        for (int i = 0; i < resultsA.Count; i++)
        {
            if (MathF.Abs(resultsA[i].Similarity - resultsB[i].Similarity) > 1e-5f)
            {
                simMatch = false;
                break;
            }
        }
        Assert(simMatch, "两个上下文 Top-10 相似度数值一致（精度 1e-5）");

        dbA.Dispose();
        dbB.Dispose();
        if (File.Exists(path1)) File.Delete(path1);
        if (File.Exists(path2)) File.Delete(path2);

        return Task.CompletedTask;
    }
}

