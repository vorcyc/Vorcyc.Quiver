using NAudio.Wave;
using TorchSharp;
using static TorchSharp.torch;

namespace NiubiServer.Inferencers;


/// <summary>
/// 使用 MERT-v1-95M 提取音频嵌入特征 (768 维均值池化向量)。
///
/// 工作流程
/// --------
///   1. 先运行 export_mert_model.py 将模型导出为：
///        F:\mert_encoder.pt      —— Transformer 编码器 (TorchScript)
///        F:\mert_mean_pool.pt    —— 均值池化模块   (TorchScript)
///   2. 运行本程序，批量处理音乐文件夹，将每个文件的 768 维嵌入保存到 CSV。
///
/// 模型输入规格（MERT-v1-95M）
/// --------------------------
///   - 采样率 : 24 000 Hz
///   - 通道数 : 单声道
///   - 数值范围: zero-mean / unit-variance 归一化后的 float32
/// </summary>
static class MertClassifier
{
    const string EncoderPath  = @"F:\mert_encoder.pt";
    const string PoolPath     = @"F:\mert_mean_pool.pt";
    const string MusicFolder  = @"G:\歌曲宝\1-100000\1-5000";
    static string OutputCsv   => Path.Combine(@"F:\", new DirectoryInfo(MusicFolder).Name + "_mert_emb.csv");
    const int SampleRate      = 24000;  // MERT-v1-95M 固定 24 kHz
    const int MaxSeconds      = 10;     // 每个文件最多取前 N 秒

    // 自动选择设备：有 CUDA 就用 GPU，否则退回 CPU
    static readonly DeviceType Device = cuda.is_available() ? DeviceType.CUDA : DeviceType.CPU;

    static readonly string[] AudioExts =
        [".mp3", ".flac", ".wav", ".m4a", ".ogg", ".aac", ".wma"];

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------
    public static void Run()
    {
        Console.WriteLine($"加载 MERT 编码器 (device={Device})...");
        using var encoder  = jit.load(EncoderPath, Device);
        using var pooler   = jit.load(PoolPath,    Device);
        encoder.eval();
        pooler.eval();

        var files = Directory
            .EnumerateFiles(MusicFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        Console.WriteLine($"找到 {files.Count} 个音频文件，开始提取嵌入...\n");

        using var csv = new StreamWriter(OutputCsv, false, System.Text.Encoding.UTF8);
        // 表头：目录名, 文件名, emb_0, emb_1, ..., emb_767
        var header = "目录名,文件名," + string.Join(",", Enumerable.Range(0, 768).Select(i => $"emb_{i}"));
        csv.WriteLine(header);

        int done = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                float[] emb = GetEmbedding(file, encoder, pooler);

                string dir  = Path.GetDirectoryName(file) ?? "";
                string name = Path.GetFileName(file);
                csv.WriteLine($"\"{dir}\",\"{name}\"," + string.Join(",", emb.Select(v => v.ToString("F6"))));

                done++;
                if (done % 100 == 0)
                    Console.WriteLine($"[{done}/{files.Count}] 已完成 {file}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  WARNING 跳过 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nOK 完成  成功:{done}  失败:{failed}");
        Console.WriteLine($"嵌入已保存到: {OutputCsv}");
    }

    // ------------------------------------------------------------------
    // 提取单文件嵌入 → float[768]
    // ------------------------------------------------------------------
    public static float[] GetEmbedding(
        string audioPath,
        jit.ScriptModule encoder,
        jit.ScriptModule pooler)
    {
        float[] samples = LoadAndNormalize(audioPath);

        int fixedLen = SampleRate * MaxSeconds;
        float[] buf  = new float[fixedLen];
        Array.Copy(samples, buf, Math.Min(samples.Length, fixedLen));

        using var _ = no_grad();

        // 输入张量移到与模型相同的设备
        using var inputValues   = torch.tensor(buf).unsqueeze(0).to(Device);
        using var attentionMask = torch.ones(1, fixedLen, dtype: ScalarType.Int64).to(Device);

        // 编码器输出 [1, seq, 768]，均值池化输出 [1, 768]，最后取回 CPU
        using var hidden = (Tensor)encoder.forward(inputValues, attentionMask);
        using var pooled = ((Tensor)pooler.forward(hidden)).cpu();
        using var flat   = pooled.squeeze(0);  // [768]

        float[] result = new float[768];
        for (int i = 0; i < 768; i++)
            result[i] = flat[i].item<float>();

        return result;
    }

    // ------------------------------------------------------------------
    // 加载音频 → 单声道 24 kHz → zero-mean / unit-variance 归一化
    // (与 Wav2Vec2FeatureExtractor do_normalize=true 一致)
    // ------------------------------------------------------------------
    static float[] LoadAndNormalize(string path)
    {
        using var reader  = new AudioFileReader(path);
        var outFmt        = new WaveFormat(SampleRate, 16, 1);
        using var resamp  = new MediaFoundationResampler(reader, outFmt);
        resamp.ResamplerQuality = 60;
        var provider      = resamp.ToSampleProvider();

        var   chunk = new float[4096];
        var   list  = new List<float>();
        int   n;
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
