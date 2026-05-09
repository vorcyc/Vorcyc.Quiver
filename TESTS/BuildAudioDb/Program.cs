const string AudioDbPath = @"g:\new_audio_v4.vdb";

using var inferencers = new NiubiServer.Inferencers.InferenceService();
await using var db = new AudioDbContext(AudioDbPath);
await db.LoadAsync();

// ====================== 1. 文件准备 ======================
var audioFiles = Directory.EnumerateFiles(@"G:\歌曲宝\200001-300000", "*.mp3", SearchOption.AllDirectories)
                          .ToList();

var existingPaths = new HashSet<string>(
    db.Audios.Select(a => a.AudioFilePath),
    StringComparer.OrdinalIgnoreCase);

var audiosNotInDb = audioFiles
                    .Where(f => !existingPaths.Contains(f))
                    .ToList();

Console.WriteLine($"库中已有文件：{db.Audios.Count}");
Console.WriteLine($"总共发现 MP3 文件: {audioFiles.Count}");
Console.WriteLine($"需要处理的文件: {audiosNotInDb.Count}\n");

// ====================== 2. 取消令牌 & 按键监听 ======================
var cts = new CancellationTokenSource();
var token = cts.Token;

// 新增：独立线程监听 Ctrl + Q
_ = Task.Run(() =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.Q)
            {
                Console.WriteLine("\n\n检测到 Ctrl+Q，正在停止处理...");
                cts.Cancel();
                break;
            }
        }
        Thread.Sleep(100); // 降低 CPU 占用
    }
});

int doneCount = 0;
const int PrintInterval = 10;
const int BatchSize = 30;
int MaxConcurrency = 16;
//int MaxConcurrency = Environment.ProcessorCount;

var semaphore = new SemaphoreSlim(MaxConcurrency);
var tasks = new List<Task>();
var startTime = DateTime.Now;

// 真实速度统计（最近60秒）
var recentCompletions = new System.Collections.Concurrent.ConcurrentQueue<DateTime>();

Console.WriteLine("开始处理... 按 【Ctrl + Q】 可中途停止。\n");

// ====================== 3. 并行处理 ======================
foreach (var audioFile in audiosNotInDb)
{
    if (token.IsCancellationRequested) break;

    // 注意：不要把 token 传进 WaitAsync！否则取消时这里会抛 OperationCanceledException，
    // 而本 foreach 在下方 try/finally 之外，异常会直接冲出程序、跳过 finally 里的最终 SaveAsync，
    // 导致 Ctrl+Q 退出时数据与 HNSW 图快照都没落盘。循环顶部的 IsCancellationRequested 已负责退出。
    await semaphore.WaitAsync();

    tasks.Add(Task.Run(async () =>
    {
        try
        {
            token.ThrowIfCancellationRequested();

            // 推荐做法：每个任务使用局部锁或独立实例
            //using var localInfer = new NiubiServer.Inferencers.InferenceService();  // ← 推荐

            var entity = AudioMediaEntity.ToEntityFromMp3(audioFile);

            entity.GenreTop3 = inferencers.PredictGenre(audioFile);
            entity.AudioLabels = inferencers.PredictAudioSet(audioFile);
            entity.MertEmbedding = inferencers.PredictMertEmbedding(audioFile);

            db.Audios.Add(entity);

            int current = Interlocked.Increment(ref doneCount);

            // 记录完成时间
            recentCompletions.Enqueue(DateTime.Now);

            // ==================== 单个文件打印 ====================
            Console.WriteLine($"[{current}] 处理完成: {Path.GetFileName(audioFile)}");
            Console.WriteLine($"    GenreTop3: {entity.GenreTop3}");
            Console.WriteLine($"    AudioLabels: {entity.AudioLabels}");
            //entity.MertEmbedding?.PrintLine();

            // ==================== 进度 + 真实速度 ====================
            if (current % PrintInterval == 0 || current == audiosNotInDb.Count)
            {
                // 清理超过60秒的记录
                var cutoff = DateTime.Now.AddSeconds(-60);
                while (recentCompletions.TryPeek(out var time) && time < cutoff)
                {
                    recentCompletions.TryDequeue(out _);
                }

                int filesLastMinute = recentCompletions.Count;

                var elapsed = DateTime.Now - startTime;
                var overallSpeedSec = elapsed.TotalSeconds > 0 ? current / elapsed.TotalSeconds : 0;

                Console.WriteLine($"→ 已处理 {current} / {audiosNotInDb.Count} " +
                                $"({(current * 100.0 / audiosNotInDb.Count):F1}%) " +
                                $"- 整体: {overallSpeedSec:F2} 文件/秒 | " +
                                $"最近1分钟: **{filesLastMinute}** 文件/分钟\n");
            }

            if (current % BatchSize == 0)
            {
                await db.SaveAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"处理失败 {Path.GetFileName(audioFile)}: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }, token));
}

// ====================== 4. 等待完成 ======================
try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException) { }
finally
{
    if (doneCount > 0)
    {
        Console.WriteLine($"\n正在保存最后的数据...");
        await db.SaveAsync();
    }

    var totalElapsed = DateTime.Now - startTime;
    var avgPerMinute = totalElapsed.TotalSeconds > 0 ? (doneCount / totalElapsed.TotalSeconds * 60) : 0;

    Console.WriteLine($"\n✅ 全部处理完成！共处理 {doneCount} 个文件。");
    Console.WriteLine($"总用时: {totalElapsed:hh\\:mm\\:ss}   平均速度: {avgPerMinute:F1} 文件/分钟");
}