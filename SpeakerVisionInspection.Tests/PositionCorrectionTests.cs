using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>位置修正节点：刚体变换数学、ctx 修正注入、ERROR 语义、目标过滤。</summary>
public class PositionCorrectionTests
{
    private static NodeResult RunCorrection(
        Dictionary<string, string>? init, string sourceName, Dictionary<string, string> sourceValues,
        out PipelineRunContext ctx, Mat? input = null)
    {
        using var node = new PositionCorrectionNode("位置修正1", init);
        using var img = input ?? new Mat(100, 100, MatType.CV_8UC3, Scalar.All(50));
        ctx = new PipelineRunContext(img);
        ctx.Results[sourceName] = new NodeResult { Values = { } };
        foreach (var (k, v) in sourceValues) ctx.Results[sourceName].Values[k] = v;
        if (ctx.Results[sourceName].Values.Count == 0)
        {
            ctx.Results.Remove(sourceName);
        }
        var result = node.Run(img, ctx);
        Assert.NotNull(result.OutputImage);
        return result;
    }

    [Fact]
    public void NodeFactory_RegistersPositionCorrection()
    {
        using var node = NodeFactory.Create("PositionCorrection", "07 位置修正");
        Assert.IsType<PositionCorrectionNode>(node);
        Assert.Equal("PositionCorrection", node.Type);
    }

    [Fact]
    public void Run_SetsCorrectionInContext()
    {
        var init = new Dictionary<string, string>
        {
            ["source"] = "定位", ["target_nodes"] = "检测,轮廓",
            ["base_x"] = "400", ["base_y"] = "300", ["base_angle"] = "0",
        };
        var values = new Dictionary<string, string>
        {
            ["loc_x"] = "500", ["loc_y"] = "400", ["loc_angle"] = "10", ["loc_valid"] = "1",
        };
        var result = RunCorrection(init, "定位", values, out var ctx);
        Assert.Equal("OK", result.Decision);

        Assert.NotNull(ctx.PoseCorrection);
        var c = ctx.PoseCorrection!;
        Assert.Equal(400, c.RefX);
        Assert.Equal(300, c.RefY);
        Assert.Equal(500, c.CurX);
        Assert.Equal(400, c.CurY);
        Assert.Equal(10, c.CurAngle);
        Assert.Contains("检测", c.TargetNodes);
        Assert.Equal("100.00", result.Values["dx"]);
        Assert.Equal("100.00", result.Values["dy"]);
        Assert.Equal("10.00", result.Values["dangle"]);
    }

