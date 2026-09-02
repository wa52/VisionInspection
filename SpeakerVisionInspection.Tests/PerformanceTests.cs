using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;
using Xunit.Abstractions;

namespace SpeakerVisionInspection.Tests;

/// <summary>
/// 性能基准（不进常规测试集，避免拖慢 dotnet test）：
///   dotnet test --filter "Category=Performance" --logger "console;verbosity=detailed"
/// 覆盖：图像源读图、软件链路、连续执行吞吐、真实 PatchCore 检测、含模型全链。
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests : IDisposable
{
    private static readonly string ModelDir =
        @"D:\AiProjects\speaker-inspection\patchcore train\models\foam_patchcore";

    private static bool ModelExists =>
        File.Exists(Path.Combine(ModelDir, "model.onnx"))
        && File.Exists(Path.Combine(ModelDir, "memory_bank.bin"))
        && File.Exists(Path.Combine(ModelDir, "backbone_config.json"));

    private readonly ITestOutputHelper _out;

    public PerformanceTests(ITestOutputHelper output) => _out = output;

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "svi_perf_" + Guid.NewGuid().ToString("N"));

    /// <summary>生成测试图像集（平滑渐变+圆形，JPEG，接近相机出图的可解码成本）。</summary>
    private string CreateImageSet(string subdir, int width, int height, int count)
    {
        var dir = Path.Combine(_tempDir, subdir);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < count; i++)
        {
            using var mat = new Mat(height, width, MatType.CV_8UC3);
            for (var y = 0; y < height; y += 4)
            {
                using var row = mat.RowRange(y, Math.Min(y + 4, height));
                row.SetTo(new Scalar(y % 256, (i * 37) % 256, (y + i * 13) % 256));
            }
            Cv2.Circle(mat, width / 2, height / 2, Math.Min(width, height) / 4, new Scalar(255, 255, 255), 8);
            Assert.True(Cv2.ImWrite(Path.Combine(dir, $"{i:D3}.jpg"), mat));
        }
        return dir;
    }

    private static void Report(ITestOutputHelper o, string name, int warmup, IReadOnlyList<double> samplesMs)
    {
        var sorted = samplesMs.OrderBy(x => x).ToArray();
        var avg = samplesMs.Average();
        o.WriteLine($"[PERF] {name,-46} n={samplesMs.Count} warmup={warmup} avg={avg:F1}ms min={sorted[0]:F1}ms p50={sorted[sorted.Length / 2]:F1}ms max={sorted[^1]:F1}ms");
    }

    private static List<double> Measure(int warmup, int n, Action body)
    {
        for (var i = 0; i < warmup; i++) body();
        var samples = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            body();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return samples;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    // ===== 1. 图像源读图 =====

    [Theory]
    [InlineData(122, 100)]   // 用户标定小图
    [InlineData(2448, 2048)] // 5MP 相机出图
    public void Perf_ImageSource_Load(int width, int height)
    {
        var dir = CreateImageSet($"src_{width}x{height}", width, height, 3);
        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" });
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);

        var samples = Measure(warmup: 3, n: 30, () =>
        {
            var r = node.Run(input, ctx);
            r.OutputImage?.Dispose();
        });
        Report(_out, $"ImageSource 读图(ImDecode) {width}x{height}", 3, samples);
    }

    // ===== 2. 软件链路节点 =====

    [Fact]
    public void Perf_Binarize_And_Geometry_2448x2048()
    {
        Directory.CreateDirectory(_tempDir);
        using var src = new Mat(2048, 2448, MatType.CV_8UC3, new Scalar(60, 60, 60));

        var bin = new BinarizeNode("二值化", new Dictionary<string, string> { ["source"] = "@input", ["threshold"] = "128" });
        var geo = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input", ["scale"] = "0.5" });
        var geoRot = new GeometryNode("旋转", new Dictionary<string, string> { ["source"] = "@input", ["angle"] = "90" });
        var ctx = new PipelineRunContext(src);

        Report(_out, "Binarize Binary 2448x2048", 2, Measure(2, 20, () =>
        {
            var r = bin.Run(src, ctx);
            r.OutputImage?.Dispose();
        }));
        Report(_out, "Geometry resize x0.5 2448x2048", 2, Measure(2, 20, () =>
        {
            var r = geo.Run(src, ctx);
            r.OutputImage?.Dispose();
        }));
        Report(_out, "Geometry rotate90 expand 2448x2048", 2, Measure(2, 20, () =>
        {
            var r = geoRot.Run(src, ctx);
            r.OutputImage?.Dispose();
        }));
    }

    // ===== 3. 整条软件流水线 =====

    [Fact]
    public void Perf_Pipeline_SoftwareChain_2448x2048()
    {
        var dir = CreateImageSet("chain", 2448, 2048, 3);
        var recipe = new Recipe
        {
            Name = "perf",
            Nodes =
            {
                new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" } },
                new RecipeNode { Name = "02 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["threshold"] = "128" } },
                new RecipeNode { Name = "03 缩放", Type = "Geometry", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "02 二值化", ["scale"] = "0.5" } },
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var input = new Mat();

        var samples = Measure(warmup: 3, n: 30, () =>
        {
            var r = pipeline.Run(input, "perf");
            foreach (var m in r.NodeImages.Values) m.Dispose();
        });
        Report(_out, "Pipeline 软件链 图像源→二值化→缩放 (2448x2048)", 3, samples);
    }

    // ===== 4. 连续执行吞吐（PipelineRunner） =====

    [Fact]
    public async Task Perf_Runner_Continuous_Throughput_5s()
    {
        var dir = CreateImageSet("runner", 122, 100, 3);
        var recipe = new Recipe
        {
            Name = "perf",
            Nodes =
            {
                new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" } },
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var runner = new PipelineRunner();
        var rounds = 0;
        runner.Completed += r => { foreach (var m in r.NodeImages.Values) m.Dispose(); Interlocked.Increment(ref rounds); };

        runner.StartContinuous(pipeline, () => "perf");
        await Task.Delay(5000);
        await runner.StopAsync();

        _out.WriteLine($"[PERF] Runner 连续执行 5s 吞吐（小图 122x100）           rounds={rounds} avg={5000.0 / Math.Max(1, rounds):F1}ms/轮");
        Assert.True(rounds > 50, $"连续执行 5 秒仅 {rounds} 轮，吞吐异常");
    }

    // ===== 5. 真实 PatchCore 模型 =====

    [Fact]
    public void Perf_PatchCore_Detect_RealModel()
    {
        if (!ModelExists) return;

        using var rt = new PatchCoreRuntime(ModelDir);
        using var img = new Mat(2048, 2448, MatType.CV_8UC3, new Scalar(90, 90, 90));
        using var small = new Mat(300, 300, MatType.CV_8UC3, Scalar.All(128));

        // 预处理固定 short-side 256 → 推理耗时与输入尺寸基本无关；大图成本在 Prepare（resize+归一化）
        var samples300 = Measure(warmup: 2, n: 10, () => rt.Detect(small));
        Report(_out, "PatchCore Detect 300x300 (纯推理)", 2, samples300);

        var samplesPre = Measure(warmup: 2, n: 20, () => _ = ImagePreprocessService.Prepare(img, 256, 224));
        Report(_out, "PatchCore 预处理 2448x2048→224 (resize+归一化)", 2, samplesPre);
    }

    // ===== 6. 含模型全链（最接近生产） =====

    [Fact]
    public void Perf_Pipeline_WithRealPatchCore_2448x2048()
    {
        if (!ModelExists) return;

        var dir = CreateImageSet("full", 2448, 2048, 3);
        var recipe = new Recipe
        {
            Name = "perf",
            Nodes =
            {
                new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" } },
                new RecipeNode { Name = "02 PatchCore", Type = "PatchCore", Enabled = true, Params = new Dictionary<string, string> { ["model_dir"] = ModelDir, ["source"] = "01 图像源" } },
                new RecipeNode { Name = "03 条件检测", Type = "Decision", Enabled = true, Rules =
                [
                    new DecisionRule
                    {
                        MatchMode = "all",
                        Conditions = [new Condition { Node = "02 PatchCore", Field = "decision", Op = "=", Value = "OK" }],
                        Result = "OK",
                        ElseResult = "NG",
                    },
                ] },
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var input = new Mat();

        var samples = Measure(warmup: 2, n: 10, () =>
        {
            var r = pipeline.Run(input, "perf");
            foreach (var m in r.NodeImages.Values) m.Dispose();
        });
        Report(_out, "Pipeline 全链 图像源→PatchCore→条件检测", 2, samples);
    }
}
