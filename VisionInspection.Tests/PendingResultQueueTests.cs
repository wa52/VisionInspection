using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>执行结果待渲染队列：最新一轮保留、跳帧释放、Mat 容错释放。</summary>
public class PendingResultQueueTests
{
    private static PipelineResult MakeResult(string name, int rows = 4, int cols = 4)
    {
        var r = new PipelineResult { Image = name };
        r.NodeImages[name] = new Mat(rows, cols, MatType.CV_8UC3, Scalar.All(1));
        return r;
    }

    [Fact]
    public void Take_Empty_ReturnsNull()
    {
        var q = new PendingResultQueue();
        Assert.Null(q.Take());
    }

    [Fact]
    public void Publish_ThenTake_RoundTrip()
    {
        var q = new PendingResultQueue();
        var r = MakeResult("a");
        Assert.Null(q.Publish(r)); // 首轮无跳帧
        Assert.Same(r, q.Take());
        Assert.Null(q.Take()); // 取后清空
        r.NodeImages["a"].Dispose();
    }

    [Fact]
    public void Publish_SecondRound_SkipsFirst_AndDisposesItsImages()
    {
        var q = new PendingResultQueue();
        var first = MakeResult("first");
        var firstMat = first.NodeImages["first"]; // 直接持有引用（DisposeSkipped 释放后会清空字典）
        var second = MakeResult("second");

        Assert.Null(q.Publish(first));
        var skipped = q.Publish(second);
        Assert.Same(first, skipped);

        PendingResultQueue.DisposeSkipped(skipped!);
        Assert.True(firstMat.IsDisposed); // 被跳过的结果图已释放
        Assert.Empty(skipped.NodeImages);

        Assert.Same(second, q.Take());
        second.NodeImages["second"].Dispose();
    }

    [Fact]
    public void DisposeSkipped_AlreadyDisposedImage_Tolerated()
    {
        var q = new PendingResultQueue();
        var r = MakeResult("x");
        r.NodeImages["x"].Dispose(); // 模拟渲染侧已释放（共享 Mat 竞态）
        PendingResultQueue.DisposeSkipped(r);
        Assert.Empty(r.NodeImages);
    }
}

/// <summary>节点输出图批量转 WPF 位图：引用去重、共享 Mat 只转一次、转换完统一释放输入。</summary>
public class ConvertNodeImagesTests
{
    [Fact]
    public void SharedMat_ConvertedOnce_AllKeysPresent_InputDisposed()
    {
        using var shared = new Mat(4, 6, MatType.CV_8UC3, Scalar.All(9));
        var nodeImages = new Dictionary<string, Mat>
        {
            ["01 图像源"] = shared,   // 透传：多个键共享同一 Mat 实例
            ["02 二值化"] = shared,
        };

        var thumbs = UiExtensions.ConvertNodeImages(nodeImages);

        Assert.Equal(2, thumbs.Count);
        Assert.NotNull(thumbs["01 图像源"]);
        Assert.Same(thumbs["01 图像源"], thumbs["02 二值化"]); // 去重后同一 BitmapSource
        Assert.True(shared.IsDisposed); // 转换完统一释放
    }

    [Fact]
    public void DistinctMats_EachConverted_AndDisposed()
    {
        var a = new Mat(4, 6, MatType.CV_8UC3, Scalar.All(1));
        var b = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(2)); // 单通道：自动转 BGR
        var nodeImages = new Dictionary<string, Mat> { ["a"] = a, ["b"] = b };

        var thumbs = UiExtensions.ConvertNodeImages(nodeImages);

        Assert.Equal(2, thumbs.Count);
        Assert.NotSame(thumbs["a"], thumbs["b"]);
        Assert.Equal(6, thumbs["a"].PixelWidth);
        Assert.Equal(8, thumbs["b"].PixelWidth);
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
    }

    [Fact]
    public void DisposedInput_Skipped_OthersStillConverted()
    {
        var dead = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));
        var alive = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(2));
        dead.Dispose();
        var nodeImages = new Dictionary<string, Mat> { ["dead"] = dead, ["alive"] = alive };

        var thumbs = UiExtensions.ConvertNodeImages(nodeImages);

        Assert.Single(thumbs);
        Assert.True(thumbs.ContainsKey("alive"));
        Assert.True(alive.IsDisposed);
    }
}
