using NAudio.Wave;
using TorchSharp;
using static TorchSharp.torch;

namespace NiubiServer.Inferencers;

/// <summary>
/// 音频推断服务：统一管理 GenreClassifier、AudioClassifier（AudioSet）、MertClassifier 三个模型的
/// 生命周期和推断调用。模型路径通过 IConfiguration 注入，模型文件不存在时对应功能自动禁用。
/// </summary>
public sealed class InferenceService : IDisposable
{
    // ── 配置键 ───────────────────────────────────────────────────────────────
    public const string CfgGenreModel    = "Inference:GenreModelPath";
    public const string CfgFbankModel    = "Inference:FbankModelPath";
    public const string CfgAstModel      = "Inference:AstTransformerModelPath";
    public const string CfgMertEncoder   = "Inference:MertEncoderPath";
    public const string CfgMertPooler    = "Inference:MertPoolerPath";

    // ── 模型参数（与原静态类保持一致） ───────────────────────────────────────
    private const int GenreSampleRate  = 16_000;
    private const int GenreMaxSeconds  = 30;
    private const int AstSampleRate    = 16_000;
    private const int AstMaxSeconds    = 10;
    private const int MertSampleRate   = 24_000;
    private const int MertMaxSeconds   = 10;
    private const float AstThreshold   = 0.3f;

    private static readonly DeviceType _device =
        cuda.is_available() ? DeviceType.CUDA : DeviceType.CPU;

    // ── 已加载的模型（null = 该模型不可用） ──────────────────────────────────
    private jit.ScriptModule? _genreModel;
    private jit.ScriptModule? _fbankModel;
    private jit.ScriptModule? _astModel;
    private jit.ScriptModule? _mertEncoder;
    private jit.ScriptModule? _mertPooler;


    public bool GenreAvailable  => _genreModel  is not null;
    public bool AudioSetAvailable => _fbankModel is not null && _astModel is not null;
    public bool MertAvailable   => _mertEncoder  is not null && _mertPooler is not null;

    public InferenceService()
    {

        _genreModel = jit.load(@"F:\genre_classifier.pt", _device);
        _fbankModel = jit.load(@"F:\ast_fbank.pt");
        _astModel = jit.load(@"F:\ast_transformer.pt", _device);
        _mertEncoder = jit.load(@"F:\mert_encoder.pt", _device);
        _mertPooler = jit.load(@"F:\mert_mean_pool.pt", _device);

        _genreModel?.eval();
        _fbankModel?.eval();
        _astModel?.eval();
        _mertEncoder?.eval();
        _mertPooler?.eval();
    }

    // ── 公开推断接口 ─────────────────────────────────────────────────────────

    // Genre 标签表（与 GenreClassifier.Labels 保持一致）
    private static readonly string[] _genreLabels =
        ["迪斯科(disco)", "金属(metal)", "雷鬼(reggae)", "蓝调(blues)", "摇滚(rock)",
         "古典(classical)", "爵士(jazz)", "嘻哈(hiphop)", "乡村(country)", "流行(pop)"];

