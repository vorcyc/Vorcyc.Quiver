using NAudio.Wave;
using TorchSharp;
using static TorchSharp.torch;

namespace NiubiServer.Inferencers;

/// <summary>
/// 独立的音乐流派分类器。
/// 使用 dima806/music_genres_classification (Wav2Vec2ForSequenceClassification)
/// 导出的 TorchScript 模型，对音频文件进行 10 类流派分类。
///
/// 前置：先运行 export_genre_model.py 将模型导出为 F:\genre_classifier.pt。
/// </summary>
static class GenreClassifier
{
    const string ModelPath    = @"F:\genre_classifier.pt";
    const string MusicFolder  = @"G:\歌曲宝\1-100000\1-5000";
    static string OutputCsv   => Path.Combine(@"F:\", new DirectoryInfo(MusicFolder).Name + "_genre.csv");
    const int    SampleRate   = 16000;   // Wav2Vec2 要求 16 kHz
    const int    MaxSeconds   = 30;

    static readonly string[] Labels =
        ["迪斯科(disco)", "金属(metal)", "雷鬼(reggae)", "蓝调(blues)", "摇滚(rock)",
         "古典(classical)", "爵士(jazz)", "嘻哈(hiphop)", "乡村(country)", "流行(pop)"];

    static readonly string[] AudioExts =
        [".mp3", ".flac", ".wav", ".m4a", ".ogg", ".aac", ".wma"];

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------
    public static void Run()
    {
        Console.WriteLine("加载流派分类模型...");
        using var model = jit.load(ModelPath, DeviceType.CUDA);
        model.eval();

        var files = Directory
            .EnumerateFiles(MusicFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        Console.WriteLine($"找到 {files.Count} 个音频文件，开始分类...\n");

        using var csv = new StreamWriter(OutputCsv, false, System.Text.Encoding.UTF8);
        csv.WriteLine("目录名,文件名,流派1,概率1,流派2,概率2,流派3,概率3");

        int done = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                var top3 = Predict(file, model, topN: 3);
                string name    = Path.GetFileName(file);
                string dir     = Path.GetDirectoryName(file) ?? "";
                csv.WriteLine(
                    $"\"{dir}\",\"{name}\"," +
                    string.Join(",", top3.Select(x => $"\"{x.label}\",{x.prob:F4}")));
                done++;
                Console.WriteLine(
                    $"[{done}/{files.Count}] {dir}  " +
                    $"{top3[0].label}({top3[0].prob:F2})  " +
                    $"{top3[1].label}({top3[1].prob:F2})  " +
                    $"{top3[2].label}({top3[2].prob:F2})");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  WARNING 跳过 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nOK 完成  成功:{done}  失败:{failed}");
        Console.WriteLine($"结果已保存到: {OutputCsv}");
    }

    // ------------------------------------------------------------------
    // 推理：返回按概率降序的 topN 结果
    // ------------------------------------------------------------------
    static List<(string label, float prob)> Predict(
        string audioPath, jit.ScriptModule model, int topN = 3)
    {
        float[] samples = LoadAndNormalize(audioPath);

        int fixedLen = SampleRate * MaxSeconds;
        float[] buf = new float[fixedLen];
        Array.Copy(samples, buf, Math.Min(samples.Length, fixedLen));

        using var _ = no_grad();
        using var input  = torch.tensor(buf).unsqueeze(0).cuda();  // [1, T] → CUDA
        using var logits = ((Tensor)model.forward(input)).cpu();   // [1, 10] → CPU
        using var probs  = torch.softmax(logits, dim: -1);         // [1, 10]
        using var flat   = probs.squeeze(0);                       // [10]

        var result = new List<(string, float)>();
        for (int i = 0; i < Labels.Length; i++)
            result.Add((Labels[i], flat[i].item<float>()));

        return [.. result.OrderByDescending(x => x.Item2).Take(topN)];
    }

    // ------------------------------------------------------------------
    // 加载音频 → 单声道 16 kHz → zero-mean/unit-var 归一化
    // （与 Wav2Vec2FeatureExtractor do_normalize=true 一致）
    // ------------------------------------------------------------------
    static float[] LoadAndNormalize(string path)
    {
        using var reader = new AudioFileReader(path);
        var outFmt = new WaveFormat(SampleRate, 16, 1);
        using var resamp = new MediaFoundationResampler(reader, outFmt);
        resamp.ResamplerQuality = 60;
        var provider = resamp.ToSampleProvider();

        var chunk = new float[4096];
        var list  = new List<float>();
        int n;
        while ((n = provider.Read(chunk, 0, chunk.Length)) > 0)
            list.AddRange(chunk[..n]);

        float[] data = [.. list];

        // zero-mean / unit-var
        double mean = 0;
        foreach (var v in data) mean += v;
        mean /= data.Length;

        double variance = 0;
        foreach (var v in data) variance += (v - mean) * (v - mean);
        variance /= data.Length;
        float std = (float)Math.Sqrt(variance + 1e-7);

        for (int i = 0; i < data.Length; i++)
            data[i] = (float)((data[i] - mean) / std);

        return data;
    }
}
