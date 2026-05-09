using System.Diagnostics;
using System.Runtime;
using Vorcyc.Quiver;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Storage;

// =====================================================================================
//  Vorcyc.Quiver — 全生命周期内存/时延压测
//  阶段：
//    1) Ingest          —— 分批 Add + AppendAsync + Clear，统计稳态写入内存
//    2) Close & Reopen  —— 新 context + LoadAsync，统计冷启动时间/内存
//    3) Query           —— 在重新加载的库上跑 Search，统计查询时延 + 稳态内存
//    4) Inspect         —— 调用 QuiverDbFile.InspectAsync 查看文件结构 + CRC 校验
//
//  目的：把"现在的库到底能不能用到生产"变成可读的具体数字。
// =====================================================================================

const string DbPath = @"g:\testdb\audios.vdb";
const int TotalCount = 2_000_000;     // 想压什么量就改这里
const int BatchSize  = 50_000;        // Append 批大小
const int VectorDim  = 1024;
const int QueryCount = 100;           // 阶段 3 跑多少次查询取均值

PrintHeader(TotalCount, BatchSize, VectorDim);
EnsureDir(DbPath);
TryDelete(DbPath);

// 注意：QuiverDbContext.DisposeAsync 会自动 SaveAsync —— 写完 Clear 后再 await using
// 会把整个 in-memory（空）状态重写为单个空 segment，覆盖掉所有 Append 的成果。
// 所以在阶段 1/2/3 都用同步 using，跳过 DisposeAsync 的自动保存。

// ─────────────────────────── 阶段 1：写入 ───────────────────────────
await StageIngest(DbPath, TotalCount, BatchSize);

// ─────────────────────────── 阶段 2：冷启动 ───────────────────────────
await StageColdReopenAndLoad(DbPath);

// ─────────────────────────── 阶段 3：查询 ───────────────────────────
await StageQuery(DbPath, QueryCount, VectorDim);

// ─────────────────────────── 阶段 4：文件诊断 ───────────────────────────
await StageInspect(DbPath);

Console.WriteLine();
Console.WriteLine("=== 全部完成 ===");

// =====================================================================================

static async Task StageIngest(string dbPath, int totalCount, int batchSize)
{
    Console.WriteLine();
    Console.WriteLine("──────────── Stage 1: Ingest (batched Append + Clear) ────────────");
    var sw = Stopwatch.StartNew();
    long peakWorking = 0;

    using var db = new AudioDbContext(dbPath);
    await db.LoadAsync();   // 空文件，瞬间返回

    int batches = totalCount / batchSize;
    for (int b = 0; b < batches; b++)
    {
        var batchSw = Stopwatch.StartNew();
        for (int i = 0; i < batchSize; i++)
            db.Audios.Add(AudioEntity.GenerateRandomAudioEntity());

        await db.AppendAsync();
        db.Audios.Clear();   // 关键：释放向量 + 索引
        ForceGc();

        batchSw.Stop();
        var ws = WorkingSetMB();
        if (ws > peakWorking) peakWorking = ws;

        Console.WriteLine(
            $"  batch {b + 1,3}/{batches}  +{batchSize:N0} rows  " +
            $"took {batchSw.Elapsed.TotalSeconds,5:F1}s  " +
            $"workingSet {ws,6:N0} MB  " +
            $"managed {ManagedMB(),6:N0} MB");
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"  写入完成：{totalCount:N0} 条，总耗时 {sw.Elapsed.TotalSeconds:F1}s " +
                      $"({totalCount / sw.Elapsed.TotalSeconds:F0} rows/s)");
    Console.WriteLine($"  写入阶段峰值 working set：{peakWorking:N0} MB");
    Console.WriteLine($"  文件大小：{new FileInfo(dbPath).Length / 1024.0 / 1024:N1} MB");
}

static async Task StageColdReopenAndLoad(string dbPath)
{
    Console.WriteLine();
    Console.WriteLine("──────────── Stage 2: Cold reopen + LoadAsync ────────────");
    ForceGc();
    var baselineWs = WorkingSetMB();
    Console.WriteLine($"  Load 前 working set：{baselineWs:N0} MB");

    var sw = Stopwatch.StartNew();
    using var db = new AudioDbContext(dbPath);
    await db.LoadAsync();
    sw.Stop();

    ForceGc();
    var afterWs = WorkingSetMB();
    var count = db.Audios.Count;

    Console.WriteLine($"  Load 完成：{count:N0} 条，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  Load 后 working set：{afterWs:N0} MB（净增 {afterWs - baselineWs:N0} MB）");
    Console.WriteLine($"  Load 后 managed heap：{ManagedMB():N0} MB");
}

