using System.Diagnostics;
using System.Reflection;
using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>分段测量 JSON 导入：LoadAsync vs ImportAsync 入库阶段。</summary>
internal static class JsonImportProfile
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ JSON Import 分段剖析 ═══");
        Console.WriteLine($"  Machine: {Environment.ProcessorCount} logical processors");

        foreach (var (label, count, dims) in new (string, int, int[])[]
        {
            ("100×128d", 100, [128]),
            ("2000×(384+512+256)", 2000, [384, 512, 256]),
            ("20000×128d", 20000, [128]),
        })
        {
            await ProfileCaseAsync(label, count, dims);
        }
    }

    private static async Task ProfileCaseAsync(string label, int count, int[] dims)
    {
        var random = new Random(42);
        var srcPath = Path.GetTempFileName() + ".vdb";
        var jsonPath = srcPath + ".json";

        try
        {
            await ExportFixtureAsync(srcPath, jsonPath, random, count, dims);

            var jsonBytes = new FileInfo(jsonPath).Length;
            var proc = Process.GetCurrentProcess();
            var provider = CreateJsonProvider();

            // ── A: LoadAsync only ──
            var typeMapDb = CreateDb(dims, Path.GetTempFileName() + ".vdb");
            await using (typeMapDb)
            {
                var typeMap = GetTypeMap(typeMapDb);
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                var loaded = await InvokeLoadAsync(provider, jsonPath, typeMap);
                sw.Stop();
                var tLoad = sw.ElapsedMilliseconds;
                var cpuLoad = proc.TotalProcessorTime - cpu0;
                int entityCount = loaded.Values.Sum(l => l.Count);

                // ── B: Ingest only (mirrors ImportAsync tail, empty db) ──
                var ingestPath = Path.GetTempFileName() + ".vdb";
                long tIngest;
                TimeSpan cpuIngest;
                await using (var ingestDb = CreateDb(dims, ingestPath))
                {
                    await ingestDb.LoadAsync();
                    cpu0 = proc.TotalProcessorTime;
                    sw.Restart();
                    await IngestLoadedSets(ingestDb, loaded);
                    sw.Stop();
                    tIngest = sw.ElapsedMilliseconds;
                    cpuIngest = proc.TotalProcessorTime - cpu0;
                }
                File.Delete(ingestPath);

                // ── C: Full ImportAsync ──
                var fullPath = Path.GetTempFileName() + ".vdb";
                long tFull;
                TimeSpan cpuFull;
                await using (var fullDb = CreateDb(dims, fullPath))
                {
                    await fullDb.LoadAsync();
                    cpu0 = proc.TotalProcessorTime;
                    sw.Restart();
                    await fullDb.ImportAsync(jsonPath, ExportFormat.Json);
                    sw.Stop();
                    tFull = sw.ElapsedMilliseconds;
                    cpuFull = proc.TotalProcessorTime - cpu0;
                }
                File.Delete(fullPath);

                double cores = Environment.ProcessorCount;
                PrintRow(label, jsonBytes, entityCount, cores,
                    "LoadAsync only", tLoad, cpuLoad);
                PrintRow(label, jsonBytes, entityCount, cores,
                    "Ingest only   ", tIngest, cpuIngest);
                PrintRow(label, jsonBytes, entityCount, cores,
                    "ImportAsync   ", tFull, cpuFull);

                Console.WriteLine($"    → Load {Pct(tLoad, tFull)}% + Ingest {Pct(tIngest, tFull)}% ≈ full ({tLoad + tIngest} ms vs {tFull} ms)");
            }
        }
        finally
        {
            foreach (var f in new[] { srcPath, jsonPath })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    private static void PrintRow(string label, long jsonBytes, int entityCount, double cores,
        string phase, long wallMs, TimeSpan cpu)
    {
        if (wallMs == 0) wallMs = 1;
        var util = cpu.TotalMilliseconds / (wallMs * cores) * 100;
        Console.WriteLine($"  [{label}] {phase}: {wallMs,5} ms wall | CPU {cpu.TotalMilliseconds,6:F0} ms | util ≈{util,4:F0}% of {cores:F0} cores | {jsonBytes:N0} B / {entityCount} ent");
    }

    private static double Pct(long part, long whole) => whole == 0 ? 0 : 100.0 * part / whole;

    private static async Task ExportFixtureAsync(string srcPath, string jsonPath, Random random, int count, int[] dims)
    {
        if (dims.Length == 1)
        {
            await using var src = new MyFaceDb(srcPath);
            await src.LoadAsync();
            for (int i = 0; i < count; i++)
                src.Faces.Add(new FaceFeature
                {
                    PersonId = $"P{i:D5}",
                    Name = $"Person{i}",
                    RegisterTime = DateTime.UtcNow.AddDays(i),
                    Embedding = RandomVector(random, dims[0])
                });
            await src.SaveAsync();
            await src.ExportAsync(jsonPath, ExportFormat.Json);
        }
        else
        {
            await using var src = new MyMultiVectorDb(srcPath);
            await src.LoadAsync();
            for (int i = 0; i < count; i++)
                src.Items.Add(new MultiVectorEntity
                {
                    Id = $"Z{i:D5}",
                    Label = $"U{i}",
                    TextEmbedding = RandomVector(random, dims[0]),
                    ImageEmbedding = RandomVector(random, dims[1]),
                    AudioEmbedding = RandomVector(random, dims[2])
                });
            await src.SaveAsync();
            await src.ExportAsync(jsonPath, ExportFormat.Json);
        }
    }

    private static QuiverDbContext CreateDb(int[] dims, string path)
        => dims.Length == 1 ? new MyFaceDb(path) : new MyMultiVectorDb(path);

    private static object CreateJsonProvider()
    {
        var asm = typeof(QuiverDbContext).Assembly;
        var factory = asm.GetType("Vorcyc.Quiver.Storage.ExportStorageProviderFactory", true)!;
        var create = factory.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return create.Invoke(null, [ExportFormat.Json, null])!;
    }

    private static Dictionary<string, Type> GetTypeMap(QuiverDbContext db)
    {
        var field = typeof(QuiverDbContext).GetField("_typeMap", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Dictionary<string, Type>)field.GetValue(db)!;
    }

    private static async Task<Dictionary<string, List<object>>> InvokeLoadAsync(
        object provider, string path, Dictionary<string, Type> typeMap)
    {
        var method = provider.GetType().GetMethod("LoadAsync")!;
        var task = (Task<Dictionary<string, List<object>>>)method.Invoke(provider, [path, typeMap, null])!;
        return await task;
    }

    private static Task IngestLoadedSets(QuiverDbContext db, Dictionary<string, List<object>> loadedSets)
    {
        var setsField = typeof(QuiverDbContext).GetField("_sets", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var addRangeCache = typeof(QuiverDbContext).GetField("_addRangeMethodCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sets = (Dictionary<Type, object>)setsField.GetValue(db)!;
        var addCache = (Dictionary<Type, MethodInfo>)addRangeCache.GetValue(db)!;
        var typeMap = GetTypeMap(db);

        foreach (var (typeName, entities) in loadedSets)
        {
            if (!typeMap.TryGetValue(typeName, out var type) || !sets.TryGetValue(type, out var set))
                continue;
            var cast = typeof(Enumerable).GetMethod("Cast")!.MakeGenericMethod(type);
            var typed = cast.Invoke(null, [entities])!;
            addCache[type].Invoke(set, [typed]);
        }

        return Task.CompletedTask;
    }
}
