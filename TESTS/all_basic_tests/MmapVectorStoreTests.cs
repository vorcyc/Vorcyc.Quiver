using Vorcyc.Quiver;

namespace AllBasicTests;

/// <summary>
/// 带 <c>[QuiverVector(MemoryMode = VectorMemoryMode.MemoryMapped)]</c> 的实体，使 <see cref="Embedding"/> 成为
/// 由 source generator 生成的 lazy partial 属性。
/// </summary>
public partial class MmapFace
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [QuiverVector(64, MemoryMode = VectorMemoryMode.MemoryMapped)]
    public partial float[]? Embedding { get; set; }
}

/// <summary>Mmap 向量存储模式的数据库上下文。</summary>
public class MmapFaceDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
    Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped },
})
{
    public QuiverSet<MmapFace> Faces { get; set; } = null!;
}

/// <summary>
/// 验证 <see cref="VectorMemoryMode.MemoryMapped"/> 端到端：
/// 写入 → SaveAsync 重绑 → 关闭重开 → LoadAsync 走 mmap 路径 → Search 命中 → lazy <c>Embedding</c> 可访问。
/// </summary>
public static class MmapVectorStoreTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine();
Console.WriteLine("══ 14. VectorMemoryMode.MemoryMapped 端到端 ══");

        var rng = new Random(1234);
        var path = Path.Combine(Path.GetTempPath(), $"quiver_mmap_{Guid.NewGuid():N}.qdb");

        try
        {
            // ── 阶段 1：写入并保存 ──
            float[] queryProbe;
            const int N = 200;
            const int Dim = 64;
            {
                await using var db = new MmapFaceDb(path);
                for (int i = 0; i < N; i++)
                {
                    var v = TestHelper.RandomVector(rng, Dim);
                    db.Faces.Add(new MmapFace { Id = $"F{i:D4}", Name = $"name-{i}", Embedding = v });
                }
                queryProbe = (float[])db.Faces.Find("F0042")!.Embedding!.Clone();
                await db.SaveAsync();

                // SaveAsync 后 store 应已 rebind：依然能取到向量、Search 仍然工作。
                var hits = db.Faces.Search(e => e.Embedding!, queryProbe, topK: 5);
                TestHelper.Assert(hits.Count == 5, "保存后 Search 仍返回 topK=5");
                TestHelper.Assert(hits[0].Entity.Id == "F0042", "保存后 Search top-1 仍命中自身");
            }

            // ── 阶段 2：关闭后重新打开 → LoadAsync 走 mmap 路径 ──
            {
                await using var db = new MmapFaceDb(path);
                await db.LoadAsync();
                TestHelper.Assert(db.Faces.Count == N, $"重开后 Count == {N}");

                var found = db.Faces.Find("F0042");
                TestHelper.Assert(found is not null, "重开后 Find 命中 F0042");
                // lazy Embedding：通过 source generator 生成的 partial getter 从 mmap 拉取。
                TestHelper.Assert(found!.Embedding is { Length: Dim }, "lazy Embedding 可访问且维度正确");

                // 数值层面应与原向量一致。
                bool exact = true;
                for (int i = 0; i < Dim; i++)
                    if (Math.Abs(found.Embedding![i] - queryProbe[i]) > 1e-6f) { exact = false; break; }
                TestHelper.Assert(exact, "lazy Embedding 字节级还原与写入一致");

                // 搜索路径直接读 mmap 视图，应能 top-1 命中自身。
                var hits = db.Faces.Search(e => e.Embedding!, queryProbe, topK: 3);
                TestHelper.Assert(hits.Count == 3, "重开后 Search 返回 topK=3");
                TestHelper.Assert(hits[0].Entity.Id == "F0042", "重开后 Search top-1 命中自身");
            }
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }
    }
}
