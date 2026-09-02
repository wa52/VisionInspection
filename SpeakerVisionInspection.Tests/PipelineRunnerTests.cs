using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>PipelineRunner：单次执行与连续执行（图像源=文件模式循环目录，不依赖相机）。</summary>
public class PipelineRunnerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "svi_runner_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private Pipeline CreatePipeline(Action<string> log)
    {
        Directory.CreateDirectory(_tempDir);
        using (var mat = new Mat(2, 4, MatType.CV_8UC3, new Scalar(0, 0, 0)))
        {
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, "a.png"), mat));
        }

        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                new RecipeNode
                {
                    Name = "01 图像源",
                    Type = "ImageSource",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["dir"] = _tempDir, ["loop"] = "true" },
                },
            },
        };
        return new Pipeline(recipe, log);
    }

    [Fact]
    public async Task RunOnce_ExecutesPipelineOnce()
    {
        using var pipeline = CreatePipeline(_ => { });
        using var runner = new PipelineRunner();
        Assert.False(runner.IsRunning);

        var result = await runner.RunOnceAsync(pipeline, "t1");

        Assert.Equal("OK", result.Decision);
        Assert.Equal("a.png", result.NodeValues["01 图像源"]["current_file"]);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task Continuous_RunsRepeatedly_AndStops()
    {
        var logs = new List<string>();
        using var pipeline = CreatePipeline(logs.Add);
        using var runner = new PipelineRunner();
        var count = 0;
        var threeRounds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Completed += _ =>
        {
            if (Interlocked.Increment(ref count) >= 3)
            {
                threeRounds.TrySetResult();
            }
        };

        runner.StartContinuous(pipeline, () => "t");
        var finished = await Task.WhenAny(threeRounds.Task, Task.Delay(15000));
        Assert.True(finished == threeRounds.Task, $"连续执行 15s 内未完成 3 轮（完成 {count} 轮），日志: {string.Join(" | ", logs)}");
        Assert.True(runner.IsRunning);

        await runner.StopAsync();
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task RunOnce_WhileContinuousRunning_Throws()
    {
        using var pipeline = CreatePipeline(_ => { });
        using var runner = new PipelineRunner();
        runner.StartContinuous(pipeline, () => "t");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync(pipeline, "t2"));
        }
        finally
        {
            await runner.StopAsync();
        }
    }

    [Fact]
    public async Task StateChanged_FiresStartAndStop()
    {
        using var pipeline = CreatePipeline(_ => { });
        using var runner = new PipelineRunner();
        var states = new List<bool>();
        runner.StateChanged += states.Add;

        await runner.RunOnceAsync(pipeline, "t");

        Assert.Equal([true, false], states);
    }
}
