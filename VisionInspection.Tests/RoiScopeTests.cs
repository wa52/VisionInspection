using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>总范围 ROI 约束：解析/点包含判定/YOLO 检出过滤/实例分割实例过滤。</summary>
public class RoiScopeTests
{
    [Fact]
    public void ParseScopeIndex()
    {
        Assert.Equal(-1, NodeRois.ParseScopeIndex(null));
        Assert.Equal(-1, NodeRois.ParseScopeIndex(new Dictionary<string, string>()));
        Assert.Equal(-1, NodeRois.ParseScopeIndex(new Dictionary<string, string> { ["scope_index"] = "-3" }));
        Assert.Equal(-1, NodeRois.ParseScopeIndex(new Dictionary<string, string> { ["scope_index"] = "abc" }));
        Assert.Equal(2, NodeRois.ParseScopeIndex(new Dictionary<string, string> { ["scope_index"] = "2" }));
    }

    [Fact]
    public void ContainsPixel_AxisAlignedAndRotated()
    {
        // 图 1000×1000：ROI 中心 (500,500) 300×200 角度 0
        var rect = new RoiRect(0.5, 0.5, 0.3, 0.2, 0);
        Assert.True(NodeRois.ContainsPixel(rect, 500, 500, 1000, 1000));
        Assert.True(NodeRois.ContainsPixel(rect, 640, 590, 1000, 1000)); // 右下角内
        Assert.False(NodeRois.ContainsPixel(rect, 660, 610, 1000, 1000)); // 右下角外
        Assert.False(NodeRois.ContainsPixel(rect, 100, 100, 1000, 1000));

        // 同一矩形旋转 90°：原短边方向变为竖直
        var rotated = new RoiRect(0.5, 0.5, 0.3, 0.2, 90);
        Assert.True(NodeRois.ContainsPixel(rotated, 590, 640, 1000, 1000));
        Assert.False(NodeRois.ContainsPixel(rotated, 640, 590, 1000, 1000));
    }

    [Fact]
    public void YoloScopeFilter_KeepsScopeDetsAndInScopeDets()
    {
        // 图 1000×1000：scope=(200,200) 600×600；范围外 ROI 检出被过滤，范围内保留
        var rois = new List<(string, RoiRect)>
        {
            ("范围", new RoiRect(0.2, 0.2, 0.6, 0.6, 0)),
            ("内部项", new RoiRect(0.3, 0.3, 0.1, 0.1, 0)),
            ("外部项", new RoiRect(0.9, 0.9, 0.05, 0.05, 0)),
        };
        var dets = new List<YoloDetection>
        {
            new(0, "缺陷", 0.9f, 300, 300, 20, 20),  // 内部项检出，中心在范围内 → 保留
            new(0, "缺陷", 0.8f, 950, 950, 10, 10),  // 外部项检出，中心在范围外 → 移除
            new(1, "其他", 0.7f, 150, 150, 10, 10),  // 范围 ROI 自身检出 → 保留
        };
        var roiIdx = new List<int> { 1, 2, 0 };
        var counts = new List<(string, int)> { ("范围", 1), ("内部项", 1), ("外部项", 1) };
        var roiClassMap = new IReadOnlyList<string>[] { Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() };

        var (outDets, outIdx, outCounts) = YoloNode.ApplyScopeFilter(
            dets, roiIdx, counts, rois, 0, 1000, 1000, roiClassMap);

        Assert.Equal(2, outDets.Count);
        Assert.Equal(300, outDets[0].Cx);
        Assert.Equal(150, outDets[1].Cx);
        Assert.Equal(new[] { 1, 0 }, outIdx);
        Assert.Equal(("范围", 1), outCounts[0]);
        Assert.Equal(("内部项", 1), outCounts[1]);
        Assert.Equal(("外部项", 0), outCounts[2]); // 计数清零
    }

    [Fact]
    public void YoloScopeFilter_InvalidIndex_Passthrough()
    {
        var dets = new List<YoloDetection> { new(0, "A", 0.9f, 10, 10, 5, 5) };
        var roiIdx = new List<int> { 0 };
        var counts = new List<(string, int)> { ("R", 1) };
        var rois = new List<(string, RoiRect)> { ("R", new RoiRect(0.1, 0.1, 0.2, 0.2, 0)) };
        var (od, oi, oc) = YoloNode.ApplyScopeFilter(dets, roiIdx, counts, rois, -1, 1000, 1000, Array.Empty<IReadOnlyList<string>>());
        Assert.Same(dets, od);
        Assert.Same(roiIdx, oi);
        Assert.Same(counts, oc);
    }

    [Fact]
    public void SegInstanceFilter_CenterInScope()
    {
        // 实例中心在范围内保留、范围外移除（SegInstance.Box 为全图像素矩形）
        var scope = new RoiRect(0.5, 0.5, 0.6, 0.6, 0); // 中心 (500,500) 600×600
        var inBox = new Rect(400, 400, 50, 50);
        var outBox = new Rect(950, 950, 30, 30);
        var instances = new List<SegNode.SegInstance>
        {
            new(new SegDetection(0, 0.9f, 425, 425, 50, 50, 0, new float[32]), "缺陷", inBox, true, "R1", 0, true),
            new(new SegDetection(0, 0.8f, 965, 965, 30, 30, 1, new float[32]), "缺陷", outBox, true, "R2", 1, true),
        };

        var kept = instances.Where(i => NodeRois.ContainsPixel(
            scope, i.Box.X + i.Box.Width / 2.0, i.Box.Y + i.Box.Height / 2.0, 1000, 1000)).ToList();        Assert.Single(kept);
        Assert.Equal(425, kept[0].Det.Cx);
    }
}
