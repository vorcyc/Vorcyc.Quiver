# 音频分析模型文档

本工程使用三个独立的 AI 模型对音频文件进行分析，均通过 Python 导出为 TorchScript 格式后由 C#（TorchSharp）加载推理。

---

## 目录

1. [环境依赖](#环境依赖)
2. [模型一：音乐流派分类 (GenreClassifier)](#模型一音乐流派分类-genreclassifier)
3. [模型二：通用音频场景识别 (AudioClassifier)](#模型二通用音频场景识别-audioclassifier)
4. [模型三：音频嵌入提取 (MertClassifier)](#模型三音频嵌入提取-mertclassifier)
5. [数据流总览](#数据流总览)
6. [C# 项目依赖](#c-项目依赖)

---

## 环境依赖

### Python（模型导出）

```bash
pip install torch torchaudio transformers
```

| 包 | 用途 |
|----|------|
| `torch` | TorchScript trace 与保存 |
| `torchaudio` | AST 模型的 fbank 预处理（kaldi 风格） |
| `transformers` | 加载 HuggingFace 模型权重 |

### C#（模型推理）

| NuGet 包 | 版本 | 用途 |
|----------|------|------|
| `TorchSharp-cuda-windows` | 0.106.0 | TorchScript 推理（对应 PyTorch 2.5，需 CUDA 11.8 或 12.4） |
| `NAudio` | 2.3.0 | 音频文件读取与重采样 |

### CUDA 要求

`TorchSharp-cuda-windows 0.106.0` 对应 **PyTorch 2.5**，需安装：

- **CUDA 12.4**（推荐）或 CUDA 11.8
- **cuDNN 9.x**

验证方式：
```powershell
nvidia-smi       # 查看驱动支持的最高 CUDA 版本
nvcc --version   # 查看已安装的 CUDA 版本
```

---

## 模型一：音乐流派分类 (GenreClassifier)

### 基本信息

| 项目 | 内容 |
|------|------|
| **模型名称** | dima806/music_genres_classification |
| **HuggingFace 地址** | https://huggingface.co/dima806/music_genres_classification |
| **基础架构** | `Wav2Vec2ForSequenceClassification`（基于 facebook/wav2vec2-base-960h 微调） |
| **任务** | 音频多分类（10 类音乐流派） |
| **输入采样率** | 16000 Hz |
| **本地路径** | `F:\music_genres_classification` |
| **导出后路径** | `F:\genre_classifier.pt` |
| **C# 类文件** | `ConsoleApp1/GenreClassifier.cs` |

### 分类标签

| ID | 英文 | 中文 |
|----|------|------|
| 0 | disco | 迪斯科 |
| 1 | metal | 金属 |
| 2 | reggae | 雷鬼 |
| 3 | blues | 蓝调 |
| 4 | rock | 摇滚 |
| 5 | classical | 古典 |
| 6 | jazz | 爵士 |
| 7 | hiphop | 嘻哈 |
| 8 | country | 乡村 |
| 9 | pop | 流行 |

### 推理流程

```
音频文件
  ↓ NAudio 重采样 → 单声道 16 kHz PCM float32
  ↓ zero-mean / unit-var 归一化（对应 do_normalize=true）
  ↓ 截断/padding → 固定长度 [1, 480000]（30秒）
  ↓ CUDA 推理 → logits [1, 10]
  ↓ softmax → 概率分布
  → Top-3 流派 + 概率
```

### Python 导出脚本

文件：`export_genre_model.py`

```python
"""
导出 dima806/music_genres_classification (Wav2Vec2ForSequenceClassification)
为 TorchScript (.pt)，供 TorchSharp jit.load 使用。

依赖:
    pip install torch transformers

运行:
    python export_genre_model.py
"""

import torch
from transformers import Wav2Vec2ForSequenceClassification

MODEL_DIR     = r"F:\music_genres_classification"
OUTPUT_PT     = r"F:\genre_classifier.pt"
SAMPLE_RATE   = 16000   # 模型要求 16 kHz
DUMMY_SECONDS = 5       # trace 用虚拟音频长度（秒）


class GenreWrapper(torch.nn.Module):
    """
    包装器：使输入输出只含 Tensor，满足 TorchScript trace 要求。
    输入:  waveform [1, T] float32，zero-mean/unit-var 归一化后的原始波形
    输出:  logits   [1, 10] float32
    """
    def __init__(self, inner: Wav2Vec2ForSequenceClassification):
        super().__init__()
        self.inner = inner

    def forward(self, waveform: torch.Tensor) -> torch.Tensor:
        out = self.inner(input_values=waveform)
        return out.logits


# 1. 加载模型
print("加载 Wav2Vec2ForSequenceClassification ...")
model = Wav2Vec2ForSequenceClassification.from_pretrained(MODEL_DIR)
model.eval()

wrapper = GenreWrapper(model)
wrapper.eval()

# 2. TorchScript trace
dummy = torch.zeros(1, SAMPLE_RATE * DUMMY_SECONDS)
print(f"Trace 输入形状: {list(dummy.shape)}")

with torch.no_grad():
    scripted = torch.jit.trace(wrapper, dummy)

# 3. 验证
with torch.no_grad():
    logits = scripted(dummy)
print(f"输出形状: {list(logits.shape)}")   # 应为 [1, 10]
probs = torch.softmax(logits, dim=-1)
print("各类概率:", probs.squeeze().tolist())

# 4. 保存
scripted.save(OUTPUT_PT)
print(f"\n已保存到 {OUTPUT_PT}")

LABELS = ["disco", "metal", "reggae", "blues", "rock",
          "classical", "jazz", "hiphop", "country", "pop"]
print("\n标签映射 (id -> label):")
for i, lbl in enumerate(LABELS):
    print(f"  {i}: {lbl}")
```

### 关键说明

- **为什么要 zero-mean/unit-var 归一化**：`preprocessor_config.json` 中 `do_normalize: true`，训练时 `Wav2Vec2FeatureExtractor` 会对原始波形做归一化，推理时必须手动复现，否则精度严重下降。
- **为什么用 trace 而不是 script**：`Wav2Vec2` 模型内部含有条件分支，`torch.jit.script` 会报错，`trace` 使用固定形状虚拟输入绕过此限制。
- **输入长度**：C# 固定截取前 30 秒（`MaxSeconds = 30`），不足则补零。

### 输出 CSV 格式

文件名：`{音乐目录名}_genre.csv`（如 `1-5000_genre.csv`）

```
目录路径, 文件名, 流派1, 概率1, 流派2, 概率2, 流派3, 概率3
```

---

## 模型二：通用音频场景识别 (AudioClassifier)

### 基本信息

| 项目 | 内容 |
|------|------|
| **模型名称** | MIT/ast-finetuned-audioset-10-10-0.4593 |
| **HuggingFace 地址** | https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593 |
| **论文** | [AST: Audio Spectrogram Transformer](https://arxiv.org/abs/2104.01778) |
| **基础架构** | `ASTForAudioClassification`（Vision Transformer 适配音频） |
| **任务** | 多标签音频分类（527 类 AudioSet） |
| **输入采样率** | 16000 Hz |
| **输入特征** | 128 维梅尔频谱图（fbank），最多 1024 帧（约 10 秒） |
| **本地路径** | `F:\ast-finetuned-audioset-10-10-0.4593` |
| **导出后路径** | `F:\ast_fbank.pt`（预处理）+ `F:\ast_transformer.pt`（推理） |
| **C# 类文件** | `ConsoleApp1/AudioClassifier.cs` |

### 为什么拆成两个模型文件

`torchaudio.compliance.kaldi.fbank` 内部使用 `torch.hann_window`，该操作在 trace 时将设备硬编码为 CPU。若将整个流程（预处理 + Transformer）打包成一个模型并尝试在 CUDA 上推理，会报：

```
RuntimeError: Expected all tensors to be on the same device,
but found at least two devices, cuda:0 and cpu!
```

**解决方案**：拆分为两个 TorchScript 模型：

| 文件 | 设备 | 输入 | 输出 |
|------|------|------|------|
| `ast_fbank.pt` | CPU | 波形 `[1, T]` | fbank `[1, 1024, 128]` |
| `ast_transformer.pt` | CUDA | fbank `[1, 1024, 128]` | logits `[1, 527]` |

### 推理流程

```
音频文件
  ↓ NAudio 重采样 → 单声道 16 kHz PCM float32（无需归一化）
  ↓ 截断/padding → [1, 160000]（10秒）
  ↓ [CPU] ast_fbank.pt
      kaldi.fbank → [T', 128]
      AudioSet 均值/方差归一化（mean=-4.2677, std=4.5690×2）
      padding/truncate → [1, 1024, 128]
  ↓ .cuda()
  ↓ [GPU] ast_transformer.pt → logits [1, 527]
  ↓ .cpu() → sigmoid（多标签独立概率）
  → 所有概率 ≥ 0.3 的标签（按概率降序）
```

### 为什么用 sigmoid 而不是 softmax

AudioSet 是**多标签数据集**，一段音频可同时属于多个类别（如"音乐 + 长笛 + 歌曲"）。

- `softmax`：强制所有类概率之和为 1，类别互斥，适合单标签任务
- `sigmoid`：每个类独立输出 0~1 的概率，适合多标签任务

本工程使用 `sigmoid`，阈值 `0.3f`，所有超过阈值的类均输出。

### 分类范围（527 类，部分示例）

| 类别组 | 包含标签示例 |
|--------|-------------|
| 人声/语音 | 语音、男性语音、女性语音、演唱、说唱、耳语、尖叫 |
| 音乐类型 | 流行、嘻哈、爵士、古典、摇滚、电子舞曲、雷鬼 |
| 乐器 | 钢琴、吉他、小提琴、长笛、萨克斯、合成器 |
| 环境音 | 风声、雨声、海浪、鸟鸣、交通噪声 |
| 生活音效 | 门铃、键盘、打字、警报、电话铃 |
| 场景 | 室内小空间、室内大厅、城市户外、乡村户外 |

完整 527 类中文映射见 `AudioClassifier.cs` 中的 `WatchedLabels` 数组。

### Python 导出脚本

文件：`export_ast_model.py`

```python
"""
导出 MIT/ast-finetuned-audioset-10-10-0.4593 为两个 TorchScript (.pt)：
  1. F:\ast_fbank.pt       — fbank 预处理（CPU，波形 → 梅尔频谱图）
  2. F:\ast_transformer.pt — AST Transformer 推理（支持 CUDA）

C# 侧：fbank 在 CPU 运算，频谱图送 CUDA 做推理，最后 logits 取回 CPU。

依赖:
    pip install torch torchaudio transformers

运行:
    python export_ast_model.py
"""

import torch
import torchaudio.compliance.kaldi as kaldi
from transformers import ASTForAudioClassification

MODEL_DIR          = r"F:\ast-finetuned-audioset-10-10-0.4593"
OUTPUT_FBANK       = r"F:\ast_fbank.pt"
OUTPUT_TRANSFORMER = r"F:\ast_transformer.pt"
SAMPLE_RATE        = 16000
DUMMY_SECONDS      = 10
MAX_FRAMES         = 1024   # AST 固定时间帧数
NUM_MEL_BINS       = 128


class FbankWrapper(torch.nn.Module):
    """
    fbank 预处理（纯 CPU）。
    输入:  waveform [1, T] float32, 16 kHz 原始 PCM
    输出:  fbank    [1, 1024, 128] float32
    """
    def forward(self, waveform: torch.Tensor) -> torch.Tensor:
        fbank = kaldi.fbank(
            waveform,
            htk_compat=True,
            sample_frequency=SAMPLE_RATE,
            use_energy=False,
            window_type="hanning",
            num_mel_bins=NUM_MEL_BINS,
            dither=0.0,
            frame_shift=10,
        )   # [T', 128]

        # AudioSet 训练统计值归一化（mean=-4.2677393, std=4.5689974×2）
        fbank = (fbank - (-4.2677393)) / (4.5689974 * 2)

        # padding / truncate → [MAX_FRAMES, 128]
        T = fbank.shape[0]
        if T < MAX_FRAMES:
            pad = torch.zeros(MAX_FRAMES - T, NUM_MEL_BINS, dtype=fbank.dtype)
            fbank = torch.cat([fbank, pad], dim=0)
        else:
            fbank = fbank[:MAX_FRAMES, :]

        return fbank.unsqueeze(0)   # [1, 1024, 128]


class TransformerWrapper(torch.nn.Module):
    """
    AST Transformer 推理（可运行在 CUDA）。
    输入:  fbank  [1, 1024, 128] float32
    输出:  logits [1, 527]  float32
    """
    def __init__(self, inner: ASTForAudioClassification):
        super().__init__()
        self.inner = inner

    def forward(self, fbank: torch.Tensor) -> torch.Tensor:
        return self.inner(input_values=fbank).logits


# ── 导出 fbank 预处理模型 ────────────────────────────────────────────────
print("导出 fbank 预处理模型 (CPU)...")
fbank_wrapper = FbankWrapper()
fbank_wrapper.eval()
dummy_wave = torch.zeros(1, SAMPLE_RATE * DUMMY_SECONDS)

with torch.no_grad():
    scripted_fbank = torch.jit.trace(fbank_wrapper, dummy_wave)
    out_fbank = scripted_fbank(dummy_wave)

print(f"  fbank 输出形状: {list(out_fbank.shape)}")   # [1, 1024, 128]
scripted_fbank.save(OUTPUT_FBANK)
print(f"  已保存到 {OUTPUT_FBANK}")

# ── 导出 AST Transformer ─────────────────────────────────────────────────
print("\n导出 AST Transformer 模型...")
model = ASTForAudioClassification.from_pretrained(MODEL_DIR)
model.eval()

trans_wrapper = TransformerWrapper(model)
trans_wrapper.eval()

with torch.no_grad():
    scripted_trans = torch.jit.trace(trans_wrapper, out_fbank)
    out_logits = scripted_trans(out_fbank)

print(f"  logits 输出形状: {list(out_logits.shape)}")  # [1, 527]
scripted_trans.save(OUTPUT_TRANSFORMER)
print(f"  已保存到 {OUTPUT_TRANSFORMER}")

print("\n全部完成。")
```

### 关键说明

- **归一化统计值**：`mean = -4.2677393`，`std = 4.5689974 × 2` 是 MIT 在 AudioSet 数据集上计算的全局统计值，与 HuggingFace `ASTFeatureExtractor` 默认值一致，推理时必须使用相同值。
- **frame_shift=10**：每帧步长 10ms，10 秒音频约产生 998 帧，padding 到 1024。
- **输入无需归一化**：与 Wav2Vec2 不同，AST 的输入是梅尔频谱图，C# 侧加载原始 PCM 即可，归一化在 `ast_fbank.pt` 内部完成。

### 输出 CSV 格式

文件名：`{音乐目录名}_speech_music.csv`（如 `1-5000_speech_music.csv`）

```
目录路径, 文件名, 命中标签(阈值>0.3)
```

标签列格式示例：
```
"G:\歌曲宝\...\1041_陈楚生_远山如昨","audio.mp3","音乐(Music)(0.82) | 长笛(Flute)(0.51) | 歌曲(Song)(0.45) | 声乐(Vocal music)(0.38)"
```

---

## 模型三：音频嵌入提取 (MertClassifier)

### 基本信息

| 项目 | 内容 |
|------|------|
| **模型名称** | m-a-p/MERT-v1-95M |
| **HuggingFace 地址** | https://huggingface.co/m-a-p/MERT-v1-95M |
| **基础架构** | HuBERT 变体（Music-Enhanced Representation Transformer） |
| **任务** | 音频嵌入提取（Encoder-only，无分类头） |
| **输入采样率** | 24000 Hz |
| **本地路径** | `F:\MERT_v1_95M`（目录名不能含连字符，否则 TorchScript 序列化失败） |
| **导出后路径** | `F:\mert_encoder.pt`（编码器）+ `F:\mert_mean_pool.pt`（均值池化） |
| **C# 类文件** | `ConsoleApp1/MertClassifier.cs` |

### 输出格式

| 层级 | 形状 | 含义 |
|------|------|------|
| `last_hidden_state` | `[1, seq, 768]` | 每帧上下文表示（约每 20ms 一帧） |
| 均值池化后 | `[1, 768]` | 整段音频的全局语义向量（embedding） |

### 嵌入用途

| 用途 | 说明 |
|------|------|
| 相似度检索 | 余弦距离找"听起来像"的歌曲 |
| 下游分类 | 在 embedding 上接线性层，用少量标注数据训练 |
| 聚类分析 | K-Means / UMAP 自动发现风格簇 |

### 推理流程

```
音频文件
  ↓ NAudio 重采样 → 单声道 24 kHz PCM float32
  ↓ zero-mean / unit-var 归一化（对应 do_normalize=true）
  ↓ 截断/padding → 固定长度 [1, 240000]（10秒）
  ↓ device=CUDA 或 CPU（自动检测）
  ↓ mert_encoder.pt → last_hidden_state [1, seq, 768]
  ↓ mert_mean_pool.pt → pooled [1, 768]
  → float[768] 嵌入向量，写入 CSV
```

### 设备兼容性

模型在 **CUDA 上 trace 导出**，TorchSharp 加载时通过 `map_location` 自动重映射：

```csharp
// 自动选择设备，一个 .pt 文件同时兼容 CPU 和 CUDA
static readonly DeviceType Device = cuda.is_available() ? DeviceType.CUDA : DeviceType.CPU;
jit.load(EncoderPath, Device);
```

> **为什么必须在 CUDA 上 trace**：MERT 内部 attention mask 处理逻辑会在运行时动态创建张量，若在 CPU 进程中 trace，HuggingFace 模块缓存（`~/.cache/huggingface/modules/`）里固化的常量张量仍指向 `cuda:0`，导致 `Expected all tensors to be on the same device` 错误，无法绕过。

### Python 导出脚本

文件：`export_mert_model.py`

```python
"""
在 CUDA 上 trace 并导出 MERT-v1-95M 为 TorchScript (.pt)。
模型、虚拟输入全部在 CUDA 上，彻底消除设备不一致问题。

注意：模型目录名不能含连字符（-），否则 TorchScript 序列化路径非法。
      请使用 F:\MERT_v1_95M（下划线），不要用 F:\MERT-v1-95M。

依赖:
    pip install torch transformers

运行:
    python export_mert_model.py
"""
import os
import torch
from transformers import AutoModel

MODEL_DIR   = r"F:\MERT_v1_95M"
OUT_ENCODER = r"F:\mert_encoder.pt"
OUT_POOL    = r"F:\mert_mean_pool.pt"
SAMPLE_RATE = 24000
SEQ_LEN     = SAMPLE_RATE * 5   # 5 秒用于 trace
DEVICE      = "cuda"

class MertEncoderWrapper(torch.nn.Module):
    def __init__(self, base_model):
        super().__init__()
        self.model = base_model
    def forward(self, input_values: torch.Tensor, attention_mask: torch.Tensor) -> torch.Tensor:
        return self.model(input_values=input_values, attention_mask=attention_mask).last_hidden_state

class MeanPoolWrapper(torch.nn.Module):
    def forward(self, hidden: torch.Tensor) -> torch.Tensor:
        return hidden.mean(dim=1)

model = AutoModel.from_pretrained(MODEL_DIR, trust_remote_code=True, torch_dtype=torch.float32)
model.eval().to(DEVICE)

encoder_wrapper = MertEncoderWrapper(model).eval().to(DEVICE)
dummy_input = torch.zeros(1, SEQ_LEN, dtype=torch.float32, device=DEVICE)
dummy_mask  = torch.ones (1, SEQ_LEN, dtype=torch.int64,   device=DEVICE)
with torch.no_grad():
    traced_encoder = torch.jit.trace(encoder_wrapper, (dummy_input, dummy_mask), strict=False)
traced_encoder.save(OUT_ENCODER)

pool_wrapper = MeanPoolWrapper().eval().to(DEVICE)
with torch.no_grad():
    dummy_hidden = torch.zeros(1, 149, 768, device=DEVICE)
    traced_pool  = torch.jit.trace(pool_wrapper, dummy_hidden)
traced_pool.save(OUT_POOL)
```

### C# 推理代码

文件：`ConsoleApp1/MertClassifier.cs`

```csharp
using NAudio.Wave;
using TorchSharp;
using static TorchSharp.torch;

static class MertClassifier
{
    const string EncoderPath = @"F:\mert_encoder.pt";
    const string PoolPath    = @"F:\mert_mean_pool.pt";
    const string MusicFolder = @"G:\歌曲宝\1-100000\1-5000";
    static string OutputCsv  => Path.Combine(@"F:\", new DirectoryInfo(MusicFolder).Name + "_mert_emb.csv");
    const int SampleRate     = 24000;  // MERT-v1-95M 固定 24 kHz
    const int MaxSeconds     = 10;

    // 自动选择设备：有 CUDA 就用 GPU，否则退回 CPU
    // 导出的是 CUDA trace 模型，TorchSharp 通过 map_location 自动重映射
    static readonly DeviceType Device = cuda.is_available() ? DeviceType.CUDA : DeviceType.CPU;

    static readonly string[] AudioExts =
        [".mp3", ".flac", ".wav", ".m4a", ".ogg", ".aac", ".wma"];

    public static void Run()
    {
        Console.WriteLine($"加载 MERT 编码器 (device={Device})...");
        using var encoder = jit.load(EncoderPath, Device);
        using var pooler  = jit.load(PoolPath,    Device);
        encoder.eval();
        pooler.eval();

        var files = Directory
            .EnumerateFiles(MusicFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        Console.WriteLine($"找到 {files.Count} 个音频文件，开始提取嵌入...\n");

        using var csv = new StreamWriter(OutputCsv, false, System.Text.Encoding.UTF8);
        csv.WriteLine("目录名,文件名," + string.Join(",", Enumerable.Range(0, 768).Select(i => $"emb_{i}")));

        int done = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                float[] emb  = GetEmbedding(file, encoder, pooler);
                string  dir  = Path.GetDirectoryName(file) ?? "";
                string  name = Path.GetFileName(file);
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

    public static float[] GetEmbedding(string audioPath, jit.ScriptModule encoder, jit.ScriptModule pooler)
    {
        float[] samples  = LoadAndNormalize(audioPath);
        int     fixedLen = SampleRate * MaxSeconds;
        float[] buf      = new float[fixedLen];
        Array.Copy(samples, buf, Math.Min(samples.Length, fixedLen));

        using var _ = no_grad();
        using var inputValues   = torch.tensor(buf).unsqueeze(0).to(Device);
        using var attentionMask = torch.ones(1, fixedLen, dtype: ScalarType.Int64).to(Device);

        // 编码器 → [1, seq, 768]，池化 → [1, 768]，取回 CPU
        using var hidden = (Tensor)encoder.forward(inputValues, attentionMask);
        using var pooled = ((Tensor)pooler.forward(hidden)).cpu();
        using var flat   = pooled.squeeze(0);  // [768]

        float[] result = new float[768];
        for (int i = 0; i < 768; i++)
            result[i] = flat[i].item<float>();
        return result;
    }

    // 加载音频 → 单声道 24 kHz → zero-mean / unit-variance 归一化
    static float[] LoadAndNormalize(string path)
    {
        using var reader = new AudioFileReader(path);
        var outFmt       = new WaveFormat(SampleRate, 16, 1);
        using var resamp = new MediaFoundationResampler(reader, outFmt) { ResamplerQuality = 60 };
        var provider     = resamp.ToSampleProvider();

        var chunk = new float[4096];
        var list  = new List<float>();
        int n;
        while ((n = provider.Read(chunk, 0, chunk.Length)) > 0)
            list.AddRange(chunk[..n]);

        float[] data = [.. list];

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
}
```

### 关键说明

- **目录名不能含连字符**：TorchScript 将模型目录名嵌入序列化文件作为 Python 模块路径，`MERT-v1-95M` 中的 `-` 是非法 Python 标识符，TorchSharp 反序列化时报 `expected newline but found 'ident'`。必须使用 `MERT_v1_95M`。
- **采样率 24 kHz**：MERT 与大多数 Wav2Vec2/HuBERT 模型不同，使用 24 kHz 而非 16 kHz，C# 侧重采样目标必须是 24000。
- **输入归一化**：与 `Wav2Vec2FeatureExtractor(do_normalize=true)` 一致，需做 zero-mean/unit-variance 归一化。

### 输出 CSV 格式

文件名：`{音乐目录名}_mert_emb.csv`（如 `1-5000_mert_emb.csv`）

```
目录路径, 文件名, emb_0, emb_1, ..., emb_767
```

---

## 数据流总览

```
音频文件 (.mp3/.flac/.wav/.m4a/.ogg/.aac/.wma)
│
├─► [GenreClassifier] ──────────────────────────────────────────────
│     NAudio → 16kHz 单声道 → zero-mean/unit-var 归一化
│     → padding/truncate [1, 480000]
│     → CUDA: genre_classifier.pt (Wav2Vec2)
│     → softmax [1,10] → Top-3 流派
│     → 写入 {dir}_genre.csv
│
├─► [AudioClassifier] ──────────────────────────────────────────────
│     NAudio → 16kHz 单声道（无需归一化）
│     → padding/truncate [1, 160000]
│     → CPU: ast_fbank.pt → fbank [1, 1024, 128]
│     → CUDA: ast_transformer.pt → logits [1, 527]
│     → sigmoid → 所有概率≥0.3 的标签
│     → 写入 {dir}_speech_music.csv
│
└─► [MertClassifier] ───────────────────────────────────────────────
      NAudio → 24kHz 单声道 → zero-mean/unit-var 归一化
      → padding/truncate [1, 240000]
      → CUDA/CPU (自动): mert_encoder.pt → last_hidden_state [1, seq, 768]
      → CUDA/CPU (自动): mert_mean_pool.pt → pooled [1, 768]
      → float[768] 嵌入向量
      → 写入 {dir}_mert_emb.csv
```

---

## C# 项目依赖

项目文件：`ConsoleApp1/ConsoleApp1.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.3.0" />
    <PackageReference Include="TorchSharp-cuda-windows" Version="0.106.0" />
  </ItemGroup>
</Project>
```

### 文件结构

```
ConsoleApp1/
├── ConsoleApp1/
│   ├── Program.cs              # 入口，调用各 Classifier.Run()
│   ├── GenreClassifier.cs      # 模型一：10 类音乐流派分类
│   ├── AudioClassifier.cs      # 模型二：527 类通用音频识别
│   └── MertClassifier.cs       # 模型三：768 维音频嵌入提取
├── export_genre_model.py        # 模型一导出脚本
├── export_ast_model.py          # 模型二导出脚本
└── export_mert_model.py         # 模型三导出脚本
```

### 模型文件路径汇总

| 文件 | 大小（约） | 说明 |
|------|-----------|------|
| `F:\music_genres_classification\` | 360 MB | 模型一 HuggingFace 原始权重 |
| `F:\genre_classifier.pt` | ~360 MB | 模型一 TorchScript |
| `F:\ast-finetuned-audioset-10-10-0.4593\` | ~330 MB | 模型二 HuggingFace 原始权重 |
| `F:\ast_fbank.pt` | ~1 MB | 模型二 fbank 预处理 TorchScript |
| `F:\ast_transformer.pt` | ~330 MB | 模型二 Transformer TorchScript |
| `F:\MERT_v1_95M\` | ~380 MB | 模型三 HuggingFace 原始权重（目录名须用下划线） |
| `F:\mert_encoder.pt` | ~380 MB | 模型三 编码器 TorchScript（CUDA trace） |
| `F:\mert_mean_pool.pt` | <1 MB | 模型三 均值池化 TorchScript |