static async Task StageQuery(string dbPath, int queryCount, int vectorDim)
{
    Console.WriteLine();
    Console.WriteLine("──────────── Stage 3: Search latency (cold start) ────────────");
    using var db = new AudioDbContext(dbPath);
    var loadSw = Stopwatch.StartNew();
    await db.LoadAsync();
    loadSw.Stop();
    Console.WriteLine($"  Search 前 Load 耗时：{loadSw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  集合大小：{db.Audios.Count:N0}");

    var rng = new Random(12345);
    var queries = new float[queryCount][];
    for (int i = 0; i < queryCount; i++)
    {
        var q = new float[vectorDim];
        for (int j = 0; j < vectorDim; j++) q[j] = (float)(rng.NextDouble() * 2 - 1);
        queries[i] = q;
    }

    // 预热一次（JIT + 索引缓存命中）
    _ = db.Audios.Search(queries[0], topK: 10);

    // 基线探针：搜索前
    ForceGc();
    var wsBefore = WorkingSetMB();
    var managedBefore = ManagedMB();
    var gen0Before = GC.CollectionCount(0);
    var gen1Before = GC.CollectionCount(1);
    var gen2Before = GC.CollectionCount(2);
    var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    Console.WriteLine($"  [pre-search]  ws={wsBefore:N0}MB  managed={managedBefore:N0}MB  " +
                      $"gen0={gen0Before} gen1={gen1Before} gen2={gen2Before}");

    var latencies = new double[queryCount];
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < queryCount; i++)
    {
        var qsw = Stopwatch.StartNew();
        var hits = db.Audios.Search(queries[i], topK: 10);
        qsw.Stop();
        latencies[i] = qsw.Elapsed.TotalMilliseconds;
        if (hits.Count == 0)
            Console.WriteLine($"  warn: query {i} 返回 0 条结果");
    }
    sw.Stop();

    // 紧接搜索后（未 GC）
    var wsRaw = WorkingSetMB();
    var managedRaw = ManagedMB();
    var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
    Console.WriteLine($"  [raw post]    ws={wsRaw:N0}MB  managed={managedRaw:N0}MB  " +
                      $"alloc/query={(allocAfter - allocBefore) / queryCount / 1024.0:N1}KB");

    // 强制 GC 后看真实驻留
    ForceGc();
    var wsGc = WorkingSetMB();
    var managedGc = ManagedMB();
    var gen0After = GC.CollectionCount(0);
    var gen1After = GC.CollectionCount(1);
    var gen2After = GC.CollectionCount(2);
    Console.WriteLine($"  [post-gc]     ws={wsGc:N0}MB  managed={managedGc:N0}MB  " +
                      $"Δgen0={gen0After - gen0Before} Δgen1={gen1After - gen1Before} Δgen2={gen2After - gen2Before}");
    Console.WriteLine($"  [delta]       ws +{wsGc - wsBefore:N0}MB  managed +{managedGc - managedBefore:N0}MB  " +
                      $"(non-managed delta: {(wsGc - wsBefore) - (managedGc - managedBefore):N0}MB)");

    Array.Sort(latencies);
    Console.WriteLine($"  完成 {queryCount} 次 topK=10 搜索，总耗时 {sw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"  延迟 (ms)：" +
        $"min {latencies[0]:F2}  " +
        $"p50 {latencies[queryCount / 2]:F2}  " +
        $"p95 {latencies[(int)(queryCount * 0.95)]:F2}  " +
        $"p99 {latencies[(int)(queryCount * 0.99)]:F2}  " +
        $"max {latencies[queryCount - 1]:F2}  " +
        $"avg {latencies.Average():F2}");
    Console.WriteLine($"  QPS（单线程）：{queryCount / sw.Elapsed.TotalSeconds:F1}");
}

static async Task StageInspect(string dbPath)
{
    Console.WriteLine();
    Console.WriteLine("──────────── Stage 4: File inspection ────────────");
    var info = await QuiverDbFile.InspectAsync(dbPath, verifyCrc: true);
    Console.WriteLine($"  Format version : v{info.FormatVersion}");
    Console.WriteLine($"  File size      : {info.FileSize / 1024.0 / 1024:N1} MB");
    Console.WriteLine($"  Segment count  : {info.Segments.Count}");
    Console.WriteLine($"  CRC valid      : {info.CrcValid}");
    foreach (var (typeName, count) in info.EntityCounts)
        Console.WriteLine($"    - {typeName} : {count:N0} entities");

    if (info.Segments.Count > 50)
        Console.WriteLine($"  > 段数 {info.Segments.Count} 较多，可考虑 RewriteAsync 合并。");
}

// ─────────────────────────── helpers ───────────────────────────

static void PrintHeader(int total, int batch, int dim)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine($"Vorcyc.Quiver Memory/Latency Lifecycle Test");
    Console.WriteLine($"  TargetCount = {total:N0}, BatchSize = {batch:N0}, Dim = {dim}");
    Console.WriteLine($"  GC = {(GCSettings.IsServerGC ? "Server" : "Workstation")}, " +
                      $"LatencyMode = {GCSettings.LatencyMode}");
    Console.WriteLine($"  Process = {Environment.ProcessId}, Cores = {Environment.ProcessorCount}");
}

static void EnsureDir(string path)
{
    var d = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
}

static void TryDelete(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { }
}

static long WorkingSetMB()
{
    var p = Process.GetCurrentProcess();
    p.Refresh();
    return p.WorkingSet64 / 1024 / 1024;
}

static long ManagedMB() => GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024;

static void ForceGc()
{
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}

// =====================================================================================

public partial class AudioEntity
{
    [QuiverKey] public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [QuiverVector(1024, MemoryMode = VectorMemoryMode.MemoryMapped)]
    public partial float[]? Embedding { get; set; }

    public static AudioEntity GenerateRandomAudioEntity()
    {
        var random = Random.Shared;
        var embedding = new float[1024];
        for (int i = 0; i < 1024; i++)
            embedding[i] = (float)(random.NextDouble() * 2 - 1);

        return new AudioEntity
        {
            Id = Guid.NewGuid(),
            Name = $"Audio_{random.Next(1, 100)}",
            CreatedAt = DateTimeOffset.Now,
            Embedding = embedding,
        };
    }
}

public class AudioDbContext : QuiverDbContext
{
    public QuiverSet<AudioEntity> Audios { get; set; } = null!;

    public AudioDbContext(string databasePath) : base(new QuiverDbOptions
    {
        DatabasePath = databasePath,
        DefaultMetric = DistanceMetric.Cosine,
        LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory },
        Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped },
    })
    { }
}
