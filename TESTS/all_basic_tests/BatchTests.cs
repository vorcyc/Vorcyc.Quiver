using System.Diagnostics;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 4：大批量 AddRange 测试（5,000 条多向量）。</summary>
public static class BatchTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 4. 大批量 AddRange 测试（5,000 条多向量）═══");

        var path = "test_batch.vdb";
        var random = new Random(42);

        var db = new MyMultiVectorDb(path);
        var batch = Enumerable.Range(0, 5000).Select(i => new MultiVectorEntity
        {
            Id = $"B{i:D5}",
            Label = $"批量用户{i}",
            Score = i * 0.1,
            IsActive = i % 2 == 0,
            TextEmbedding = RandomVector(random, 384),
            ImageEmbedding = RandomVector(random, 512),
            AudioEmbedding = RandomVector(random, 256)
        }).ToList();

        var sw = Stopwatch.StartNew();
        db.Items.AddRange(batch);
        var addMs = sw.ElapsedMilliseconds;

        sw.Restart();
        await db.SaveAsync();
        var saveMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var dbRead = new MyMultiVectorDb(path);
        await dbRead.LoadAsync();
        var loadMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"  AddRange {addMs}ms / 保存 {saveMs}ms / 加载 {loadMs}ms");

        Assert(dbRead.Items.Count == 5000, "5000 条多向量加载正确");
        Assert(dbRead.Items.Find("B00000")?.Label == "批量用户0", "首条正确");
        Assert(dbRead.Items.Find("B02500")?.Label == "批量用户2500", "中间正确");
        Assert(dbRead.Items.Find("B04999")?.Label == "批量用户4999", "尾条正确");

        var sample = dbRead.Items.Find("B01000")!;
        Assert(sample.TextEmbedding.Length == 384 &&
               sample.ImageEmbedding.Length == 512 &&
               sample.AudioEmbedding.Length == 256,
            "抽样实体三组向量维度正确 (384/512/256)");

        File.Delete(path);
    }
}
