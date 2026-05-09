using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Storage;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// HNSW 实体：索引使用 HNSW（因此 SaveAsync 会写出 <c>IndexSnapshot</c> 段）。向量走堆内存模式，
/// 以便 <c>FlushTombstonesAsync</c> 能在不被 mmap 文件句柄占用的情况下追加 Tombstone 段。
/// </summary>
public class HealFace
{
    [QuiverKey]
    public string Id { get; set; } = string.Empty;

    [QuiverVector(32)]
    [QuiverIndex(VectorIndexType.HNSW, M = 8, EfConstruction = 64, EfSearch = 32)]
    public float[]? Embedding { get; set; }
}

/// <summary>HNSW + 堆内存模式的数据库上下文。</summary>
public class HealFaceDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    DefaultMetric = DistanceMetric.Cosine,
})
{
    public QuiverSet<HealFace> Faces { get; set; } = null!;
}

/// <summary>
/// 回归测试：HNSW 图快照引用了"实际未写入向量 store 的 id"（非正常退出 / tombstone 造成的
/// 快照与向量数据不一致）时，加载应自愈而不是在后续 Add/Search 解引用缺失向量时抛
/// <c>KeyNotFoundException</c>（例如 "Vector id N not found in mmap store."）。
/// <para>
/// 复现手段（纯公共 API、确定性）：
/// <list type="number">
///   <item><c>SaveAsync</c> 写出包含全部节点的 HNSW <c>IndexSnapshot</c> 段；</item>
///   <item>重开后 <c>Remove</c> 一批实体并 <c>FlushTombstonesAsync</c>：只追加 Tombstone 段，
///         <b>不</b>重写快照——于是文件里的快照仍引用被删实体的 id；</item>
///   <item>再次加载：快照恢复出引用了被删 id 的拓扑，但这些 id 的向量已不在 store。
///         修复后加载阶段会 <c>ReconcileWithStore</c> 剔除悬空节点，后续 Add/Search 不再崩溃。</item>
/// </list>
/// </para>
/// </summary>
public static class SnapshotSelfHealTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 18. HNSW 快照自愈：悬空节点剔除 ═══");

        var rng = new Random(20240607);
        var path = Path.Combine(Path.GetTempPath(), $"quiver_heal_{Guid.NewGuid():N}.qdb");

        const int Dim = 32;
        const int N = 300;

        try
        {
            // ── 阶段 1：建库（HNSW）并 SaveAsync 写出快照 ──
            using (var db = new HealFaceDb(path))
            {
                await db.LoadAsync();
                for (int i = 0; i < N; i++)
                    db.Faces.Add(new HealFace { Id = $"H{i:D4}", Embedding = RandomVector(rng, Dim) });
                await db.SaveAsync();
            }

            // 文件应包含 IndexSnapshot 段（证明快照确实写出）。
            var infoAfterSave = await QuiverDbFile.InspectAsync(path, verifyCrc: false);
            int snapshotSegments = infoAfterSave.Segments.Count(s => s.Kind == SegmentKind.IndexSnapshot);
            Assert(snapshotSegments >= 1, $"SaveAsync 写出 IndexSnapshot 段（实际：{snapshotSegments}）");

            // ── 阶段 2：删除中间一批实体，只 FlushTombstones（保留旧快照不重写）──
            // 删除中段 id：它们极可能是其它节点的邻居，从而触发后续 SearchLayer 的解引用。
            using (var db = new HealFaceDb(path))
            {
                await db.LoadAsync();
                Assert(db.Faces.Count == N, $"重开后 Count == {N}");

                for (int i = 100; i < 160; i++)
                    db.Faces.RemoveByKey($"H{i:D4}");

                await db.FlushTombstonesAsync();
            }

            // 旧快照应仍在文件中（FlushTombstones 只追加 Tombstone 段，不重写快照）。
            var infoAfterFlush = await QuiverDbFile.InspectAsync(path, verifyCrc: false);
            bool stillHasSnapshot = infoAfterFlush.Segments.Any(s => s.Kind == SegmentKind.IndexSnapshot);
            Assert(stillHasSnapshot, "FlushTombstones 后旧 IndexSnapshot 段仍保留（快照引用了已删 id）");

            // ── 阶段 3：再次加载（快照引用悬空 id）→ 加载 + 后续 Add/Search 不应抛异常 ──
            using (var db = new HealFaceDb(path))
            {
                bool loadThrew = false;
                try
                {
                    await db.LoadAsync();
                }
                catch (Exception ex)
                {
                    loadThrew = true;
                    Console.WriteLine($"    LoadAsync 抛异常：{ex.GetType().Name}: {ex.Message}");
                }
                Assert(!loadThrew, "悬空快照下 LoadAsync 不抛异常");

                // 被 tombstone 的实体应已剔除。
                Assert(db.Faces.Count == N - 60, $"重开后存活 Count == {N - 60}");
                Assert(db.Faces.Find("H0120") is null, "被删实体 H0120 查不到");
                Assert(db.Faces.Find("H0050") is not null, "未删实体 H0050 仍在");

                // 关键断言：后续 Add 会走 SearchLayer 沿邻居遍历。修复前会在解引用悬空 id 时抛
                // "Vector id N not found in ... store."；修复后悬空节点已被剔除，不再崩溃。
                bool addThrew = false;
                try
                {
                    for (int i = 0; i < 20; i++)
                        db.Faces.Add(new HealFace { Id = $"NEW{i:D2}", Embedding = RandomVector(rng, Dim) });
                }
                catch (Exception ex)
                {
                    addThrew = true;
                    Console.WriteLine($"    Add 抛异常：{ex.GetType().Name}: {ex.Message}");
                }
                Assert(!addThrew, "自愈后新增实体不抛 \"not found in store\"");

                // Search 同样走 SearchLayer，应正常返回。
                bool searchThrew = false;
                int hitCount = 0;
                try
                {
                    var probe = RandomVector(rng, Dim);
                    hitCount = db.Faces.Search(e => e.Embedding!, probe, topK: 5).Count;
                }
                catch (Exception ex)
                {
                    searchThrew = true;
                    Console.WriteLine($"    Search 抛异常：{ex.GetType().Name}: {ex.Message}");
                }
                Assert(!searchThrew, "自愈后 Search 不抛异常");
                Assert(hitCount > 0, "自愈后 Search 仍返回结果");
            }
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }
    }
}
