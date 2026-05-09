using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Storage;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 15：Tombstone — 删除后 AppendAsync 写 Tombstone 段，重新加载剔除死实体。</summary>
public static class TombstoneTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 15. Tombstone：Remove + Append + 重开过滤 ═══");

        await Test_RemoveThenAppend_TombstonesDeadEntities();
        await Test_AutoMerge_TriggersRewriteOnSegmentCount();
    }

    private static FaceFeature MakeFace(int i, Random rng) => new()
    {
        PersonId = $"T{i:D4}",
        Name = $"Person{i}",
        RegisterTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
        Embedding = RandomVector(rng, 128),
    };

    private static async Task Test_RemoveThenAppend_TombstonesDeadEntities()
    {
        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(7);

            // 1) 初始保存 20 条
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                db.Faces.AddRange(Enumerable.Range(0, 20).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
            }

            // 2) 重新加载、删除 5 条、FlushTombstonesAsync 只写 Tombstone 段
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                Assert(db.Faces.Count == 20, "初始 Load 后 Count == 20");

                for (int i = 0; i < 5; i++)
                    db.Faces.RemoveByKey($"T{i:D4}");

                Assert(db.Faces.Count == 15, "Remove 5 条后内存 Count == 15");
                await db.FlushTombstonesAsync();
            }

            // 3) 文件检查：必须存在 Tombstone 段
            var info = await QuiverDbFile.InspectAsync(path);
            int tombstoneSegments = info.Segments.Count(s => s.Kind == SegmentKind.Tombstone);
            Assert(tombstoneSegments >= 1, $"文件中存在 Tombstone 段（实际：{tombstoneSegments}）");
            Assert(info.CrcValid, "Tombstone 后 CRC 校验通过");

            // 4) 重新加载，应该只剩 15 条且被删除的主键查不到
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                Assert(db.Faces.Count == 15, $"重新 Load 后 Count == 15（实际：{db.Faces.Count}）");
                for (int i = 0; i < 5; i++)
                    Assert(db.Faces.Find($"T{i:D4}") is null, $"已删除 T{i:D4} 不应再被找到");
                for (int i = 5; i < 20; i++)
                    Assert(db.Faces.Find($"T{i:D4}") is not null, $"未删除 T{i:D4} 仍能找到");
            }

            // 5) Save 后 tombstone 段消失
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                await db.SaveAsync();
            }
            var info2 = await QuiverDbFile.InspectAsync(path);
            int tombstones2 = info2.Segments.Count(s => s.Kind == SegmentKind.Tombstone);
            Assert(tombstones2 == 0, $"Save 后无 Tombstone 段（实际：{tombstones2}）");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task Test_AutoMerge_TriggersRewriteOnSegmentCount()
    {
        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(13);

            // 用 AutoMergeMaxSegments=4 触发自动 merge
            QuiverDbContext NewCtx() => new MyFaceDbWithAutoMerge(path);

            using (var db = NewCtx())
            {
                await db.LoadAsync();
                var faces = (QuiverSet<FaceFeature>)db.GetType().GetProperty("Faces")!.GetValue(db)!;
                faces.AddRange(Enumerable.Range(0, 10).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
            }

            // 连续 Append，应该在中途触发一次 Rewrite，段数最终被压缩。
            for (int round = 0; round < 6; round++)
            {
                using var db = NewCtx();
                await db.LoadAsync();
                var faces = (QuiverSet<FaceFeature>)db.GetType().GetProperty("Faces")!.GetValue(db)!;
                faces.Clear();
                faces.AddRange(Enumerable.Range(round * 10 + 100, 5).Select(i => MakeFace(i, rng)));
                await db.AppendAsync();
            }

            var info = await QuiverDbFile.InspectAsync(path);
            Assert(info.Segments.Count <= 4,
                $"AutoMerge 触发后段数应被压缩到阈值以内（实际：{info.Segments.Count}）");
            Assert(info.CrcValid, "AutoMerge 后 CRC 校验通过");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

/// <summary>启用 background merge 且阈值很小的上下文，用于测试自动合并触发。</summary>
public class MyFaceDbWithAutoMerge(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    EnableBackgroundMerge = true,
    AutoMergeMaxSegments = 4,
})
{
    public QuiverSet<FaceFeature> Faces { get; set; } = null!;
}
