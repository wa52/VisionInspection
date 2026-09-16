using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>
/// 快速匹配（FastMatch）：与轮廓匹配同模板/同输出契约，搜索内核走快速模式
/// （点集抽稀 + 单轮亚像素精修）——合成图位姿恢复 + loc_* 定位契约 + 快于完整模式的时序方向。
/// </summary>
[Collection("FastMatch")] // 快速模式调优旋钮是 internal static，诊断/时序测试须与其它快速匹配测试串行
public class FastMatchTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public FastMatchTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }
    /// <summary>画边缘丰富的合成形状：带顶部缺口的亮矩形 + 圆孔（整体相对形状中心定位，可旋转）。</summary>
    private static Mat DrawShape(int w, int h, double cx, double cy, double angleDeg = 0)
    {
        var mat = new Mat(h, w, MatType.CV_8UC1, Scalar.All(40));
        var cos = Math.Cos(angleDeg * Math.PI / 180.0);
        var sin = Math.Sin(angleDeg * Math.PI / 180.0);

        FillRotatedRect(mat, (float)cx, (float)cy, 140, 100, angleDeg, new Scalar(220));

        var nx = cx + 0 * cos - (-30) * sin;
        var ny = cy + 0 * sin + (-30) * cos;
        FillRotatedRect(mat, (float)nx, (float)ny, 40, 40, angleDeg, new Scalar(40));

        var hx = cx + 30 * cos - 15 * sin;
        var hy = cy + 30 * sin + 15 * cos;
        Cv2.Circle(mat, (int)Math.Round(hx), (int)Math.Round(hy), 18, new Scalar(50), -1);
        return mat;
    }

    private static void FillRotatedRect(Mat mat, float cx, float cy, float w, float h, double angleDeg, Scalar color)
    {
        var cos = Math.Cos(angleDeg * Math.PI / 180.0);
        var sin = Math.Sin(angleDeg * Math.PI / 180.0);
        var hw = w / 2;
        var hh = h / 2;
        var pts = new (double X, double Y)[]
        {
            (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh),
        }
        .Select(c => new Point(
            (int)Math.Round(cx + c.X * cos - c.Y * sin),
            (int)Math.Round(cy + c.X * sin + c.Y * cos)))
        .ToArray();
        Cv2.FillConvexPoly(mat, pts, color);
    }

    private static ShapeTemplate BuildTemplate()
    {
        // 512×384，形状中心 (256,192)；模板 ROI 180×140 @ (166,122)，基准点=形状中心（patch 内 (90,70)）
        using var templateImg = DrawShape(512, 384, 256, 192);
        using var patch = new Mat(templateImg, new OpenCvSharp.Rect(166, 122, 180, 140)).Clone();
        var template = ShapeTemplateBuilder.Build(patch, 90, 70, 1.0, 30, 4);
        template.RoiX = 166;
        template.RoiY = 122;
        template.RoiW = 180;
        template.RoiH = 140;
        return template;
    }

    [Fact]
    public void NodeFactory_RegistersFastMatch()
    {
        using var node = NodeFactory.Create("FastMatch", "01 快速匹配");
        var fast = Assert.IsType<FastMatchNode>(node);
        Assert.Equal("FastMatch", node.Type);
        // 继承轮廓匹配：同参数定义/同判定模式
        Assert.Contains(node.ParamDefs, d => d.Key == "model_dir");
        Assert.Equal("OK", ContourMatchNode.ComputeDecision(null, found: true));
    }

    [Fact]
    public void FastMatcher_RecoversIdentityPose()
    {
        var template = BuildTemplate();
        using var search = DrawShape(800, 640, 400, 300);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -10, angleExtent: 20,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true, fastMode: true);
        var best = Assert.Single(matcher.Find(search));

        Assert.True(best.Score >= 0.7, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.X, 397, 403);
        Assert.InRange(best.Y, 297, 303);
        Assert.InRange(best.Angle, -2, 2);
    }

    [Fact]
    public void FastMatcher_RecoversRotatedPose()
    {
        var template = BuildTemplate();
        using var search = DrawShape(800, 640, 400, 300, angleDeg: 25);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true, fastMode: true);
        var best = Assert.Single(matcher.Find(search));

        Assert.True(best.Score >= 0.6, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.Angle, 22, 28);
        Assert.InRange(best.X, 396, 404);
        Assert.InRange(best.Y, 296, 304);
    }

    [Fact]
    public void FastMatcher_NoObjectBelowMinScore()
    {
        var template = BuildTemplate();
        using var search = new Mat(640, 800, MatType.CV_8UC1, Scalar.All(40));
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true, fastMode: true);
        Assert.Empty(matcher.Find(search));
    }

    [Fact]
    public void FastMatchNode_Run_OutputsLocatorContract()
    {
        // 模板落盘到临时目录（与建模弹窗保存同契约），节点经 NodeFactory 创建并 EnsureLoaded 后执行
        var template = BuildTemplate();
        var dir = Path.Combine(Path.GetTempPath(), "svi_fast_" + Guid.NewGuid().ToString("N"));
        FastMatchNode? fast = null;
        try
        {
            template.Save(dir);
            fast = Assert.IsType<FastMatchNode>(NodeFactory.Create("FastMatch", "01 快速匹配",
                new Dictionary<string, string> { ["model_dir"] = dir, ["source"] = "@input" }));
            fast.EnsureLoaded(dir);

            using var search = DrawShape(800, 640, 400, 300, angleDeg: 25);
            using var bgr = new Mat();
            Cv2.CvtColor(search, bgr, ColorConversionCodes.GRAY2BGR);
            var result = fast.Run(bgr, new PipelineRunContext(bgr));

            Assert.Equal("OK", result.Decision);
            Assert.Equal("1", result.Values["loc_valid"]);
            Assert.Equal("1", result.Values["matches"]);
            Assert.InRange(double.Parse(result.Values["loc_x"]), 396, 404);
            Assert.InRange(double.Parse(result.Values["loc_y"]), 296, 304);
            Assert.InRange(double.Parse(result.Values["loc_angle"]), 22, 28);
            Assert.NotEmpty(result.Annotations); // 十字/角度线/模板范围框矢量叠加
            result.OutputImage?.Dispose();
        }
        finally
        {
            fast.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FastMatcher_ParallelWorkers_SameResultAsSerial()
    {
        // 并行按角度/候选分块、按索引序合并——结果必须与串行完全一致（快速+完整两模式都锁）
        var template = BuildTemplate();
        using var search = DrawShape(800, 640, 400, 300, angleDeg: 25);
        foreach (var fast in new[] { true, false })
        {
            var serial = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
                numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true, fastMode: fast, maxWorkers: 1).Find(search);
            var par = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
                numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true, fastMode: fast, maxWorkers: 8).Find(search);

            var s = Assert.Single(serial);
            var p = Assert.Single(par);
            Assert.Equal(s.Score, p.Score, 6);
            Assert.Equal(s.X, p.X, 6);
            Assert.Equal(s.Y, p.Y, 6);
            Assert.Equal(s.Angle, p.Angle, 6);
        }
    }

    [Fact]
    public void NodeFactory_FastMatch_HasWorkersParam()
    {
        using var node = NodeFactory.Create("FastMatch", "01 快速匹配");
        var fast = Assert.IsType<FastMatchNode>(node);
        Assert.Contains(fast.ParamDefs, d => d.Key == "max_workers");
        Assert.Equal("0", fast.Params["max_workers"]);
        fast.SetParam("max_workers", "4");
        Assert.Equal("4", fast.Params["max_workers"]);
        // 非法值回退自动（≤0），不抛异常
        fast.SetParam("max_workers", "abc");
        Assert.Equal("abc", fast.Params["max_workers"]);
    }

    [Fact]
    public void FastMatchNode_FasterThanFullMode()
    {
        // 时序方向（不卡具体毫秒防抖动）：同搜索条件下快速模式必须快于完整模式（点集抽稀+粗层步长2，3~5 倍量级）
        var template = BuildTemplate();
        using var search = DrawShape(640, 480, 320, 240, angleDeg: 10);

        double Time(bool fast)
        {
            // 预热一次（JIT/缓存），再计时取平均
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
                numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true, fastMode: fast);
            _ = matcher.Find(search);
            return sw.Elapsed.TotalMilliseconds;
        }

        Time(fast: true);
        Time(fast: false);
        var fastMs = 0.0;
        var fullMs = 0.0;
        const int Rounds = 5;
        for (var i = 0; i < Rounds; i++)
        {
            fastMs += Time(fast: true);
            fullMs += Time(fast: false);
        }
        fastMs /= Rounds;
        fullMs /= Rounds;
        _output.WriteLine($"快速匹配平均 {fastMs:F1} ms / 完整匹配平均 {fullMs:F1} ms / 加速比 {fullMs / fastMs:F2}x");
        Assert.True(fastMs < 120, $"快速模式绝对上限(640×480 全图+60° 范围): {fastMs:F1}ms 应 <120ms");
        Assert.True(fastMs * 1.5 < fullMs, $"快速模式应显著快于完整模式(≥1.5x): fast={fastMs:F1}ms full={fullMs:F1}ms");
    }
}
