using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 稳定性测试（长跑/反复启停/内存/停止响应，不进常规测试集）：
///   dotnet test --filter "Category=Stability" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Stability")]
public class StabilityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "svi_stab_" + Guid.NewGuid().ToString("N"));

    public StabilityTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    /// <summary>建 3 张小图循环目录 → 图像源 链。</summary>
    private Pipeline CreateLoopPipeline(Action<string> log, out string[] fileOrder)
    {
        Directory.CreateDirectory(_tempDir);
        fileOrder = new[] { "a.png", "b.png", "c.png" };
        foreach (var (name, idx) in fileOrder.Select((n, i) => (n, i)))
        {
            using var mat = new Mat(2 + idx, 4 + idx, MatType.CV_8UC3, new Scalar(idx, idx, idx));
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, name), mat));
        }

        var recipe = new Recipe
        {
            Name = "stab",
            Nodes =
            {
                new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = _tempDir, ["loop"] = "true" } },
            },
        };
        return new Pipeline(recipe, log);
    }

    [Fact]
    public async Task Continuous_300Rounds_CompletesAndCursorCycles()
    {
        using var pipeline = CreateLoopPipeline(_ => { }, out var fileOrder);
        using var runner = new PipelineRunner();
        var files = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Completed += r =>
        {
            files.Add(r.NodeValues["01 图像源"]["current_file"]);
            foreach (var m in r.NodeImages.Values) m.Dispose();
            if (files.Count >= 300) done.TrySetResult();
        };

        runner.StartContinuous(pipeline, () => "stab");
        var finished = await Task.WhenAny(done.Task, Task.Delay(120_000));
        sw.Stop();
        await runner.StopAsync();

        Assert.True(finished == done.Task, $"300 轮未在 120s 内完成（完成 {files.Count} 轮）");
        for (var i = 0; i < files.Count; i++)
        {
            Assert.Equal(fileOrder[i % fileOrder.Length], files[i]); // 游标循环正确
        }
        Assert.False(runner.IsRunning);
        _out.WriteLine($"[STAB] 连续 300 轮完成，耗时 {sw.Elapsed.TotalSeconds:F1}s（{sw.Elapsed.TotalMilliseconds / 300:F1}ms/轮），游标循环正确");
    }

    [Fact]
    public async Task Repeated_StartStop_20Cycles_NoDeadlock()
    {
        using var pipeline = CreateLoopPipeline(_ => { }, out _);
        using var runner = new PipelineRunner();
        var results = 0;
        runner.Completed += r => { foreach (var m in r.NodeImages.Values) m.Dispose(); Interlocked.Increment(ref results); };

        for (var i = 0; i < 20; i++)
        {
            runner.StartContinuous(pipeline, () => "stab");
            Assert.True(runner.IsRunning, $"第 {i + 1} 轮启动后应为运行中");
            await Task.Delay(150); // 留出运行窗口，立即停止会在首轮完成前取消
            await runner.StopAsync();
            Assert.False(runner.IsRunning, $"第 {i + 1} 轮停止后应已空闲");
        }

        Assert.True(results > 0, "20 次启停应至少产生若干轮结果");
        _out.WriteLine($"[STAB] 20 次启停循环无死锁，共产生 {results} 轮结果");
    }

    [Fact]
    public async Task SingleRun_100Sequential_AllSucceed()
    {
        using var pipeline = CreateLoopPipeline(_ => { }, out _);
        using var runner = new PipelineRunner();
        var samples = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await runner.RunOnceAsync(pipeline, "stab");
            sw.Stop();
            foreach (var m in result.NodeImages.Values) m.Dispose();
            Assert.Equal("OK", result.Decision);
            Assert.Null(result.Error);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        _out.WriteLine($"[STAB] 连续 100 次单次执行全部 OK：avg={samples.Average():F1}ms p50={samples[50]:F1}ms max={samples[^1]:F1}ms");
        Assert.All(samples, ms => Assert.True(ms < 2000, $"单次执行超时: {ms:F0}ms"));
    }

    [Fact]
    public async Task Memory_NoGrowthAfterLongRun()
    {
        // 先跑热身轮让 JIT/缓存稳定
        using var pipeline = CreateLoopPipeline(_ => { }, out _);
        using var runner = new PipelineRunner();
        for (var i = 0; i < 20; i++)
        {
            var r = await runner.RunOnceAsync(pipeline, "warm");
            foreach (var m in r.NodeImages.Values) m.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(true);

        const int rounds = 300;
        for (var i = 0; i < rounds; i++)
        {
            var r = await runner.RunOnceAsync(pipeline, "stab");
            foreach (var m in r.NodeImages.Values) m.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);
        var growthMb = (after - before) / 1024.0 / 1024.0;

        _out.WriteLine($"[STAB] {rounds} 轮后托管内存增长: {growthMb:F1} MB（基线 {before / 1024 / 1024:F0} MB）");
        Assert.True(growthMb < 100, $"{rounds} 轮后托管内存增长 {growthMb:F0}MB，疑似泄漏");
    }

    [Fact]
    public async Task StopAsync_RespondsPromptly_DuringSlowRound()
    {
        // 注册一个 200ms 慢节点（进程级注册表，用独立类型名避免影响其他测试）
        NodeFactory.Register("StabSlowNode", (name, init) => new SlowNode(name));

        var recipe = new Recipe
        {
            Name = "stab",
            Nodes =
            {
                new RecipeNode { Name = "01 慢节点", Type = "StabSlowNode", Enabled = true },
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var runner = new PipelineRunner();

        runner.StartContinuous(pipeline, () => "stab");
        await Task.Delay(100); // 至少进入一轮慢检测
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runner.StopAsync();
        sw.Stop();

        Assert.False(runner.IsRunning);
        // 取消在轮间生效：最长等待=当前轮(200ms)+调度余量
        Assert.True(sw.Elapsed.TotalMilliseconds < 3000, $"StopAsync 响应 {sw.Elapsed.TotalMilliseconds:F0}ms 过慢");
        _out.WriteLine($"[STAB] 慢轮(200ms)中停止响应: {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    private sealed class SlowNode : IModelNode
    {
        public SlowNode(string name) => Name = name;
        public string Name { get; set; }
        public string Type => "StabSlowNode";
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ParamDef> ParamDefs { get; } = Array.Empty<ParamDef>();
        public IReadOnlyDictionary<string, string> Params { get; } = new Dictionary<string, string>();
        public void SetParam(string key, string value) { }
        public NodeResult Run(Mat bgr, PipelineRunContext ctx)
        {
            Thread.Sleep(200);
            return new NodeResult { Decision = "OK" };
        }
        public void Dispose() { }
    }
}
