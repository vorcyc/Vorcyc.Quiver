using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// InMemory 实体容器回归测试。
/// 验证当前默认内存模式下 CRUD、搜索、持久化往返、Count 等场景的正确性。
/// </summary>
public static class InMemoryEntityStoreTests
{
    public static async Task RunAsync()
    {
        await Test_Validate_InMemory_AllowsMissingDatabasePath();
        await Test_InMemory_BasicCrudAndCount();
        await Test_InMemory_PersistenceRoundTrip();
        await Test_InMemory_SearchReturnsResults();
        await Test_InMemory_UpsertAndFind();
        await Test_InMemory_RemoveByKey();
        await Test_InMemory_Clear();
        await Test_InMemory_LargeDataset();
        await Test_InMemory_SearchConsistency();
    }

    private static Task Test_Validate_InMemory_AllowsMissingDatabasePath()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 1. Validate — InMemory 无 DatabasePath 可构造 ═══");

        var noThrow = false;
        try
        {
            _ = new MyLazyLoadDb(null!);
            noThrow = true;
        }
        catch { }

        Assert(noThrow, "InMemory 模式无 DatabasePath 时构造不抛异常");
        return Task.CompletedTask;
    }

    private static async Task Test_InMemory_BasicCrudAndCount()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 2. 基本 CRUD 与 Count ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_basic_{Guid.NewGuid():N}.vdb");
        try
        {
            await using var db = new MyLazyLoadDb(path);
            await db.LoadAsync();

            Assert(db.Faces.Count == 0, "初始 Count == 0");

            var rng = new Random(42);
            db.Faces.Add(new FaceFeature { PersonId = "P001", Name = "Alice", Embedding = RandomVector(rng, 128) });
            db.Faces.Add(new FaceFeature { PersonId = "P002", Name = "Bob", Embedding = RandomVector(rng, 128) });

            Assert(db.Faces.Count == 2, "添加 2 条后 Count == 2");

            db.Faces.RemoveByKey("P001");
            Assert(db.Faces.Count == 1, "删除 1 条后 Count == 1");

            db.Faces.Clear();
            Assert(db.Faces.Count == 0, "Clear 后 Count == 0");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_PersistenceRoundTrip()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 3. 持久化往返 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_rt_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(7);
            var expected = new FaceFeature
            {
                PersonId = "RT001",
                Name = "RoundTrip",
                RegisterTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Embedding = RandomVector(rng, 128)
            };

            await using (var db = new MyLazyLoadDb(path))
            {
                await db.LoadAsync();
                db.Faces.Add(expected);
                await db.SaveAsync();
            }

            await using var db2 = new MyLazyLoadDb(path);
            await db2.LoadAsync();

            Assert(db2.Faces.Count == 1, "往返后 Count == 1");
            var found = db2.Faces.Find("RT001");
            Assert(found is not null, "往返后 Find 成功");
            Assert(found?.Name == "RoundTrip", "往返后 Name 一致");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_SearchReturnsResults()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 4. 搜索返回结果 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_srch_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(99);
            await using var db = new MyLazyLoadDb(path);
            await db.LoadAsync();

            var target = RandomVector(rng, 128);
            db.Faces.Add(new FaceFeature { PersonId = "S001", Name = "Target", Embedding = (float[])target.Clone() });
            for (int i = 0; i < 9; i++)
                db.Faces.Add(new FaceFeature { PersonId = $"S{i + 2:D3}", Name = $"Other{i}", Embedding = RandomVector(rng, 128) });

            var results = db.Faces.Search(target, topK: 3);
            Assert(results.Count == 3, "Search topK=3 返回 3 条结果");
            Assert(results[0].Entity.PersonId == "S001", "最相似的是 Target 本身");
            Assert(results[0].Similarity >= results[1].Similarity, "结果按相似度降序排列");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_UpsertAndFind()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 5. Upsert + Find ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_upsert_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(55);
            await using var db = new MyLazyLoadDb(path);
            await db.LoadAsync();

            db.Faces.Add(new FaceFeature { PersonId = "U001", Name = "Original", Embedding = RandomVector(rng, 128) });
            db.Faces.Upsert(new FaceFeature { PersonId = "U001", Name = "Updated", Embedding = RandomVector(rng, 128) });

            Assert(db.Faces.Count == 1, "Upsert 后 Count 仍为 1");
            Assert(db.Faces.Find("U001")?.Name == "Updated", "Upsert 后 Name 更新为 Updated");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_RemoveByKey()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 6. RemoveByKey ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_rm_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(13);
            await using var db = new MyLazyLoadDb(path);
            await db.LoadAsync();

            db.Faces.Add(new FaceFeature { PersonId = "R001", Name = "Alice", Embedding = RandomVector(rng, 128) });
            db.Faces.Add(new FaceFeature { PersonId = "R002", Name = "Bob", Embedding = RandomVector(rng, 128) });

            var removed = db.Faces.RemoveByKey("R001");
            Assert(removed, "RemoveByKey 已存在的 key 返回 true");
            Assert(db.Faces.Count == 1, "删除后 Count == 1");
            Assert(db.Faces.Find("R001") is null, "删除后 Find 返回 null");

            var notFound = db.Faces.RemoveByKey("R999");
            Assert(!notFound, "RemoveByKey 不存在的 key 返回 false");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_Clear()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 7. Clear ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_clear_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(21);
            await using var db = new MyLazyLoadDb(path);
            await db.LoadAsync();

            for (int i = 0; i < 5; i++)
                db.Faces.Add(new FaceFeature { PersonId = $"C{i:D3}", Name = $"Face{i}", Embedding = RandomVector(rng, 128) });

            Assert(db.Faces.Count == 5, "Clear 前 Count == 5");
            db.Faces.Clear();
            Assert(db.Faces.Count == 0, "Clear 后 Count == 0");
            Assert(db.Faces.Find("C000") is null, "Clear 后 Find 返回 null");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_LargeDataset()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 8. 大数据集（1000 条）═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_large_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(77);
            await using var db = new MyLazyLoadDb(path);
            await db.LoadAsync();

            const int n = 1000;
            for (int i = 0; i < n; i++)
                db.Faces.Add(new FaceFeature
                {
                    PersonId = $"L{i:D4}",
                    Name = $"Face{i}",
                    Embedding = RandomVector(rng, 128)
                });

            Assert(db.Faces.Count == n, $"大数据集 Count == {n}");
            Assert(db.Faces.Find("L0500") is not null, "大数据集中 Find 中间记录成功");
            Assert(db.Faces.Find("L0999") is not null, "大数据集中 Find 末条记录成功");

            var q = db.Faces.Find("L0200")!.Embedding;
            var hits = db.Faces.Search(q, topK: 5);
            Assert(hits.Count == 5, "大数据集 Search topK=5 返回 5 条");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_InMemory_SearchConsistency()
    {
        Console.WriteLine("\n═══ InMemoryEntityStore 9. 上下文搜索一致性 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_mem_cmp_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(123);
            var entities = Enumerable.Range(0, 50)
                .Select(i => new FaceFeature
                {
                    PersonId = $"CMP{i:D3}",
                    Name = $"Face{i}",
                    Embedding = RandomVector(rng, 128)
                })
                .ToList();

            var query = RandomVector(rng, 128);

            List<string> fullResults;
            await using (var fullDb = new MyFaceDb(path))
            {
                await fullDb.LoadAsync();
                fullDb.Faces.AddRange(entities);
                await fullDb.SaveAsync();
                fullResults = fullDb.Faces.Search(query, topK: 5).Select(r => r.Entity.PersonId).ToList();
            }

            List<string> secondResults;
            await using (var secondDb = new MyLazyLoadDb(path))
            {
                await secondDb.LoadAsync();
                secondResults = secondDb.Faces.Search(query, topK: 5).Select(r => r.Entity.PersonId).ToList();
            }

            Assert(fullResults.Count == 5 && secondResults.Count == 5, "两个上下文均返回 5 条结果");
            Assert(fullResults.SequenceEqual(secondResults), "两个 InMemory 上下文 Top-5 结果顺序一致");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static void CleanupFiles(string path)
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
        {
            try { File.Delete(f); } catch { }
        }
    }
}
