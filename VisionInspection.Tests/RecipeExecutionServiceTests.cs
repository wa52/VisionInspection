using OpenCvSharp;
using VisionInspection.Camera;
using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>
/// 方案执行编排服务（应用层）：单次/连续运行、标脏重建、热应用参数、设备委托注入。
/// 相机能力用假委托注入；发波场景用 NodeFactory 注册的假 NG 节点驱动（对齐 CameraIoNodeTests 范式）。
/// </summary>
public class RecipeExecutionServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "svi_exec_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _logs = new();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private string CreateImageDir(int count = 2)
    {
        Directory.CreateDirectory(_tempDir);
        for (var i = 0; i < count; i++)
        {
            using var mat = new Mat(20, 40, MatType.CV_8UC3, new Scalar(i * 40, 0, 0));
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, $"{i}.png"), mat));
        }
        return _tempDir;
    }

    private RecipeExecutionService CreateService(Recipe? recipe, List<IoCommunicationSettings>? ioCalls = null)
    {
        return new RecipeExecutionService(
            () => recipe,
            msg => { lock (_logs) _logs.Add(msg); },
            () => ms => null, // 帧提供器：文件模式图像源不走相机抓帧
            () =>
            {
                if (ioCalls == null) return null;
                return settings => { lock (ioCalls) ioCalls.Add(settings); };
            });
    }

    private static Recipe RecipeWithImageSource(string dir) => new()
    {
        Name = "exec",
        Nodes =
        {
            new RecipeNode
            {
                Name = "01 图像源",
                Type = "ImageSource",
                Enabled = true,
                Params = new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" },
            },
            new RecipeNode
            {
                Name = "02 二值化",
                Type = "Binarize",
                Enabled = true,
                Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["threshold"] = "128" },
            },
        },
    };

    [Fact]
    public async Task RunOnce_BuildsPipeline_EmitsCompleted()
    {
        var dir = CreateImageDir();
        var recipe = RecipeWithImageSource(dir);
        using var svc = CreateService(recipe);
        PipelineResult? got = null;
        svc.Completed += r => { foreach (var m in r.NodeImages.Values) m.Dispose(); got = r; };

        await svc.RunOnceAsync();

        Assert.NotNull(got);
        Assert.NotNull(svc.CurrentPipeline);
        Assert.Equal(2, svc.CurrentPipeline!.Nodes.Count(n => n.Enabled));
    }

    [Fact]
    public async Task RunOnce_NoImageSource_FailsPrepare_NoCompleted()
    {
        var recipe = new Recipe { Name = "empty", Nodes = { new RecipeNode { Name = "01 二值化", Type = "Binarize", Enabled = true } } };
        using var svc = CreateService(recipe);
        var completed = 0;
        svc.Completed += _ => Interlocked.Increment(ref completed);

        await svc.RunOnceAsync();

        Assert.Equal(0, completed);
        Assert.Null(svc.CurrentPipeline);
        lock (_logs)
            Assert.Contains(_logs, m => m.Contains("没有启用的图像源节点"));
    }

    [Fact]
    public async Task Invalidate_ForcesRebuild_NewPipelineInstance()
    {
        var dir = CreateImageDir();
        var recipe = RecipeWithImageSource(dir);
        using var svc = CreateService(recipe);

        await svc.RunOnceAsync();
        var first = svc.CurrentPipeline;
        Assert.NotNull(first);

        svc.Invalidate();
        await svc.RunOnceAsync();
        Assert.NotNull(svc.CurrentPipeline);
        Assert.NotSame(first, svc.CurrentPipeline);
        first!.Dispose();
    }

    [Fact]
    public async Task HotApplyParam_ModelDir_OnModelNode_MarksDirty()
    {
        var dir = CreateImageDir();
        // YoloNode 模型加载失败只记校验日志不阻断建线，适合验证 model_dir 标脏语义
        var recipe = RecipeWithImageSource(dir);
        recipe.Nodes.Add(new RecipeNode
        {
            Name = "03 YOLO",
            Type = "YOLO",
            Enabled = true,
            Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["model_dir"] = "" },
        });
        using var svc = CreateService(recipe);

        await svc.RunOnceAsync();
        var first = svc.CurrentPipeline;
        Assert.NotNull(first);

        svc.HotApplyParam("03 YOLO", "model_dir", "D:\\somewhere");
        await svc.RunOnceAsync(); // 标脏后下一轮执行触发重建
        Assert.NotSame(first, svc.CurrentPipeline);
        first!.Dispose();
    }

    [Fact]
    public async Task HotApplyParam_Threshold_AppliesDirectly_WithoutRebuild()
    {
        var dir = CreateImageDir();
        var recipe = RecipeWithImageSource(dir);
        using var svc = CreateService(recipe);

        await svc.RunOnceAsync();
        var first = svc.CurrentPipeline;

        svc.HotApplyParam("02 二值化", "threshold", "64");
        Assert.Same(first, svc.CurrentPipeline); // 非模型目录参数：热更不重建
        Assert.Equal("64", first!.Nodes.First(n => n.Name == "02 二值化").Params.GetValueOrDefault("threshold"));
    }

    [Fact]
    public async Task SetNodeEnabled_TogglesRuntimeNode()
    {
        var dir = CreateImageDir();
        var recipe = RecipeWithImageSource(dir);
        using var svc = CreateService(recipe);

        await svc.RunOnceAsync();
        svc.SetNodeEnabled("02 二值化", false);
        var node = svc.CurrentPipeline!.Nodes.First(n => n.Name == "02 二值化");
        Assert.False(node.Enabled);
    }

    [Fact]
    public async Task ToggleContinuous_StartAndStop_RunsAndStops()
    {
        var dir = CreateImageDir();
        var recipe = RecipeWithImageSource(dir);
        using var svc = CreateService(recipe);
        var completed = 0;
        svc.Completed += r => { foreach (var m in r.NodeImages.Values) m.Dispose(); Interlocked.Increment(ref completed); };

        await svc.ToggleContinuousAsync();
        Assert.True(svc.IsRunning);
        Assert.True(svc.IsContinuous);

        var done = Task.Run(async () =>
        {
            while (Volatile.Read(ref completed) < 3) await Task.Delay(10);
        });
        await Task.WhenAny(done, Task.Delay(15000));

        await svc.StopAsync();
        Assert.False(svc.IsRunning);
        Assert.True(Volatile.Read(ref completed) >= 3, "15s 内未完成 3 轮连续执行");
    }

    [Fact]
    public async Task CameraIoFactory_PassedThrough_EmitsOnNg()
    {
        NodeFactory.Register("FakeNgForExecSvc", (name, _) => new FakeNgNode(name));
        var dir = CreateImageDir();
        var recipe = new Recipe
        {
            Name = "io",
            Nodes =
            {
                new RecipeNode
                {
                    Name = "00 图像源",
                    Type = "ImageSource",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["dir"] = dir, ["loop"] = "true" },
                },
                new RecipeNode { Name = "01 检测", Type = "FakeNgForExecSvc", Enabled = true },
                new RecipeNode
                {
                    Name = "02 条件检测",
                    Type = "Decision",
                    Enabled = true,
                    Rules =
                    [
                        new DecisionRule
                        {
                            MatchMode = "all",
                            Conditions = [new Condition { Node = "01 检测", Field = "decision", Op = "=", Value = "OK" }],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
                new RecipeNode
                {
                    Name = "03 相机IO通信",
                    Type = "CameraIo",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["source"] = "02 条件检测", ["output_when"] = "NG" },
                },
            },
        };
        var ioCalls = new List<IoCommunicationSettings>();
        using var svc = CreateService(recipe, ioCalls);

        await svc.RunOnceAsync();

        Assert.Single(ioCalls); // NG → CameraIo 节点执行 → IO 输出委托被调一次
    }

    /// <summary>恒 NG 假节点（驱动 CameraIo 发波验证）。</summary>
    private sealed class FakeNgNode : IModelNode
    {
        public FakeNgNode(string name) => Name = name;
        public string Name { get; set; } = "";
        public string Type => "FakeNgForExecSvc";
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ParamDef> ParamDefs => Array.Empty<ParamDef>();
        public IReadOnlyDictionary<string, string> Params { get; } = new Dictionary<string, string>();
        public void SetParam(string key, string value) { }
        public NodeResult Run(Mat bgr, PipelineRunContext ctx) => new() { Decision = "NG", Values = { ["decision"] = "NG" } };
        public void Dispose() { }
    }
}
