using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using VisionInspection.Services;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 功能测试（用户场景级，进常规测试集）：
/// 单次执行全链、连续执行中途停止、旧方案别名读写回环、异常节点语义、缺来源不崩溃。
/// </summary>
public class FunctionalTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "svi_func_" + Guid.NewGuid().ToString("N"));

    public FunctionalTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private string CreateImageDir(int count = 3)
    {
        Directory.CreateDirectory(_tempDir);
        for (var i = 0; i < count; i++)
        {
            using var mat = new Mat(20, 40, MatType.CV_8UC3, new Scalar(i * 40, 0, 0));
            Cv2.Rectangle(mat, new Rect(5 + i, 5, 10, 10), new Scalar(255, 255, 255), -1);
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, $"{i}.png"), mat));
        }
        return _tempDir;
    }

    private static RecipeNode ImageSource(string dir, string name = "01 图像源") => new()
    {
        Name = name,
        Type = "ImageSource",
        Enabled = true,
        Params = new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" },
    };

    [Fact]
    public async Task SingleRun_FullChain_ImageSourceToDisplay()
    {
        var dir = CreateImageDir();
        var recipe = new Recipe
        {
            Name = "func",
            Nodes =
            {
                ImageSource(dir),
                new RecipeNode { Name = "02 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["threshold"] = "128" } },
                new RecipeNode { Name = "03 缩放", Type = "Geometry", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "02 二值化", ["op"] = "resize", ["scale"] = "0.5" } },
                new RecipeNode { Name = "04 显示", Type = "Display", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "03 缩放" } },
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var runner = new PipelineRunner();

        var result = await runner.RunOnceAsync(pipeline, "func");
        try
        {
            Assert.Equal("OK", result.Decision);
            Assert.Null(result.Error);
            Assert.Equal("0.png", result.NodeValues["01 图像源"]["current_file"]);
            Assert.Equal("20", result.NodeValues["01 图像源"]["out_h"]);
            Assert.NotNull(result.DisplayImage);
            Assert.Equal(20, result.DisplayImage!.Width);  // 40x20 缩放 0.5 → 20x10
            Assert.Equal(10, result.DisplayImage!.Height);
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
        _out.WriteLine($"[FUNC] 单次执行全链 OK，节点值: {string.Join(" | ", result.NodeValues.Select(kv => $"{kv.Key}: {string.Join(",", kv.Value)}"))}");
    }

    [Fact]
    public async Task Continuous_StopMidway_ResultsConsistent()
    {
        var dir = CreateImageDir();
        var recipe = new Recipe
        {
            Name = "func",
            Nodes = { ImageSource(dir) },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var runner = new PipelineRunner();
        var decisions = new List<string>();
        var got10 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Completed += r =>
        {
            foreach (var m in r.NodeImages.Values) m.Dispose();
            lock (decisions)
            {
                decisions.Add(r.Decision);
                if (decisions.Count >= 10) got10.TrySetResult();
            }
        };

        runner.StartContinuous(pipeline, () => "func");
        var finished = await Task.WhenAny(got10.Task, Task.Delay(15000));
        await runner.StopAsync();

        Assert.True(finished == got10.Task, "15s 内未收集到 10 轮结果");
        Assert.All(decisions, d => Assert.Equal("OK", d));
        Assert.False(runner.IsRunning);
        _out.WriteLine($"[FUNC] 连续执行收集 {decisions.Count} 轮后停止，判定全部 OK");
    }

    [Fact]
    public void Recipe_ImageLoadAlias_RoundTripPreservesParams()
    {
        var path = Path.Combine(_tempDir, "recipe_alias.json");
        Directory.CreateDirectory(_tempDir);
        var recipe = new Recipe
        {
            Name = "alias",
            BaseDir = _tempDir,
            Nodes =
            {
                new RecipeNode
                {
                    Name = "01 旧图像读取",
                    Type = "ImageLoad", // 旧方案类型名
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["dir"] = _tempDir, ["loop"] = "false" },
                },
            },
        };
        RecipeStore.Save(recipe, path);

        var loaded = RecipeStore.LoadFile(path);
        var node = NodeFactory.FromRecipeNode(loaded.Nodes[0]);

        Assert.IsType<ImageSourceNode>(node);
        Assert.Equal("ImageSource", node.Type);
        Assert.Equal(_tempDir, node.Params["dir"]);
        Assert.Equal("false", node.Params["loop"]);
        node.Dispose();
    }

    [Fact]
    public void Pipeline_UnsupportedNode_YieldsErrorNotCrash()
    {
        var dir = CreateImageDir();
        var recipe = new Recipe
        {
            Name = "func",
            Nodes =
            {
                ImageSource(dir),
                new RecipeNode { Name = "02 YOLO", Type = "YOLO", Enabled = true },
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var input = new Mat();

        var result = pipeline.Run(input, "func");
        try
        {
            Assert.Equal("ERROR", result.Decision); // 任一启用节点异常 → 整线 ERROR，不静默放行
            Assert.Contains("02 YOLO", result.Error);
            Assert.Equal("ERROR", result.NodeValues["02 YOLO"]["decision"]);
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }

    [Fact]
    public void Pipeline_BrokenSourceReference_DoesNotCrash()
    {
        var dir = CreateImageDir();
        var logs = new List<string>();
        var recipe = new Recipe
        {
            Name = "func",
            Nodes =
            {
                ImageSource(dir),
                new RecipeNode { Name = "02 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "不存在的节点", ["threshold"] = "128" } },
            },
        };
        using var pipeline = new Pipeline(recipe, logs.Add);
        Assert.Contains(logs, l => l.Contains("[校验]") && l.Contains("不存在")); // 构建期校验提前提示

        using var input = new Mat();
        var result = pipeline.Run(input, "func");
        try
        {
            Assert.Equal("OK", result.Decision); // 来源缺失节点空输出，不拖垮整线
            Assert.False(result.NodeValues["02 二值化"].ContainsKey("out_w"));
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }
}
