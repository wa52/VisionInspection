using System.IO;
using System.Numerics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// PatchCore 推理运行时：ONNX 特征提取 + kNN 打分（复现 Python: torch.cdist(p=2) -> topk(k) -> mean -> max）。
/// </summary>
public sealed class PatchCoreRuntime : IPatchCoreRuntime
{
    private readonly InferenceSession _session;
    private readonly float[] _memory; // M * featureDim, row-major
    private readonly int _memoryRows;
    private readonly int _featureDim;
    private readonly int _knn;
    private readonly int _resize;
    private readonly int _imageSize;
    private readonly float[] _memNorm2;

    public PatchCoreRuntime(string modelDir)
    {
        ModelDir = modelDir;
        // 目录与必备文件前置校验：错误信息直接指出缺什么、路径是什么
        if (!Directory.Exists(modelDir))
        {
            throw new DirectoryNotFoundException($"模型目录不存在: {modelDir}");
        }

        var missing = new[] { "backbone_config.json", "model.onnx", "memory_bank.bin" }
            .Where(f => !File.Exists(Path.Combine(modelDir, f)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException($"模型目录缺少文件: {string.Join(", ", missing)}（目录: {modelDir}）");
        }

        var configFile = Path.Combine(modelDir, "backbone_config.json");
        using var cfgDoc = JsonDocument.Parse(File.ReadAllText(configFile));
        var root = cfgDoc.RootElement;
        _knn = root.TryGetProperty("knn", out var knn) ? knn.GetInt32() : 9;
        _featureDim = root.TryGetProperty("feature_dim", out var fd) ? fd.GetInt32() : 0;
        if (root.TryGetProperty("preprocess", out var pre))
        {
            _resize = pre.TryGetProperty("resize", out var r) ? r.GetInt32() : 256;
            _imageSize = pre.TryGetProperty("image_size", out var s) ? s.GetInt32() : 224;
            if (pre.TryGetProperty("tiling_enabled", out var t) && t.GetBoolean())
            {
                throw new InvalidOperationException("tiling 模式暂不支持 C# 部署；请用非分块配置重新训练并导出。");
            }
        }
        else
        {
            _resize = 256;
            _imageSize = 224;
        }

        Threshold = LoadThreshold(modelDir);
        var memoryData = LoadMemory(Path.Combine(modelDir, "memory_bank.bin"));
        if (_featureDim == 0)
        {
            throw new InvalidDataException("backbone_config.json 缺少 feature_dim，无法确定 memory bank 行数");
        }

        if (memoryData.Length % _featureDim != 0)
        {
            throw new InvalidDataException($"memory_bank.bin 长度 {memoryData.Length} 不是 feature_dim {_featureDim} 的整数倍");
        }

        _memory = memoryData;
        _memoryRows = memoryData.Length / _featureDim;

        _memNorm2 = new float[_memoryRows];
        for (var i = 0; i < _memoryRows; i++)
        {
            _memNorm2[i] = Norm2(_memory, i * _featureDim, _featureDim);
        }

        var onnxPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(onnxPath))
        {
            throw new FileNotFoundException("未找到 ONNX 模型文件。请先运行训练工具 export 子命令。", onnxPath);
        }

        _session = new InferenceSession(onnxPath, CreateSessionOptions());
        var outDims = _session.OutputMetadata.First().Value.Dimensions.ToArray();
        FeatureH = outDims.Length >= 2 && outDims[^2] > 0 ? outDims[^2] : 14;
        FeatureW = outDims.Length >= 1 && outDims[^1] > 0 ? outDims[^1] : 14;
        Summary =
            $"backbone={ReadBackbone(root)} knn={_knn} memory={_memoryRows} feature_dim={_featureDim} " +
            $"threshold={Threshold?.ToString("F4") ?? "?"} feature_grid={FeatureH}x{FeatureW}";
    }

    public string ModelDir { get; }
    public double? Threshold { get; }
    public int FeatureH { get; }
    public int FeatureW { get; }
    public string Summary { get; }

    public int InputSize => _imageSize;

    private static string ReadBackbone(JsonElement root) =>
        root.TryGetProperty("backbone", out var b) ? b.GetString() ?? "?" : "?";

    private static double? LoadThreshold(string modelDir)
    {
        var path = Path.Combine(modelDir, "threshold.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        // threshold 为 null（训练后未跑评估标定）或缺失 → 视为无阈值，由节点手动阈值参数兜底
        if (!doc.RootElement.TryGetProperty("threshold", out var t) || t.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        return t.GetDouble();
    }

    private static float[] LoadMemory(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"未找到 memory bank: {path}");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length % 4 != 0)
        {
            throw new InvalidDataException($"memory_bank.bin 长度非法: {bytes.Length}");
        }

        var data = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        return data;
    }

