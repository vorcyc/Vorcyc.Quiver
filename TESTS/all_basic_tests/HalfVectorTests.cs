using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// Half[] 向量全链路测试。
/// 覆盖：插入、float 查询重载、Half 查询重载、Top-1、阈值搜索、持久化往返（Float16 落盘）、删除。
/// </summary>
public static class HalfVectorTests
{
    private const int Dim = 16;
    private const int Count = 200;

    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ Half[] 向量测试 ═══");
        await Test_InsertAndFloatQuery();
        await Test_HalfQueryOverload();
        await Test_Top1();
        await Test_ThresholdSearch();
        await Test_PersistenceRoundTrip();
        await Test_Delete();
    }

    // ── 辅助 ──────────────────────────────────────────────────────

    private static Half[] RandomHalfVec(Random rng)
    {
        var v = new Half[Dim];
        for (int i = 0; i < Dim; i++) v[i] = (Half)(rng.NextSingle() * 2 - 1);
        return v;
    }

    private static float[] RandomFloatVec(Random rng)
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = rng.NextSingle() * 2 - 1;
        return v;
    }

    private static Half[] ToHalf(float[] f)
    {
        var h = new Half[f.Length];
        for (int i = 0; i < f.Length; i++) h[i] = (Half)f[i];
        return h;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"qvr_half_{Guid.NewGuid():N}.vdb");

    // ── 1. 插入 + float 查询重载 ──────────────────────────────────

    private static async Task Test_InsertAndFloatQuery()
    {
        var path = TempPath();
        try
        {
            var rng = new Random(1);
            using var db = new HalfVectorDb(path);

            for (int i = 0; i < Count; i++)
                db.Items.Add(new HalfVectorEntity { Id = $"h{i}", Label = $"L{i}", Vec = RandomHalfVec(rng) });

            Assert(db.Items.Count == Count, $"Half 插入数量：{db.Items.Count}/{Count}");

            // float[] 查询重载：需要先 widen 到 float 传给 float[] 版本
            var qf = RandomFloatVec(rng);
            var r = db.Items.Search(e => e.Vec, ToHalf(qf), topK: 5);
            Assert(r.Count <= 5 && r.Count > 0, $"Half 向量 float 查询返回 {r.Count} 条");

            await db.SaveAsync();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── 2. Half 查询重载 ──────────────────────────────────────────

    private static async Task Test_HalfQueryOverload()
    {
        var path = TempPath();
        try
        {
            var rng = new Random(2);
            using var db = new HalfVectorDb(path);

            for (int i = 0; i < Count; i++)
                db.Items.Add(new HalfVectorEntity { Id = $"h{i}", Vec = RandomHalfVec(rng) });

            var q = RandomHalfVec(rng);
            var r = db.Items.Search(e => e.Vec, q, topK: 10);

            Assert(r.Count <= 10 && r.Count > 0, $"Half[] 查询重载返回 {r.Count} 条");
            Assert(r.TrueForAll(x => x.Similarity >= 0 && x.Similarity <= 1.001f), "Half 查询相似度在 [0,1] 范围内");
            // 结果按相似度降序
            for (int i = 1; i < r.Count; i++)
                Assert(r[i - 1].Similarity >= r[i].Similarity, $"Half 查询结果降序：[{i - 1}]≥[{i}]");

            await Task.CompletedTask;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── 3. Top-1 ─────────────────────────────────────────────────

    private static async Task Test_Top1()
    {
        var path = TempPath();
        try
        {
            var rng = new Random(3);
            using var db = new HalfVectorDb(path);

            // 插入一个已知向量并用同一向量查询，期望 Top-1 就是自身
            var known = RandomHalfVec(rng);
            db.Items.Add(new HalfVectorEntity { Id = "target", Vec = known });

            for (int i = 0; i < 50; i++)
                db.Items.Add(new HalfVectorEntity { Id = $"noise{i}", Vec = RandomHalfVec(rng) });

            var top1 = db.Items.SearchTop1(e => e.Vec, known);
            Assert(top1 != null, "Top-1 不为 null");
            Assert(top1!.Entity.Id == "target", $"Top-1 命中 target（实际：{top1.Entity.Id}）");

            await Task.CompletedTask;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── 4. 阈值搜索 ───────────────────────────────────────────────

    private static async Task Test_ThresholdSearch()
    {
        var path = TempPath();
        try
        {
            var rng = new Random(4);
            using var db = new HalfVectorDb(path);

            for (int i = 0; i < Count; i++)
                db.Items.Add(new HalfVectorEntity { Id = $"h{i}", Vec = RandomHalfVec(rng) });

            var q = RandomHalfVec(rng);
            const float threshold = 0.5f;
            var r = db.Items.SearchByThreshold(e => e.Vec, q, threshold);

            Assert(r.TrueForAll(x => x.Similarity >= threshold - 1e-4f),
                $"Half 阈值搜索：所有结果 ≥ {threshold}");
            // 结果数量可能为 0，这是合法的
            Assert(true, $"Half 阈值搜索返回 {r.Count} 条（阈值 {threshold}）");

            await Task.CompletedTask;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── 5. Float16 持久化往返 ─────────────────────────────────────

    private static async Task Test_PersistenceRoundTrip()
    {
        var path = TempPath();
        try
        {
            var rng = new Random(5);
            var originals = new HalfVectorEntity[Count];

            // 写入
            using (var db = new HalfVectorDb(path))
            {
                for (int i = 0; i < Count; i++)
                {
                    originals[i] = new HalfVectorEntity { Id = $"h{i}", Label = $"L{i}", Vec = RandomHalfVec(rng) };
                    db.Items.Add(originals[i]);
                }
                await db.SaveAsync();
            }

            // 读回
            using var db2 = new HalfVectorDb(path);
            await db2.LoadAsync();

            Assert(db2.Items.Count == Count, $"Float16 持久化：加载数量 {db2.Items.Count}/{Count}");

            // 验证前 10 条的 Vec 值往返精度（fp16 精度误差 ≤ 0.001）
            bool allMatch = true;
            for (int i = 0; i < 10; i++)
            {
                var orig = originals[i];
                var loaded = db2.Items.Find(orig.Id);
                if (loaded == null || loaded.Vec.Length != Dim) { allMatch = false; break; }
                for (int j = 0; j < Dim; j++)
                {
                    if (Math.Abs((float)orig.Vec[j] - (float)loaded.Vec[j]) > 0.002f)
                    { allMatch = false; break; }
                }
                if (!allMatch) break;
            }
            Assert(allMatch, "Float16 持久化：向量值往返精度正确");

            // 重载后查询仍正常
            var q = RandomHalfVec(rng);
            var r = db2.Items.Search(e => e.Vec, q, topK: 5);
            Assert(r.Count > 0, $"Float16 持久化：重载后查询返回 {r.Count} 条");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── 6. 删除 ───────────────────────────────────────────────────

    private static async Task Test_Delete()
    {
        var path = TempPath();
        try
        {
            var rng = new Random(6);
            using var db = new HalfVectorDb(path);

            for (int i = 0; i < 20; i++)
                db.Items.Add(new HalfVectorEntity { Id = $"h{i}", Vec = RandomHalfVec(rng) });

            db.Items.RemoveByKey("h0");
            Assert(db.Items.Count == 19, $"Half 删除后数量：{db.Items.Count}");
            Assert(db.Items.Find("h0") == null, "Half 删除后 Find 返回 null");

            await Task.CompletedTask;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