    [Fact]
    public void Run_LocInvalid_Fails()
    {
        var init = new Dictionary<string, string>
        {
            ["source"] = "定位", ["target_nodes"] = "检测",
            ["base_x"] = "400", ["base_y"] = "300", ["base_angle"] = "0",
        };
        var values = new Dictionary<string, string>
        {
            ["loc_x"] = "0", ["loc_y"] = "0", ["loc_angle"] = "0", ["loc_valid"] = "0",
        };
        var result = RunCorrection(init, "定位", values, out var ctx);
        Assert.Equal("ERROR", result.Decision);
        Assert.Null(ctx.PoseCorrection);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void Run_MissingLocKeys_Fails()
    {
        var init = new Dictionary<string, string>
        {
            ["source"] = "定位", ["target_nodes"] = "检测",
            ["base_x"] = "400", ["base_y"] = "300", ["base_angle"] = "0",
        };
        var result = RunCorrection(init, "定位", new Dictionary<string, string> { ["score"] = "0.9" }, out var ctx);
        Assert.Equal("ERROR", result.Decision);
        Assert.Null(ctx.PoseCorrection);
    }

    [Fact]
    public void Run_SourceMissing_Fails()
    {
        var init = new Dictionary<string, string>
        {
            ["source"] = "定位", ["target_nodes"] = "检测",
            ["base_x"] = "400", ["base_y"] = "300", ["base_angle"] = "0",
        };
        var result = RunCorrection(init, "定位", new Dictionary<string, string>(), out _);
        Assert.Equal("ERROR", result.Decision);
    }

    [Fact]
    public void Run_NoBasePose_Passthrough()
    {
        var init = new Dictionary<string, string>
        {
            ["source"] = "定位", ["target_nodes"] = "检测",
        };
        var values = new Dictionary<string, string>
        {
            ["loc_x"] = "500", ["loc_y"] = "400", ["loc_angle"] = "10", ["loc_valid"] = "1",
        };
        var result = RunCorrection(init, "定位", values, out var ctx);
        Assert.Equal("OK", result.Decision);
        Assert.Null(ctx.PoseCorrection);
        Assert.Equal("0", result.Values["base_set"]);
    }

    [Fact]
    public void ApplyPoseCorrection_TranslationOnly()
    {
        // 图 1000×1000：ROI 中心 (500,500)px 角度 0；定位 (400,300)→(500,400)、Δθ=0 → 中心平移 (100,100)
        var rois = new List<(string, RoiRect)> { ("检测项", new RoiRect(0.5, 0.5, 0.2, 0.2, 0)) };
        var correction = new PoseCorrection("定位", ["检测"], 400, 300, 0, 500, 400, 0);
        var mapped = NodeRois.ApplyPoseCorrection(rois, "检测", 1000, 1000, correction);

        var (_, rect) = Assert.Single(mapped);
        Assert.NotSame(rois[0].Item2, rect);
        var (cx, cy, _, _, angle) = rect.ToPixels(1000, 1000);
        // (500,500) + (P1−P0)=(100,100) → (600,600)
        Assert.InRange(cx, 599, 601);
        Assert.InRange(cy, 599, 601);
        Assert.InRange(angle, -0.5, 0.5);
    }

    [Fact]
    public void ApplyPoseCorrection_RotationOnly()
    {
        // 定位点不动 (400,300)，Δθ=90°：ROI 中心 (500,300) 绕定位点旋转 → (400,400)，角度 +90
        var rois = new List<(string, RoiRect)> { ("检测项", new RoiRect(0.5, 0.3, 0.1, 0.1, 0)) };
        var correction = new PoseCorrection("定位", ["检测"], 400, 300, 0, 400, 300, 90);
        var mapped = NodeRois.ApplyPoseCorrection(rois, "检测", 1000, 1000, correction);

        var (_, rect) = Assert.Single(mapped);
        var (cx, cy, _, _, angle) = rect.ToPixels(1000, 1000);
        Assert.InRange(cx, 399, 401);
        Assert.InRange(cy, 399, 401);
        Assert.InRange(angle, 89.5, 90.5);
    }

    [Fact]
    public void ApplyPoseCorrection_NonTargetUnchanged()
    {
        var roi = new RoiRect(0.5, 0.5, 0.2, 0.2, 0);
        var rois = new List<(string, RoiRect)> { ("别的节点项", roi) };
        var correction = new PoseCorrection("定位", ["检测"], 400, 300, 0, 500, 400, 30);
        var mapped = NodeRois.ApplyPoseCorrection(rois, "别的节点", 1000, 1000, correction);
        Assert.Same(rois, mapped);
        Assert.Equal(roi, mapped[0].Item2);
    }

    [Fact]
    public void ApplyPoseCorrection_NullCorrection_Passthrough()
    {
        var roi = new RoiRect(0.5, 0.5, 0.2, 0.2, 0);
        var rois = new List<(string, RoiRect)> { ("检测项", roi) };
        var mapped = NodeRois.ApplyPoseCorrection(rois, "检测", 1000, 1000, null);
        Assert.Same(rois, mapped);
    }

    [Fact]
    public void ApplyPoseCorrection_Scale_GrowsRectAndOffsets()
    {
        // 尺度 (2,1)、无旋转：ROI 中心 (500,500) 相对定位点 (400,300) 偏移 (100,200)
        // → 缩放后 (200,200) → 新中心 (400+200, 300+200)=(600,500)；宽 200→400，高不变
        var rois = new List<(string, RoiRect)> { ("检测项", new RoiRect(0.5, 0.5, 0.2, 0.2, 0)) };
        var correction = new PoseCorrection("定位", ["检测"], 400, 300, 0, 400, 300, 0, 2.0, 1.0);
        var mapped = NodeRois.ApplyPoseCorrection(rois, "检测", 1000, 1000, correction);

        var (_, rect) = Assert.Single(mapped);
        var (cx, cy, w, h, angle) = rect.ToPixels(1000, 1000);
        Assert.InRange(cx, 599, 601);
        Assert.InRange(cy, 499, 501);
        Assert.InRange(w, 399, 401);
        Assert.InRange(h, 199, 201);
        Assert.InRange(angle, -0.5, 0.5);
    }

    [Fact]
    public void Run_ScalesPassedToContext()
    {
        // scale_x/scale_y 参数非法时回退 1；有效值写入 ctx.PoseCorrection
        var init = new Dictionary<string, string> { ["source"] = "定位", ["base_x"] = "10", ["base_y"] = "20", ["base_angle"] = "0", ["scale_x"] = "abc", ["scale_y"] = "1.5" };
        var values = new Dictionary<string, string> { ["loc_valid"] = "1", ["loc_x"] = "30", ["loc_y"] = "40", ["loc_angle"] = "0" };
        RunCorrection(init, "定位", values, out var ctx);
        Assert.NotNull(ctx.PoseCorrection);
        Assert.Equal(1.0, ctx.PoseCorrection!.ScaleX, 4);
        Assert.Equal(1.5, ctx.PoseCorrection.ScaleY, 4);
    }

    [Fact]
    public void Run_PassthroughImageIsOwnedClone()
    {
        // 回归：透传图曾直接共享上游 Mat 实例，UI 释放上游图后悬空（Cannot access a disposed object）。
        // 现要求克隆独立副本：上游图释放后节点输出图仍可访问。
        var sourceImg = new Mat(320, 640, MatType.CV_8UC3, Scalar.All(90));
        try
        {
            var init = new Dictionary<string, string> { ["source"] = "定位" };
            var values = new Dictionary<string, string> { ["loc_valid"] = "1", ["loc_x"] = "30", ["loc_y"] = "40", ["loc_angle"] = "0" };
            var result = RunCorrection(init, "定位", values, out _, input: sourceImg);
            Assert.NotSame(sourceImg, result.OutputImage);
            sourceImg.Dispose(); // 模拟 UI 释放上游节点结果图
            Assert.Equal(640, result.OutputImage!.Width); // 不应抛 ObjectDisposedException
            result.OutputImage.Dispose();
        }
        finally
        {
            sourceImg.Dispose();
        }
    }
}
