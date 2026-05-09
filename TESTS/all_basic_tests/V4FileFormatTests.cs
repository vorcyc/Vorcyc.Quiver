using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Storage;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 13：v4 (QDB\x04) 文件格式 — Append / Rewrite / Merge / Inspect。</summary>
public static class V4FileFormatTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 13. v4 文件格式：Append / Rewrite / Merge / Inspect ═══");

        await Test_AppendThenLoad_PreservesAllEntities();
        await Test_RewriteCollapsesAppendedSegments();
        await Test_Inspect_ReportsSegmentsAndCrc();
        await Test_Merge_Append_RawCopy();
        await Test_Merge_LastWriterWins();
        await Test_Merge_FirstWriterWins();
    }

    private static FaceFeature MakeFace(int i, Random rng) => new()
    {
        PersonId = $"V4{i:D4}",
        Name = $"Person{i}",
        RegisterTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
        Embedding = RandomVector(rng, 128),
    };

    private static async Task Test_AppendThenLoad_PreservesAllEntities()
    {
        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(1);

            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                db.Faces.AddRange(Enumerable.Range(0, 50).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
            }

            // 在新上下文中追加另外 50 条
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                Assert(db.Faces.Count == 50, "Append 前 Count == 50");
                // 清空内存，仅追加增量
                db.Faces.Clear();
                db.Faces.AddRange(Enumerable.Range(50, 50).Select(i => MakeFace(i, rng)));
                await db.AppendAsync();
            }

            // 重新加载，应看到合并后的 100 条
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                Assert(db.Faces.Count == 100, "Append 后 Load 得到 100 条");
                Assert(db.Faces.Find("V40000") is not null, "Find 第一批首条 OK");
                Assert(db.Faces.Find("V40099") is not null, "Find 第二批末条 OK");
            }
        }
        finally { TryDelete(path); }
    }

    private static async Task Test_RewriteCollapsesAppendedSegments()
    {
        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(2);
            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                db.Faces.AddRange(Enumerable.Range(0, 20).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
                db.Faces.Clear();
                db.Faces.AddRange(Enumerable.Range(20, 20).Select(i => MakeFace(i, rng)));
                await db.AppendAsync();
                db.Faces.Clear();
                db.Faces.AddRange(Enumerable.Range(40, 20).Select(i => MakeFace(i, rng)));
                await db.AppendAsync();
            }

            var beforeInfo = await QuiverDbFile.InspectAsync(path);
            // schema v2: 3 次保存 × (EntityMeta + VectorBlob) = 6 段
            Assert(beforeInfo.Segments.Count == 6, $"Save 前段数 == 6（实际 {beforeInfo.Segments.Count}）");

            using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                await db.SaveAsync();
            }

            var afterInfo = await QuiverDbFile.InspectAsync(path);
            Assert(afterInfo.Segments.Count == 2, $"Save 后段数 == 2（实际 {afterInfo.Segments.Count}）");
            Assert(afterInfo.Segments[0].EntityCount == 60, "Rewrite 后单段实体数 == 60");
            Assert(afterInfo.CrcValid, "Rewrite 后 CRC 校验通过");
        }
        finally { TryDelete(path); }
    }

    private static async Task Test_Inspect_ReportsSegmentsAndCrc()
    {
        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(3);
            await using (var db = new MyFaceDb(path))
            {
                await db.LoadAsync();
                db.Faces.AddRange(Enumerable.Range(0, 10).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
            }

            var info = await QuiverDbFile.InspectAsync(path);
            Assert(info.FormatVersion == 4, "Inspect 报告版本 4");
            Assert(info.Segments.Count == 2, "Inspect 段数 == 2");
            Assert(info.CrcValid, "Inspect CRC 通过");
            Assert(info.EntityCounts.TryGetValue(typeof(FaceFeature).FullName!, out var c) && c == 10,
                "Inspect 实体计数 == 10");

            // 故意破坏 1 字节后 CRC 应失败
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Position = info.Segments[0].Offset + 4; // 跳过 magic-ish 区域
                int b = fs.ReadByte();
                fs.Position = info.Segments[0].Offset + 4;
                fs.WriteByte((byte)(b ^ 0xFF));
            }
            var corrupted = await QuiverDbFile.InspectAsync(path);
            Assert(!corrupted.CrcValid, "破坏后 Inspect 报告 CRC 失败");
        }
        finally { TryDelete(path); }
    }

    private static async Task Test_Merge_Append_RawCopy()
    {
        var a = Path.GetTempFileName() + ".vdb";
        var b = Path.GetTempFileName() + ".vdb";
        var dst = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(4);
            await using (var db = new MyFaceDb(a))
            {
                await db.LoadAsync();
                db.Faces.AddRange(Enumerable.Range(0, 5).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
            }
            await using (var db = new MyFaceDb(b))
            {
                await db.LoadAsync();
                db.Faces.AddRange(Enumerable.Range(5, 5).Select(i => MakeFace(i, rng)));
                await db.SaveAsync();
            }

            await QuiverDbFile.MergeAsync([a, b], dst);

            var info = await QuiverDbFile.InspectAsync(dst);
            Assert(info.Segments.Count == 4, "Merge(Append) 段数 == 4");
            Assert(info.CrcValid, "Merge(Append) CRC 通过");

            await using var merged = new MyFaceDb(dst);
            await merged.LoadAsync();
            Assert(merged.Faces.Count == 10, "Merge(Append) 加载后 Count == 10");
        }
        finally { TryDelete(a); TryDelete(b); TryDelete(dst); }
    }

    private static async Task Test_Merge_LastWriterWins()
    {
        var a = Path.GetTempFileName() + ".vdb";
        var b = Path.GetTempFileName() + ".vdb";
        var dst = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(5);
            await using (var db = new MyFaceDb(a))
            {
                await db.LoadAsync();
                var e = MakeFace(1, rng);
                e.Name = "OLD";
                db.Faces.Add(e);
                await db.SaveAsync();
            }
            await using (var db = new MyFaceDb(b))
            {
                await db.LoadAsync();
                var e = MakeFace(1, rng);
                e.Name = "NEW";
                db.Faces.Add(e);
                await db.SaveAsync();
            }

            var typeMap = new Dictionary<string, Type> { [typeof(FaceFeature).FullName!] = typeof(FaceFeature) };
            await QuiverDbFile.MergeAsync([a, b], dst,
                new MergeOptions { ConflictPolicy = MergeConflictPolicy.LastWriterWins },
                typeMap);

            await using var merged = new MyFaceDb(dst);
            await merged.LoadAsync();
            Assert(merged.Faces.Count == 1, "LWW 合并后 Count == 1");
            Assert(merged.Faces.Find("V40001")?.Name == "NEW", "LWW 保留靠后版本");
        }
        finally { TryDelete(a); TryDelete(b); TryDelete(dst); }
    }

    private static async Task Test_Merge_FirstWriterWins()
    {
        var a = Path.GetTempFileName() + ".vdb";
        var b = Path.GetTempFileName() + ".vdb";
        var dst = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(6);
            await using (var db = new MyFaceDb(a))
            {
                await db.LoadAsync();
                var e = MakeFace(1, rng);
                e.Name = "FIRST";
                db.Faces.Add(e);
                await db.SaveAsync();
            }
            await using (var db = new MyFaceDb(b))
            {
                await db.LoadAsync();
                var e = MakeFace(1, rng);
                e.Name = "SECOND";
                db.Faces.Add(e);
                await db.SaveAsync();
            }

            var typeMap = new Dictionary<string, Type> { [typeof(FaceFeature).FullName!] = typeof(FaceFeature) };
            await QuiverDbFile.MergeAsync([a, b], dst,
                new MergeOptions { ConflictPolicy = MergeConflictPolicy.FirstWriterWins },
                typeMap);

            await using var merged = new MyFaceDb(dst);
            await merged.LoadAsync();
            Assert(merged.Faces.Count == 1, "FWW 合并后 Count == 1");
            Assert(merged.Faces.Find("V40001")?.Name == "FIRST", "FWW 保留靠前版本");
        }
        finally { TryDelete(a); TryDelete(b); TryDelete(dst); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
