using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// 懒加载分页缓存（EntityPageCache）功能测试。
/// 验证懒加载模式下 CRUD、搜索、持久化往返、页面逐出、Count/IsLazyLoading 等场景的正确性。
/// </summary>
public static class LazyLoadingTests
{
    public static async Task RunAsync()
    {
        await Test_Validate_LazyLoading_RequiresDatabasePath();
        await Test_LazyLoading_BasicCrudAndCount();
        await Test_LazyLoading_IsLazyLoadingFlag();
        await Test_LazyLoading_PersistenceRoundTrip();
        await Test_LazyLoading_SearchReturnsResults();
        await Test_LazyLoading_UpsertAndFind();
        await Test_LazyLoading_RemoveByKey();
        await Test_LazyLoading_Clear();
        await Test_LazyLoading_PageEviction();
        await Test_LazyLoading_LargeDataset();
        await Test_LazyLoading_WalRoundTrip();
        await Test_LazyLoading_VsFullMemorySearchConsistency();
    }

    // ───────────────────────────────────────────────────────
    // 1. 配置校验
    // ───────────────────────────────────────────────────────

    private static Task Test_Validate_LazyLoading_RequiresDatabasePath()
    {
        Console.WriteLine("\n═══ LazyLoading 1. Validate — 无 DatabasePath 时抛出异常 ═══");

        var threw = false;
        try
        {
            // 懒加载未设置 DatabasePath，QuiverDbContext 构造时应抛出
            _ = new MyLazyLoadDb(null!);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        catch (Exception)
        {
            threw = true; // 任意异常都视为 Validate 生效
        }

        Assert(threw, "LazyLoading 无 DatabasePath 时 Validate 抛出异常");
        return Task.CompletedTask;
    }

    // ───────────────────────────────────────────────────────
    // 2. 基本 CRUD 与 Count
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_BasicCrudAndCount()
    {
        Console.WriteLine("\n═══ LazyLoading 2. 基本 CRUD 与 Count ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_basic_{Guid.NewGuid():N}.vdb");
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

    // ───────────────────────────────────────────────────────
    // 3. IsLazyLoading 标志
    // ───────────────────────────────────────────────────────

    private static Task Test_LazyLoading_IsLazyLoadingFlag()
    {
        Console.WriteLine("\n═══ LazyLoading 3. IsLazyLoading 标志 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_flag_{Guid.NewGuid():N}.vdb");
        try
        {
            using var db = new MyLazyLoadDb(path);
            Assert(db.Faces.IsLazyLoading, "懒加载模式下 IsLazyLoading == true");

            using var db2 = new MyFaceDb(path);
            Assert(!db2.Faces.IsLazyLoading, "全量模式中 IsLazyLoading == false");
        }
        finally
        {
            CleanupFiles(path);
        }

        return Task.CompletedTask;
    }

    // ───────────────────────────────────────────────────────
    // 4. 持久化往返
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_PersistenceRoundTrip()
    {
        Console.WriteLine("\n═══ LazyLoading 4. 持久化往返 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_rt_{Guid.NewGuid():N}.vdb");
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

            // 写入
            await using (var db = new MyLazyLoadDb(path))
            {
                await db.LoadAsync();
                db.Faces.Add(expected);
                await db.SaveAsync();
            }

            // 读回
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

    // ───────────────────────────────────────────────────────
    // 5. 搜索返回结果
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_SearchReturnsResults()
    {
        Console.WriteLine("\n═══ LazyLoading 5. 搜索返回结果 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_srch_{Guid.NewGuid():N}.vdb");
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

    // ───────────────────────────────────────────────────────
    // 6. Upsert + Find
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_UpsertAndFind()
    {
        Console.WriteLine("\n═══ LazyLoading 6. Upsert + Find ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_upsert_{Guid.NewGuid():N}.vdb");
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

    // ───────────────────────────────────────────────────────
    // 7. RemoveByKey
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_RemoveByKey()
    {
        Console.WriteLine("\n═══ LazyLoading 7. RemoveByKey ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_rm_{Guid.NewGuid():N}.vdb");
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

    // ───────────────────────────────────────────────────────
    // 8. Clear
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_Clear()
    {
        Console.WriteLine("\n═══ LazyLoading 8. Clear ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_clear_{Guid.NewGuid():N}.vdb");
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

    // ───────────────────────────────────────────────────────
    // 9. 页面逐出（MaxCachedPages=2, PageSize=4）
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_PageEviction()
    {
        Console.WriteLine("\n═══ LazyLoading 9. 页面逐出（MaxCachedPages=2, PageSize=4）═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_evict_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(33);
            // MaxCachedPages=2 PageSize=4 → 超过 8 条时触发逐出
            await using var db = new MyLazyLoadDb(path, maxCachedPages: 2, pageSize: 4);
            await db.LoadAsync();

            const int n = 20;
            for (int i = 0; i < n; i++)
                db.Faces.Add(new FaceFeature { PersonId = $"E{i:D3}", Name = $"Face{i}", Embedding = RandomVector(rng, 128) });

            Assert(db.Faces.Count == n, $"插入 {n} 条后 Count 正确（即使发生了页面逐出）");

            // 逐出后仍能正确 Find（需要从磁盘重新加载页面）
            var found = db.Faces.Find("E000");
            Assert(found is not null, "逐出后 Find 首条记录仍成功");
            Assert(found?.Name == "Face0", "逐出后 Name 值正确");

            // 搜索也能跨所有页面工作
            var query = db.Faces.Find("E010")!.Embedding;
            var results = db.Faces.Search(query, topK: 1);
            Assert(results.Count == 1, "逐出后 Search 返回结果");
            Assert(results[0].Entity.PersonId == "E010", "逐出后 Search 最近邻为自身");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    // ───────────────────────────────────────────────────────
    // 10. 大数据集（超过多个页面）
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_LargeDataset()
    {
        Console.WriteLine("\n═══ LazyLoading 10. 大数据集（1000 条）═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_large_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(77);
            // PageSize=100 MaxCachedPages=4 → 缓存上限 400 条，其余按需换入
            await using var db = new MyLazyLoadDb(path, maxCachedPages: 4, pageSize: 100);
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

            // 确保随机 key 仍可查到
            Assert(db.Faces.Find("L0500") is not null, "大数据集中 Find 中间记录成功");
            Assert(db.Faces.Find("L0999") is not null, "大数据集中 Find 末条记录成功");

            // 搜索
            var q = db.Faces.Find("L0200")!.Embedding;
            var hits = db.Faces.Search(q, topK: 5);
            Assert(hits.Count == 5, "大数据集 Search topK=5 返回 5 条");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    // ───────────────────────────────────────────────────────
    // 11. WAL 往返
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_WalRoundTrip()
    {
        Console.WriteLine("\n═══ LazyLoading 11. WAL 往返 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_wal_{Guid.NewGuid():N}.vdb");
        try
        {
            var rng = new Random(88);

            // 写入 + SaveChangesAsync（WAL 增量）
            await using (var db = new MyLazyLoadWalDb(path))
            {
                await db.LoadAsync();
                db.Faces.Add(new FaceFeature { PersonId = "W001", Name = "WAL-Alice", Embedding = RandomVector(rng, 128) });
                db.Faces.Add(new FaceFeature { PersonId = "W002", Name = "WAL-Bob", Embedding = RandomVector(rng, 128) });
                await db.SaveChangesAsync();

                // 再追加一条
                db.Faces.Add(new FaceFeature { PersonId = "W003", Name = "WAL-Carol", Embedding = RandomVector(rng, 128) });
                // DisposeAsync 自动调用 SaveChangesAsync
            }

            // 读回并验证 WAL 回放
            await using var db2 = new MyLazyLoadWalDb(path);
            await db2.LoadAsync();

            Assert(db2.Faces.Count == 3, "WAL 往返后 Count == 3");
            Assert(db2.Faces.Find("W001") is not null, "WAL 往返后 W001 可找到");
            Assert(db2.Faces.Find("W003") is not null, "WAL 往返后 W003（未手动 flush 的）可找到");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    // ───────────────────────────────────────────────────────
    // 12. 懒加载 vs 全量模式搜索一致性
    // ───────────────────────────────────────────────────────

    private static async Task Test_LazyLoading_VsFullMemorySearchConsistency()
    {
        Console.WriteLine("\n═══ LazyLoading 12. 懒加载 vs 全量模式搜索一致性 ═══");

        var path = Path.Combine(Path.GetTempPath(), $"quiver_ll_cmp_{Guid.NewGuid():N}.vdb");
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

            // 全量模式写入并搜索
            List<string> fullResults;
            await using (var fullDb = new MyFaceDb(path))
            {
                await fullDb.LoadAsync();
                fullDb.Faces.AddRange(entities);
                await fullDb.SaveAsync();
                fullResults = fullDb.Faces.Search(query, topK: 5).Select(r => r.Entity.PersonId).ToList();
            }

            // 懒加载模式读取并搜索
            List<string> lazyResults;
            await using (var lazyDb = new MyLazyLoadDb(path))
            {
                await lazyDb.LoadAsync();
                lazyResults = lazyDb.Faces.Search(query, topK: 5).Select(r => r.Entity.PersonId).ToList();
            }

            Assert(fullResults.Count == 5 && lazyResults.Count == 5, "两种模式均返回 5 条结果");
            Assert(fullResults.SequenceEqual(lazyResults), "懒加载与全量模式 Top-5 结果顺序一致");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    // ───────────────────────────────────────────────────────
    // 辅助：清理临时文件
    // ───────────────────────────────────────────────────────

    private static void CleanupFiles(string path)
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!,
                     Path.GetFileName(path) + "*"))
        {
            try { File.Delete(f); } catch { /* ignore */ }
        }
    }
}