    private readonly object _detectLock = new();

    /// <summary>
    /// 会话线程配置：限制单模型推理线程数，避免多模型并行时互相抢核导致负优化。
    /// 默认每模型 2 线程；若本机核心很少（≤4）则降到 1，避免过度争抢。
    /// </summary>
    private static SessionOptions CreateSessionOptions()
    {
        var so = new SessionOptions();
        var cores = Math.Max(1, Environment.ProcessorCount);
        so.IntraOpNumThreads = cores <= 4 ? 1 : 2;
        return so;
    }

    /// <summary>检测单张 BGR 图，返回 (imageScore, patchScoreMap 2D, decision)。线程安全：内部串行化推理。</summary>
    public (double Score, float[,] PatchMap, string Decision) Detect(Mat bgr)
    {
        lock (_detectLock)
        {
            var tensor = ImagePreprocessService.Prepare(bgr, _resize, _imageSize);
            var input = NamedOnnxValue.CreateFromTensor("input", tensor);
            using var results = _session.Run([input]);
            var feats = results.First().AsTensor<float>();
            var (score, patchMap) = ScoreFeatures(feats);
            var decision = Threshold is not null && score > Threshold ? "NG" : "OK";
            return (score, patchMap, decision);
        }
    }

    private (double Score, float[,] PatchMap) ScoreFeatures(Tensor<float> feats)
    {
        var dims = feats.Dimensions.ToArray();
        var c = dims[^3];
        var h = dims[^2];
        var w = dims[^1];
        var n = h * w;
        var featureVec = new float[n * c];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var ch = 0; ch < c; ch++)
                {
                    featureVec[(y * w + x) * c + ch] = feats[0, ch, y, x];
                }
            }
        }

        var featNorm = new float[n];
        for (var i = 0; i < n; i++)
        {
            featNorm[i] = Norm2(featureVec, i * c, c);
        }

        // 并行 SIMD 矩阵乘（196x1536 · 1536x9173）算 dot = F·Mᵀ，避免 OpenCV gemm 单线程瓶颈
        var prodArr = new float[n * _memoryRows];
        Parallel.For(0, n, i =>
        {
            var fBase = i * c;
            var rowBase = i * _memoryRows;
            var vecWidth = Vector<float>.Count;
            for (var j = 0; j < _memoryRows; j++)
            {
                var mBase = j * c;
                var acc = Vector<float>.Zero;
                var d = 0;
                for (; d + vecWidth <= c; d += vecWidth)
                {
                    acc += new Vector<float>(featureVec, fBase + d) * new Vector<float>(_memory, mBase + d);
                }

                var dot = Vector.Dot(acc, Vector<float>.One);
                for (; d < c; d++)
                {
                    dot += featureVec[fBase + d] * _memory[mBase + d];
                }

                prodArr[rowBase + j] = dot;
            }
        });

        var patchScores = new float[n];
        var patchScores2D = new float[h, w];
        var k = Math.Min(_knn, _memoryRows);
        var kth = new float[k];

        // 逐行 topk 最小距离 -> mean
        for (var i = 0; i < n; i++)
        {
            var rowBase = i * _memoryRows;
            var fn = featNorm[i];
            FillKSmallest(kth, _memoryRows, (j) =>
            {
                var dist2 = fn + _memNorm2[j] - 2.0f * prodArr[rowBase + j];
                return MathF.Sqrt(dist2 > 0f ? dist2 : 0f);
            });
            var sum = 0f;
            for (var t = 0; t < k; t++)
            {
                sum += kth[t];
            }

            patchScores[i] = sum / k;
        }

        var max = patchScores[0];
        for (var i = 1; i < n; i++)
        {
            if (patchScores[i] > max)
            {
                max = patchScores[i];
            }
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                patchScores2D[y, x] = patchScores[y * w + x];
            }
        }

        return (max, patchScores2D);
    }

    private static void FillKSmallest(float[] buf, int count, Func<int, float> get)
    {
        var k = buf.Length;
        var filled = 0;
        for (var i = 0; i < count; i++)
        {
            var v = get(i);
            if (filled < k)
            {
                buf[filled++] = v;
                if (filled == k)
                {
                    Array.Sort(buf);
                }

                continue;
            }

            if (v < buf[k - 1])
            {
                // 线性插入到已排序 buf
                var pos = k - 1;
                while (pos > 0 && buf[pos - 1] > v)
                {
                    buf[pos] = buf[pos - 1];
                    pos--;
                }

                buf[pos] = v;
            }
        }
    }

    private static float Norm2(float[] data, int start, int len)
    {
        double sum = 0;
        for (var i = 0; i < len; i++)
        {
            var v = data[start + i];
            sum += v * v;
        }

        return (float)sum;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
