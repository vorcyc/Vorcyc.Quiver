using System.Diagnostics;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 1-2：单向量 / 多向量实体往返测试。</summary>
public static class RoundTripTests
{
    public static async Task RunAsync()
    {
        await Test1_SingleVectorRoundTrip();
        await Test2_MultiVectorRoundTrip();
    }

    // ==================== 1. 单向量实体往返测试（1,000 条）====================
    private static async Task Test1_SingleVectorRoundTrip()
    {
        Console.WriteLine("\n═══ 1. 单向量实体往返测试（1,000 条）═══");

        var path = "test_roundtrip.vdb";
        var random = new Random(42);
        const int entityCount = 1000;
        var baseTime = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var dbWrite = new MyFaceDb(path);

        var originals = new FaceFeature[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            originals[i] = new FaceFeature
            {
                PersonId = $"P{i:D5}",
                Name = $"用户_{i}",
                RegisterTime = baseTime.AddMinutes(i),
                Embedding = RandomVector(random, 128)
            };
            dbWrite.Faces.Add(originals[i]);
        }

        var sw = Stopwatch.StartNew();
        await dbWrite.SaveAsync();
        var saveMs = sw.ElapsedMilliseconds;
        var fileSize = new FileInfo(path).Length;

        sw.Restart();
        var dbRead = new MyFaceDb(path);
        await dbRead.LoadAsync();
        var loadMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"  保存 {saveMs}ms / 加载 {loadMs}ms / 文件 {fileSize:N0} bytes");
        Assert(dbRead.Faces.Count == entityCount, $"实体数量：{dbRead.Faces.Count}/{entityCount}");

        var allMatch = true;
        for (int i = 0; i < entityCount; i++)
        {
            var orig = originals[i];
            var loaded = dbRead.Faces.Find(orig.PersonId);
            if (loaded == null || loaded.Name != orig.Name ||
                loaded.RegisterTime != orig.RegisterTime ||
                loaded.Embedding.Length != 128)
            { allMatch = false; break; }
            for (int j = 0; j < 128; j++)
            {
                if (MathF.Abs(loaded.Embedding[j] - orig.Embedding[j]) > 1e-6f)
                { allMatch = false; break; }
            }
            if (!allMatch) break;
        }
        Assert(allMatch, $"全部 {entityCount} 条逐字段+向量精度校验通过");
        File.Delete(path);
    }

    // ==================== 2. 多向量实体往返测试（2,000 条）====================
    private static async Task Test2_MultiVectorRoundTrip()
    {
        Console.WriteLine("\n═══ 2. 多向量实体往返测试（2,000 条 × 3 个向量字段）═══");

        var path = "test_multi_vec.vdb";
        var random = new Random(42);
        const int entityCount = 2000;
        var dbWrite = new MyMultiVectorDb(path);

        var originals = new MultiVectorEntity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            originals[i] = new MultiVectorEntity
            {
                Id = $"MV{i:D5}",
                Label = $"多向量实体_{i}",
                Score = random.NextDouble() * 100,
                IsActive = i % 3 != 0,
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
            };
            dbWrite.Items.Add(originals[i]);
        }

        var sw = Stopwatch.StartNew();
        await dbWrite.SaveAsync();
        var saveMs = sw.ElapsedMilliseconds;
        var fileSize = new FileInfo(path).Length;

        sw.Restart();
        var dbRead = new MyMultiVectorDb(path);
        await dbRead.LoadAsync();
        var loadMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"  保存 {saveMs}ms / 加载 {loadMs}ms / 文件 {fileSize:N0} bytes");
        Assert(dbRead.Items.Count == entityCount, $"多向量实体数量：{dbRead.Items.Count}/{entityCount}");

        var allMatch = true;
        for (int i = 0; i < entityCount; i++)
        {
            var orig = originals[i];
            var loaded = dbRead.Items.Find(orig.Id);
            if (loaded == null || loaded.Label != orig.Label ||
                MathF.Abs((float)(loaded.Score - orig.Score)) > 1e-4f ||
                loaded.IsActive != orig.IsActive)
            { allMatch = false; break; }
            if (loaded.TextEmbedding.Length != 384 ||
                loaded.ImageEmbedding.Length != 512 ||
                loaded.AudioEmbedding.Length != 256)
            { allMatch = false; break; }
            if (!VectorsEqual(loaded.TextEmbedding, orig.TextEmbedding) ||
                !VectorsEqual(loaded.ImageEmbedding, orig.ImageEmbedding) ||
                !VectorsEqual(loaded.AudioEmbedding, orig.AudioEmbedding))
            { allMatch = false; break; }
        }
        Assert(allMatch, $"全部 {entityCount} 条三组向量精度校验通过");
        File.Delete(path);
    }

    private static bool VectorsEqual(float[] a, float[] b)
    {
        for (int k = 0; k < a.Length; k++)
            if (MathF.Abs(a[k] - b[k]) > 1e-6f) return false;
        return true;
    }
}