    /// <summary>
    /// 流派分类 Top-3，返回格式 "摇滚(rock):0.85|流行(pop):0.12|蓝调(blues):0.03"。
    /// 模型未加载时返回 null。
    /// </summary>
    public string? PredictGenre(string audioFilePath)
    {
        if (_genreModel is null) return null;
        try
        {
            float[] samples = LoadAndNormalize(audioFilePath, GenreSampleRate);
            int windowLen = GenreSampleRate * GenreMaxSeconds;

            float[] avgProbs = SlidingWindowProbAverage(samples, windowLen, maxWindows: 8, buf =>
            {
                using var _ = no_grad();
                using var input  = torch.tensor(buf).unsqueeze(0).to(_device);
                using var logits = ((Tensor)_genreModel.forward(input)).cpu();
                using var probs  = torch.softmax(logits, dim: -1);
                using var flat   = probs.squeeze(0);
                return ExtractFloats(flat, _genreLabels.Length);
            });

            var top3 = avgProbs
                .Select((p, i) => (label: _genreLabels[i], prob: p))
                .OrderByDescending(x => x.prob)
                .Take(3);

            return string.Join("|", top3.Select(x => $"{x.label}:{x.prob:F4}"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Genre 推断失败: {audioFilePath}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// AudioSet 命中标签（sigmoid 超过阈值），返回格式 "音乐(Music):0.91|说唱(Rapping):0.45"。
    /// 无命中时返回概率最高的一个标签。模型未加载时返回 null。
    /// </summary>
    public string? PredictAudioSet(string audioFilePath)
    {
        if (_fbankModel is null || _astModel is null) return null;
        try
        {
            float[] samples = LoadAudioMono(audioFilePath, AstSampleRate);
            int windowLen = AstSampleRate * AstMaxSeconds;

            float[] avgProbs = SlidingWindowProbAverage(samples, windowLen, maxWindows: 8, buf =>
            {
                using var _ = no_grad();
                using var input  = torch.tensor(buf).unsqueeze(0);
                using var fbank  = (Tensor)_fbankModel.forward(input);
                using var logits = ((Tensor)_astModel.forward(fbank.to(_device))).cpu();
                using var probs  = torch.sigmoid(logits);
                using var flat   = probs.squeeze(0);
                return ExtractFloats(flat, 527);
            });

            var hits = AudioClassifier.WatchedLabels
                .Select(l => (l.name, prob: avgProbs[l.idx]))
                .Where(x => x.prob >= AstThreshold)
                .OrderByDescending(x => x.prob)
                .ToList();

            if (hits.Count == 0)
            {
                var best = AudioClassifier.WatchedLabels
                    .Select(l => (l.name, prob: avgProbs[l.idx]))
                    .MaxBy(x => x.prob);
                hits.Add(best);
            }

            return string.Join("|", hits.Select(x => $"{x.name}:{x.prob:F4}"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioSet 推断失败: {audioFilePath}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// MERT-v1-95M 音频嵌入（768 维均值池化）。
    /// 模型未加载时返回 null。
    /// </summary>
    public float[]? PredictMertEmbedding(string audioFilePath)
    {
        if (_mertEncoder is null || _mertPooler is null) return null;
        try
        {
            float[] samples = LoadAndNormalize(audioFilePath, MertSampleRate);
            int windowLen = MertSampleRate * MertMaxSeconds;

            float[] avgEmbedding = SlidingWindowEmbeddingAverage(samples, windowLen, maxWindows: 8, buf =>
            {
                using var _ = no_grad();
                using var inputValues   = torch.tensor(buf).unsqueeze(0).to(_device);
                using var attentionMask = torch.ones(1, windowLen, dtype: ScalarType.Int64).to(_device);
                using var hidden = (Tensor)_mertEncoder.forward(inputValues, attentionMask);
                using var pooled = ((Tensor)_mertPooler.forward(hidden)).cpu();
                using var flat   = pooled.squeeze(0);
                return ExtractFloats(flat, 768);
            });

            return avgEmbedding;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MERT 推断失败: {audioFilePath}, 错误: {ex.Message}");
            return null;
        }
    }

    // ── 私有辅助 ─────────────────────────────────────────────────────────────

    private static float[] FixLength(float[] samples, int targetLen)
    {
        float[] buf = new float[targetLen];
        Array.Copy(samples, buf, Math.Min(samples.Length, targetLen));
        return buf;
    }

    /// <summary>
    /// 将 <paramref name="samples"/> 按 <paramref name="windowLen"/> 切成若干不重叠窗口（最多 <paramref name="maxWindows"/> 个），
    /// 对每个窗口调用 <paramref name="inference"/> 得到概率数组，最终对所有窗口的概率逐元素取均值。
    /// 音频长度不足一个窗口时用零填充后仍推断一次。
    /// </summary>
    private static float[] SlidingWindowProbAverage(
        float[] samples, int windowLen, int maxWindows,
        Func<float[], float[]> inference)
    {
        int totalWindows = Math.Max(1, Math.Min(maxWindows, (int)Math.Ceiling((double)samples.Length / windowLen)));
        float[]? sum = null;

        for (int w = 0; w < totalWindows; w++)
        {
            int offset = w * windowLen;
            float[] buf = new float[windowLen];
            int copyLen = Math.Min(windowLen, Math.Max(0, samples.Length - offset));
            if (copyLen > 0) Array.Copy(samples, offset, buf, 0, copyLen);

            float[] probs = inference(buf);

            if (sum is null)
                sum = probs;
            else
                for (int i = 0; i < sum.Length; i++) sum[i] += probs[i];
        }

        float[] avg = sum!;
        for (int i = 0; i < avg.Length; i++) avg[i] /= totalWindows;
        return avg;
    }

    /// <summary>
    /// 与 <see cref="SlidingWindowProbAverage"/> 相同的滑动逻辑，
    /// 但对各窗口返回的嵌入向量（任意维度）逐元素取均值，适用于 MERT。
    /// </summary>
    private static float[] SlidingWindowEmbeddingAverage(
        float[] samples, int windowLen, int maxWindows,
        Func<float[], float[]> inference)
    {
        return SlidingWindowProbAverage(samples, windowLen, maxWindows, inference);
    }

    private static float[] ExtractFloats(Tensor flat, int count)
    {
        float[] result = new float[count];
        for (int i = 0; i < count; i++)
            result[i] = flat[i].item<float>();
        return result;
    }

    /// <summary>加载为单声道 PCM，不做归一化（供 AST fbank 使用）。</summary>
    private static float[] LoadAudioMono(string path, int sampleRate)
    {
        using var reader  = new AudioFileReader(path);
        var outFmt        = new WaveFormat(sampleRate, 16, 1);
        using var resamp  = new MediaFoundationResampler(reader, outFmt) { ResamplerQuality = 60 };
        var provider      = resamp.ToSampleProvider();

        // 预估容量，减少 List 内部数组扩容和 LOH 碎片
        int estimatedSamples = (int)(reader.TotalTime.TotalSeconds * sampleRate) + 4096;
        var list = new List<float>(estimatedSamples);

        var chunk = new float[4096];
        int n;
        while ((n = provider.Read(chunk, 0, chunk.Length)) > 0)
            for (int i = 0; i < n; i++)
                list.Add(chunk[i]);

        return list.ToArray();
    }

    /// <summary>加载为单声道 PCM 并做 zero-mean/unit-var 归一化（供 Wav2Vec2/MERT 使用）。</summary>
    private static float[] LoadAndNormalize(string path, int sampleRate)
    {
        float[] data = LoadAudioMono(path, sampleRate);

        double mean = 0;
        foreach (var v in data) mean += v;
        mean /= data.Length;

        double variance = 0;
        foreach (var v in data) variance += (v - mean) * (v - mean);
        float std = (float)Math.Sqrt(variance / data.Length + 1e-7);

        for (int i = 0; i < data.Length; i++)
            data[i] = (float)((data[i] - mean) / std);

        return data;
    }


    /// <summary>
    /// 入库完成后主动卸载模型、释放显存，降低常驻内存占用。
    /// 再次调用推理前需重新调用构造或重启服务。
    /// </summary>
    public void UnloadModels()
    {
        _genreModel?.Dispose();   _genreModel   = null;
        _fbankModel?.Dispose();   _fbankModel   = null;
        _astModel?.Dispose();     _astModel     = null;
        _mertEncoder?.Dispose();  _mertEncoder  = null;
        _mertPooler?.Dispose();   _mertPooler   = null;

        // 通知 LibTorch 释放 CUDA 缓存分配器持有的空闲块（需在 GC 前调用）
        if (cuda.is_available())
        {
            try { NativeTorch.EmptyCudaCache(); }
            catch { /* LibTorch 版本不支持时忽略 */ }
        }

        // 在后台线程执行阻塞 GC，避免冻结服务端进程
        Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });

        // 注意：LibTorch 的 CPU 内存缓存池（CachingAllocator）在 Dispose 后仍持有
        // 已提交的虚拟内存，无法从托管侧强制归还 OS。进程内存在模型卸载后会下降，
        // 但不会降回到启动时水位。如需彻底释放，重启服务端进程是唯一方式。
        Console.WriteLine("推理模型已卸载，GC 清理已在后台启动");
    }

    public void Dispose()
    {
        UnloadModels();
    }
}

/// <summary>
/// 直接调用 LibTorch 原生导出函数，用于 TorchSharp 尚未封装的接口。
/// </summary>
internal static class NativeTorch
{
    private const string Lib = "torch_cuda";

    /// <summary>释放 CUDA 缓存分配器持有的所有空闲内存块，归还给 GPU 驱动。</summary>
    [System.Runtime.InteropServices.DllImport(Lib, EntryPoint = "THCudaCachingAllocator_emptyCache",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    internal static extern void EmptyCudaCache();

    /// <summary>强制 OS 将进程工作集中所有可换出的物理页驱逐，立即降低任务管理器显示的内存占用。</summary>
    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);
}
