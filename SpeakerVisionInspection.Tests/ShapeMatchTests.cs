using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>轮廓匹配核心：合成图位姿恢复、模板 round-trip、圆域 NMS、判定。</summary>
public class ShapeMatchTests
{
    /// <summary>画边缘丰富的合成形状：带顶部缺口的亮矩形 + 圆孔（整体相对形状中心定位，可旋转）。</summary>
    private static Mat DrawShape(int w, int h, double cx, double cy, double angleDeg = 0)
    {
        var mat = new Mat(h, w, MatType.CV_8UC1, Scalar.All(40));
        var cos = Math.Cos(angleDeg * Math.PI / 180.0);
        var sin = Math.Sin(angleDeg * Math.PI / 180.0);

        // 主体 140×100
        FillRotatedRect(mat, (float)cx, (float)cy, 140, 100, angleDeg, new Scalar(220));

        // 顶部缺口 40×40（相对中心 (0,-30)）
        var nx = cx + 0 * cos - (-30) * sin;
        var ny = cy + 0 * sin + (-30) * cos;
        FillRotatedRect(mat, (float)nx, (float)ny, 40, 40, angleDeg, new Scalar(40));

        // 圆孔（相对中心 (30,15)，r=18；圆旋转不变，只转位置）
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

    private static ShapeTemplate BuildTemplateFromStandard()
    {
        // 512×384，形状中心 (256,192)；模板 ROI 180×140 @ (166,122)，基准点=形状中心（patch 内 (90,70)）
        using var templateImg = DrawShape(512, 384, 256, 192);
        using var patch = new Mat(templateImg, new OpenCvSharp.Rect(166, 122, 180, 140)).Clone();
        var template = ShapeTemplateBuilder.Build(patch, 90, 70, 1.0, 30, 4);
        // 建模弹窗保存时总会写 ROI 几何（模板范围框用）
        template.RoiX = 166;
        template.RoiY = 122;
        template.RoiW = 180;
        template.RoiH = 140;
        return template;
    }

    /// <summary>3 层模板（360×280 patch → L=3），配 1000×800 搜索图（搜索也 3 层，覆盖跨层细化路径）。</summary>
    private static ShapeTemplate BuildTemplateMultiLevel()
    {
        using var templateImg = DrawShape(720, 560, 360, 280);
        using var patch = new Mat(templateImg, new OpenCvSharp.Rect(180, 140, 360, 280)).Clone();
        return ShapeTemplateBuilder.Build(patch, 180, 140, 1.0, 30, 4);
    }

    [Fact]
    public void Builder_ExtractsUnitDirectionPoints()
    {
        var template = BuildTemplateFromStandard();
        Assert.True(template.Levels >= 2);
        var pts = template.LevelPoints[0];
        Assert.True(pts.Count > 50, $"点数不足: {pts.Count}");
        Assert.All(pts, p =>
        {
            var len = Math.Sqrt(p.Dx * p.Dx + p.Dy * p.Dy);
            Assert.True(Math.Abs(len - 1.0) < 0.02);
        });
    }

    [Fact]
    public void Template_JsonRoundTrip()
    {
        var template = BuildTemplateFromStandard();
        var dir = Path.Combine(Path.GetTempPath(), "svi_shape_" + Guid.NewGuid().ToString("N"));
        try
        {
            template.Save(dir);
            var loaded = ShapeTemplate.Load(dir);
            Assert.Equal(template.ReferenceX, loaded.ReferenceX);
            Assert.Equal(template.ReferenceY, loaded.ReferenceY);
            Assert.Equal(template.Levels, loaded.Levels);
            Assert.Equal(template.LevelPoints[0].Count, loaded.LevelPoints[0].Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Matcher_RecoversIdentityPose()
    {
        var template = BuildTemplateFromStandard();
        using var search = DrawShape(800, 640, 400, 300);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -10, angleExtent: 20,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var matches = matcher.Find(search);

        var best = Assert.Single(matches);
        Assert.True(best.Score >= 0.7, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.X, 397, 403);
        Assert.InRange(best.Y, 297, 303);
        Assert.InRange(best.Angle, -2, 2);
    }

    [Fact]
    public void Matcher_RecoversRotatedPose()
    {
        var template = BuildTemplateFromStandard();
        using var search = DrawShape(800, 640, 400, 300, angleDeg: 25);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var matches = matcher.Find(search);

        var best = Assert.Single(matches);
        Assert.True(best.Score >= 0.6, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.Angle, 22, 28);
        Assert.InRange(best.X, 396, 404);
        Assert.InRange(best.Y, 296, 304);
    }

    [Fact]
    public void Matcher_MultiLevel_RecoversIdentityPose()
    {
        // 回归：≥3 层时跨层细化曾因层坐标换算反向全部丢候选 → 恒等位姿必须恢复
        var template = BuildTemplateMultiLevel();
        Assert.True(template.Levels >= 3, $"模板层数: {template.Levels}");
        using var search = DrawShape(1000, 800, 500, 400);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -10, angleExtent: 20,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var matches = matcher.Find(search);

        var best = Assert.Single(matches);
        Assert.True(best.Score >= 0.7, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.X, 496, 504);
        Assert.InRange(best.Y, 396, 404);
        Assert.InRange(best.Angle, -2, 2);
    }

    [Fact]
    public void Matcher_MultiLevel_RecoversRotatedPose()
    {
        var template = BuildTemplateMultiLevel();
        using var search = DrawShape(1000, 800, 500, 400, angleDeg: 25);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var matches = matcher.Find(search);

        var best = Assert.Single(matches);
        Assert.True(best.Score >= 0.6, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.Angle, 22, 28);
        Assert.InRange(best.X, 495, 505);
        Assert.InRange(best.Y, 395, 405);
    }

    [Fact]
    public void ContourOverlay_TransformsTemplatePointsToPose()
    {
        var template = BuildTemplateFromStandard();
        Assert.NotEmpty(template.LevelPoints[0]);

        // 恒等位姿：点 = 模板点 + (400,300)
        var shapes = ContourMatchNode.BuildContourOverlay(
            [new ShapeMatchInstance(400, 300, 0, 0.95)], template, "OK");
        Assert.Equal(2, shapes.Count); // 点集 + 模板范围框
        var shape = shapes[0];
        Assert.True(shape.AsPoints);
        Assert.Empty(shape.Label);
        var points = Assert.Single(shape.Polys);
        Assert.NotEmpty(points);
        var p0 = template.LevelPoints[0][0];
        Assert.InRange(points[0].X, 400 + p0.X - 1.5, 400 + p0.X + 1.5);
        Assert.InRange(points[0].Y, 300 + p0.Y - 1.5, 300 + p0.Y + 1.5);

        // 模板范围框：恒等位姿下中心 = (400+RoiX+RoiW/2 …) —— 框中心 = 基准点绝对位置 + ROI 偏差
        var rectPoly = Assert.Single(shapes[1].Polys);
        Assert.Equal(4, rectPoly.Length);

        // 旋转 90°：点 = (400 − p.Y, 300 + p.X)
        var rotated = ContourMatchNode.BuildContourOverlay(
            [new ShapeMatchInstance(400, 300, 90, 0.95)], template, "OK");
        Assert.Equal(2, rotated.Count);
        var rpoints = Assert.Single(rotated[0].Polys);
        Assert.InRange(rpoints[0].X, 400 - p0.Y - 1.5, 400 - p0.Y + 1.5);
        Assert.InRange(rpoints[0].Y, 300 + p0.X - 1.5, 300 + p0.X + 1.5);
    }

    [Fact]
    public void Matcher_SubPixel_DoesNotBreakPoseRecovery()
    {
        // 亚像素细化后：位姿恢复精度不回退，且输出可为小数
        var template = BuildTemplateFromStandard();
        using var search = DrawShape(800, 640, 400, 300, angleDeg: 25);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var best = Assert.Single(matcher.Find(search));
        Assert.InRange(best.Angle, 23, 27);
        Assert.InRange(best.X, 396, 404);
        Assert.InRange(best.Y, 296, 304);
    }

    [Fact]
    public void Matcher_SmallSearchRegion_StillFindsObject()
    {
        // 回归：ROI 裁剪区比模板旋转包络还小时，基准点位置范围不得为空（否则"画了 ROI 模板就不生效"）
        var template = BuildTemplateMultiLevel();
        using var search = DrawShape(200, 160, 100, 80);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -10, angleExtent: 20,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var best = Assert.Single(matcher.Find(search));
        Assert.True(best.Score >= 0.6, $"分数过低: {best.Score:F3}");
        Assert.InRange(best.X, 96, 104);
        Assert.InRange(best.Y, 76, 84);
    }

    [Fact]
    public void Matcher_NoObjectBelowMinScore()
    {
        var template = BuildTemplateFromStandard();
        using var search = new Mat(640, 800, MatType.CV_8UC1, Scalar.All(40)); // 空背景
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        Assert.Empty(matcher.Find(search));
    }

    [Fact]
    public void CircleIou_KnownValues()
    {
        Assert.Equal(1.0, ShapeMatcher.CircleIou(10, 10, 20, 10, 10, 20), 6);
        Assert.Equal(0.0, ShapeMatcher.CircleIou(10, 10, 20, 60, 10, 20), 6);
        Assert.InRange(ShapeMatcher.CircleIou(10, 10, 20, 30, 10, 20), 0.2, 0.29); // d=r
    }

    [Fact]
    public void ComputeDecision_Modes()
    {
        Assert.Equal("OK", ContourMatchNode.ComputeDecision(null, found: true));
        Assert.Equal("NG", ContourMatchNode.ComputeDecision(null, found: false));
        Assert.Equal("NG", ContourMatchNode.ComputeDecision(ContourMatchNode.DecisionFoundNg, found: true));
        Assert.Equal("OK", ContourMatchNode.ComputeDecision(ContourMatchNode.DecisionFoundNg, found: false));
    }

    [Fact]
    public void NodeFactory_RegistersContourMatch()
    {
        using var node = NodeFactory.Create("ContourMatch", "01 轮廓匹配");
        Assert.IsType<ContourMatchNode>(node);
        Assert.Equal("ContourMatch", node.Type);
    }
}
