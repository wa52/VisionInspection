using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>视频源：真实解码、逐帧抽样、落盘、EOF 停止以及重新设置起始帧。</summary>
public sealed class VideoSourceNodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vi_video_" + Guid.NewGuid().ToString("N"));

    private string MakeVideo(int frames = 5)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "sample.avi");
        using var writer = new VideoWriter(path, FourCC.MJPG, 10, new Size(80, 48));
        Assert.True(writer.IsOpened(), "OpenCV 测试环境必须支持 MJPEG/AVI 编码。");
        for (var i = 0; i < frames; i++)
        {
            using var image = new Mat(48, 80, MatType.CV_8UC3, new Scalar(i * 30, 40, 90));
            writer.Write(image);
        }
        writer.Release();
        return path;
    }

    [Fact]
    public void ExtractsOriginalSizeFrames_WithMetadata_AndNoRepeatedLastFrame()
    {
        var video = MakeVideo();
        using var node = new VideoSourceNode("视频源", new Dictionary<string, string>
        {
            ["path"] = video, ["frame_step"] = "2",
            ["save_dir"] = Path.Combine(_dir, "frames"),
        });
        node.EnsureOpened(_dir);
        using var empty = new Mat();
        var ctx = new PipelineRunContext(empty);
        foreach (var expected in new[] { "0", "2", "4" })
        {
            var result = node.Run(empty, ctx);
            Assert.Equal("false", result.Values["eof"]);
            Assert.Equal(expected, result.Values["frame_index"]);
            Assert.Equal("10", result.Values["fps"]);
            Assert.True(result.Values.ContainsKey("timestamp_ms"));
            Assert.Equal(80, result.OutputImage!.Width);
            Assert.Equal(48, result.OutputImage.Height);
            result.OutputImage.Dispose();
        }
        var eos = node.Run(empty, ctx);
        Assert.Equal("true", eos.Values["eof"]);
        Assert.Null(eos.OutputImage);
        Assert.Equal("true", node.Run(empty, ctx).Values["eof"]);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_dir, "frames"), "*.png").Length);
    }

    [Fact]
    public void UpdatingStartFrame_ResetsCapture_WithoutRestartingWholeApp()
    {
        var video = MakeVideo();
        using var node = new VideoSourceNode("视频源",
            new Dictionary<string, string> { ["path"] = video });
        node.EnsureOpened(_dir);
        using var empty = new Mat();
        var ctx = new PipelineRunContext(empty);
        var first = node.Run(empty, ctx);
        Assert.Equal("0", first.Values["frame_index"]);
        first.OutputImage!.Dispose();
        node.SetParam("start_frame", "3");
        node.EnsureOpened(_dir);
        var reset = node.Run(empty, ctx);
        Assert.Equal("3", reset.Values["frame_index"]);
        reset.OutputImage!.Dispose();
        Assert.Throws<InvalidDataException>(() => node.SetParam("frame_step", "0"));
    }

    [Fact]
    public async Task ContinuousPipeline_StopsAtEof_WithoutLoopingOrSendingDownstream()
    {
        var video = MakeVideo();
        var recipe = new Recipe
        {
            BaseDir = _dir,
            Nodes =
            {
                new RecipeNode
                {
                    Name = "视频源", Type = "VideoSource",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = video, ["frame_step"] = "2",
                    },
                },
            },
        };
        using var pipeline = new Pipeline(recipe);
        using var runner = new PipelineRunner();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameIndices = new List<string>();
        var eosCount = 0;
        runner.Completed += result =>
        {
            if (result.EndOfStream) eosCount++;
            else frameIndices.Add(result.NodeValues["视频源"]["frame_index"]);
            foreach (var image in result.NodeImages.Values) image.Dispose();
        };
        runner.StateChanged += running => { if (!running) ended.TrySetResult(); };
        runner.StartContinuous(pipeline, () => "match");
        var completed = await Task.WhenAny(ended.Task, Task.Delay(10000));
        if (completed != ended.Task) await runner.StopAsync();
        Assert.Same(ended.Task, completed);
        Assert.Equal(new[] { "0", "2", "4" }, frameIndices);
        Assert.Equal(1, eosCount);
        Assert.False(runner.IsRunning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
