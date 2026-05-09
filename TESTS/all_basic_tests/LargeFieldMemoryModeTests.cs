using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Storage;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

public static class LargeFieldMemoryModeTests
{
    public static async Task RunAsync()
    {
        await Test_LazyLoad_MaterializesOnAccess();
        await Test_PagedCache_MaterializesOnAccess();
        await Test_LazyLoad_NullableNullRoundTrip();
        await Test_LazyLoad_SaveWithoutAccess_ReusesSlice();
        await Test_LazyLoad_SaveAsWithoutAccess_ReusesSlice();
        await Test_LazyLoad_RewriteWithoutAccess_ReusesSlicesAcrossSegments();
        await Test_LazyLoad_AssignedPayload_OverridesSlice();
        Test_PagedCache_InvalidCacheSize_Throws();
    }

    private static async Task Test_LazyLoad_SaveAsWithoutAccess_ReusesSlice()
    {
        Console.WriteLine("\n═══ LargeField LazyLoad: SaveAs without access reuses slice ═══");
        var sourcePath = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_saveas_src_{Guid.NewGuid():N}.vdb");
        var copyPath = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_saveas_dst_{Guid.NewGuid():N}.vdb");
        try
        {
            var expectedA = Payload(8192, 17);
            var expectedB = Payload(12288, 29);
            await using (var db = new LargeFieldLazyModeDb(sourcePath))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "SA", Payload = expectedA, Embedding = FilledVector(4, 6f) });
                db.Items.Add(new LargeFieldMemoryEntity { Id = "SB", Payload = expectedB, Embedding = FilledVector(4, 7f) });
                await db.SaveAsync();
            }

            using (var loaded = new LargeFieldLazyModeDb(sourcePath))
            {
                await loaded.LoadAsync();
                Assert(loaded.Items.Find("SA") is not null, "SaveAs 前 SA 可查找但未访问 payload");
                Assert(loaded.Items.Find("SB") is not null, "SaveAs 前 SB 可查找但未访问 payload");
                await loaded.SaveAsync(copyPath);
            }

            File.Delete(sourcePath);

            using var copied = new LargeFieldLazyModeDb(copyPath);
            await copied.LoadAsync();
            AssertPayload(copied.Items.Find("SA"), expectedA, "SaveAs 后 SA payload 保持一致");
            AssertPayload(copied.Items.Find("SB"), expectedB, "SaveAs 后 SB payload 保持一致");
        }
        finally
        {
            CleanupFiles(sourcePath);
            CleanupFiles(copyPath);
        }
    }

    private static async Task Test_LazyLoad_RewriteWithoutAccess_ReusesSlicesAcrossSegments()
    {
        Console.WriteLine("\n═══ LargeField LazyLoad: Rewrite without access reuses slices across segments ═══");
        var path = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_rewrite_reuse_{Guid.NewGuid():N}.vdb");
        try
        {
            var expectedA = Payload(4096, 31);
            var expectedB = Payload(6144, 43);
            var expectedC = Payload(10240, 59);
            await using (var db = new LargeFieldLazyModeDb(path))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "RA", Payload = expectedA, Embedding = FilledVector(4, 8f) });
                db.Items.Add(new LargeFieldMemoryEntity { Id = "RB", Payload = expectedB, Embedding = FilledVector(4, 9f) });
                await db.SaveAsync();

                db.Items.Clear();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "RC", Payload = expectedC, Embedding = FilledVector(4, 10f) });
                await db.AppendAsync();
            }

            var beforeInfo = await QuiverDbFile.InspectAsync(path);
            Assert(beforeInfo.Segments.Count == 6, $"Rewrite 前包含两组 EntityMeta/VectorBlob/Blob 段（实际 {beforeInfo.Segments.Count}）");

            using (var loaded = new LargeFieldLazyModeDb(path))
            {
                await loaded.LoadAsync();
                Assert(loaded.Items.Find("RA") is not null, "Save 前 RA 可查找但未访问 payload");
                Assert(loaded.Items.Find("RB") is not null, "Save 前 RB 可查找但未访问 payload");
                Assert(loaded.Items.Find("RC") is not null, "Save 前 RC 可查找但未访问 payload");
                await loaded.SaveAsync();
            }

            var afterInfo = await QuiverDbFile.InspectAsync(path);
            Assert(afterInfo.Segments.Count == 3, $"Save 后折叠为 EntityMeta/VectorBlob/Blob 三段（实际 {afterInfo.Segments.Count}）");
            Assert(afterInfo.CrcValid, "Save 后 CRC 校验通过");

            using var reloaded = new LargeFieldLazyModeDb(path);
            await reloaded.LoadAsync();
            AssertPayload(reloaded.Items.Find("RA"), expectedA, "Rewrite 后 RA payload 保持一致");
            AssertPayload(reloaded.Items.Find("RB"), expectedB, "Rewrite 后 RB payload 保持一致");
            AssertPayload(reloaded.Items.Find("RC"), expectedC, "Rewrite 后 RC payload 保持一致");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_LazyLoad_SaveWithoutAccess_ReusesSlice()
    {
        Console.WriteLine("\n═══ LargeField LazyLoad: Save without access reuses slice ═══");
        var path = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_reuse_{Guid.NewGuid():N}.vdb");
        try
        {
            var expected = Enumerable.Range(0, 4096).Select(i => (byte)(i % 239)).ToArray();
            await using (var db = new LargeFieldLazyModeDb(path))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "R1", Payload = expected, Embedding = FilledVector(4, 4f) });
                await db.SaveAsync();
            }

            using (var loaded = new LargeFieldLazyModeDb(path))
            {
                await loaded.LoadAsync();
                var item = loaded.Items.Find("R1");
                Assert(item is not null, "slice reuse Find 成功");
                await loaded.SaveAsync();
            }

            using var reloaded = new LargeFieldLazyModeDb(path);
            await reloaded.LoadAsync();
            var reloadedItem = reloaded.Items.Find("R1");
            Assert(reloadedItem is not null, "slice reuse 后重新加载成功");
            Assert(reloadedItem!.Payload is not null && reloadedItem.Payload.SequenceEqual(expected), "未访问 payload 直接 Save 后字节仍一致");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_LazyLoad_AssignedPayload_OverridesSlice()
    {
        Console.WriteLine("\n═══ LargeField LazyLoad: assigned payload overrides slice ═══");
        var path = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_override_{Guid.NewGuid():N}.vdb");
        try
        {
            var original = Enumerable.Range(0, 512).Select(i => (byte)(i % 127)).ToArray();
            var updated = Enumerable.Range(0, 768).Select(i => (byte)(255 - i % 127)).ToArray();
            await using (var db = new LargeFieldLazyModeDb(path))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "O1", Payload = original, Embedding = FilledVector(4, 5f) });
                await db.SaveAsync();
            }

            using (var loaded = new LargeFieldLazyModeDb(path))
            {
                await loaded.LoadAsync();
                var item = loaded.Items.Find("O1")!;
                item.Payload = updated;
                await loaded.SaveAsync();
            }

            using var reloaded = new LargeFieldLazyModeDb(path);
            await reloaded.LoadAsync();
            var reloadedItem = reloaded.Items.Find("O1");
            Assert(reloadedItem is not null, "override 后重新加载成功");
            Assert(reloadedItem!.Payload is not null && reloadedItem.Payload.SequenceEqual(updated), "赋新值后 Save 优先写新 payload");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static void Test_PagedCache_InvalidCacheSize_Throws()
    {
        Console.WriteLine("\n═══ LargeField PagedCache: invalid cache size throws ═══");
        Assert(Throws<InvalidOperationException>(() => _ = new InvalidLargeFieldCacheDb()),
            "LargeFieldMaxCachedPayloads <= 0 时抛 InvalidOperationException");
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is TException)
        {
            return true;
        }
    }

    private static async Task Test_LazyLoad_MaterializesOnAccess()
    {
        Console.WriteLine("\n═══ LargeField LazyLoad: access materializes payload ═══");
        var path = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_{Guid.NewGuid():N}.vdb");
        try
        {
            var expected = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
            await using (var db = new LargeFieldLazyModeDb(path))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "L1", Payload = expected, Embedding = FilledVector(4, 1f) });
                await db.SaveAsync();
            }

            using var loaded = new LargeFieldLazyModeDb(path);
            await loaded.LoadAsync();
            var item = loaded.Items.Find("L1");
            Assert(item is not null, "LazyLoad Find 成功");
            Assert(item!.Payload is not null && item.Payload.SequenceEqual(expected), "LazyLoad 首次访问返回原始 payload");
            Assert(item.Payload is not null && item.Payload.SequenceEqual(expected), "LazyLoad 重复访问仍返回一致 payload");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_PagedCache_MaterializesOnAccess()
    {
        Console.WriteLine("\n═══ LargeField PagedCache: access materializes payload ═══");
        var path = Path.Combine(Path.GetTempPath(), $"quiver_large_paged_{Guid.NewGuid():N}.vdb");
        try
        {
            var expected = Enumerable.Range(0, 2048).Select(i => (byte)(255 - i % 251)).ToArray();
            await using (var db = new LargeFieldPagedModeDb(path))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "P1", Payload = expected, Embedding = FilledVector(4, 2f) });
                await db.SaveAsync();
            }

            using var loaded = new LargeFieldPagedModeDb(path);
            await loaded.LoadAsync();
            var item = loaded.Items.Find("P1");
            Assert(item is not null, "PagedCache Find 成功");
            Assert(item!.Payload is not null && item.Payload.SequenceEqual(expected), "PagedCache 首次访问返回原始 payload");
            Assert(item.Payload is not null && item.Payload.SequenceEqual(expected), "PagedCache 重复访问命中缓存语义一致");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static async Task Test_LazyLoad_NullableNullRoundTrip()
    {
        Console.WriteLine("\n═══ LargeField LazyLoad: nullable null roundtrip ═══");
        var path = Path.Combine(Path.GetTempPath(), $"quiver_large_lazy_null_{Guid.NewGuid():N}.vdb");
        try
        {
            await using (var db = new LargeFieldLazyModeDb(path))
            {
                await db.LoadAsync();
                db.Items.Add(new LargeFieldMemoryEntity { Id = "N1", Payload = null, Embedding = FilledVector(4, 3f) });
                await db.SaveAsync();
            }

            using var loaded = new LargeFieldLazyModeDb(path);
            await loaded.LoadAsync();
            var item = loaded.Items.Find("N1");
            Assert(item is not null, "LazyLoad nullable null Find 成功");
            Assert(item!.Payload is null, "LazyLoad nullable null 保持为 null");
        }
        finally
        {
            CleanupFiles(path);
        }
    }

    private static float[] FilledVector(int dim, float value)
    {
        var vector = new float[dim];
        Array.Fill(vector, value);
        return vector;
    }

    private static byte[] Payload(int length, int seed)
    {
        var payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)((i * seed + 11) % 251);
        return payload;
    }

    private static void AssertPayload(LargeFieldMemoryEntity? entity, byte[] expected, string message)
    {
        Assert(entity is not null, message + "：实体存在");
        Assert(entity!.Payload is not null && entity.Payload.SequenceEqual(expected), message);
    }

    private static void CleanupFiles(string path)
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
        {
            try { File.Delete(f); } catch { }
        }
    }
}

public class LargeFieldLazyModeDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.LazyLoad }
})
{
    public QuiverSet<LargeFieldMemoryEntity> Items { get; set; } = null!;
}

public class LargeFieldPagedModeDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    LargeFields =
    {
        MemoryMode = GlobalLargeFieldMemoryMode.PagedCache,
        MaxCachedPayloads = 2
    }
})
{
    public QuiverSet<LargeFieldMemoryEntity> Items { get; set; } = null!;
}

public class InvalidLargeFieldCacheDb() : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = "invalid_large_field_cache.vdb",
    LargeFields =
    {
        MemoryMode = GlobalLargeFieldMemoryMode.PagedCache,
        MaxCachedPayloads = 0
    }
})
{
    public QuiverSet<LargeFieldMemoryEntity> Items { get; set; } = null!;
}

public partial class LargeFieldMemoryEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverLargeField(Nullable = true)] public partial byte[]? Payload { get; set; }
    [QuiverVector(4)] public float[] Embedding { get; set; } = [];
}
