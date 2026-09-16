using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 诊断（真实场景复现，文件缺失时静默跳过）：泡棉检测方案的 08 快速匹配节点在真实原图上找不到目标。
/// 用完整模式结果作真值，探针检查：粗层候选里有没有真值附近候选、真值位姿在各层的得分与阈值差距。
/// </summary>
[Collection("FastMatch")] // 与 FastMatchTests 串行（共享调优旋钮静态字段）
public class FastMatchRealDiagTests
{
    private readonly ITestOutputHelper _output;

    public FastMatchRealDiagTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string TemplateDir = Environment.GetEnvironmentVariable("VISION_INSPECTION_FASTMATCH_TEMPLATE_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "模板", "08 快速匹配");

    private static readonly string FrameDir = Environment.GetEnvironmentVariable("VISION_INSPECTION_TEST_FRAME_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "test-data", "frames");

    private static readonly (double Cx, double Cy, double W, double H, double Angle) NodeRoi =
        (0.135, 0.5247, 0.2477, 0.551, 0);

    [Fact]
    public void Diag_Avx2VsScalar_ScoreCompare()
    {
        if (!Directory.Exists(TemplateDir) || !Directory.Exists(FrameDir))
        {
            return;
        }

        var template = ShapeTemplate.Load(TemplateDir);
        using var bgr = Cv2.ImRead(Directory.GetFiles(FrameDir, "*.jpg")[0], ImreadModes.Color);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var (cx, cy, w, h, angle) = new RoiRect(NodeRoi.Cx, NodeRoi.Cy, NodeRoi.W, NodeRoi.H, NodeRoi.Angle)
            .ToPixels(bgr.Width, bgr.Height);
        var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, bgr.Width, bgr.Height);
        using var cropView = new Mat(gray, cropRect);
        using var crop = cropView.Clone();

        // 完整模式粗层 L2（161×268）梯度场
        using var l1 = new Mat();
        Cv2.PyrDown(crop, l1);
        using var l2 = new Mat();
        Cv2.PyrDown(l1, l2);
        var (dirX, dirY, mag) = ShapeMatcher.ExtractDirections(l2, template.Sigma);
        var width = l2.Width;

        var matcher = new ShapeMatcher(template, 0.7, -30, 30, 1, 0.3, 30, template.Sigma, true, false);
        var points = template.LevelPoints[2];
        var mismatches = 0;
        var checkedCount = 0;
        foreach (var angleDeg in new[] { -30.0, -26.0, -18.0, -10.0, -2.0, 0.0 })
        {
            var rot = ShapeMatcher.BuildRotated(
                points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(),
                points.Select(p => p.Dx).ToArray(), points.Select(p => p.Dy).ToArray(), angleDeg);
            for (var py = 1; py < l2.Height - 2; py += 7)
            {
                for (var px = 1; px < width - 2; px += 11)
                {
                    var s1 = matcher.ScoreAtRotScalar(rot, dirX, dirY, mag, width, px, py, 0);
                    var s2 = matcher.ScoreAtRotAvx2(rot, dirX, dirY, mag, width, px, py, 0);
                    checkedCount++;
                    if (Math.Abs(s1 - s2) > 1e-6)
                    {
                        if (mismatches < 8)
                        {
                            _output.WriteLine($"失配: angle={angleDeg} px={px} py={py} scalar={s1:F4} avx2={s2:F4}");
                        }
                        mismatches++;
                    }
                }
            }
        }
        _output.WriteLine($"共检查 {checkedCount} 个(位置×角度)，失配 {mismatches}");
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void Diag_TruePoseScoreProbe()
    {
        if (!Directory.Exists(TemplateDir) || !Directory.Exists(FrameDir))
        {
            return;
        }

        var template = ShapeTemplate.Load(TemplateDir);
        using var bgr = Cv2.ImRead(Directory.GetFiles(FrameDir, "*.jpg")[0], ImreadModes.Color);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var (cx, cy, w, h, angle) = new RoiRect(NodeRoi.Cx, NodeRoi.Cy, NodeRoi.W, NodeRoi.H, NodeRoi.Angle)
            .ToPixels(bgr.Width, bgr.Height);
        var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, bgr.Width, bgr.Height);
        using var cropView = new Mat(gray, cropRect);
        using var crop = cropView.Clone();

        // 复刻 Find 的金字塔：L2 = 161×268
        using var l1 = new Mat();
        Cv2.PyrDown(crop, l1);
        using var l2 = new Mat();
        Cv2.PyrDown(l1, l2);
        var (dirX, dirY, mag) = ShapeMatcher.ExtractDirections(l2, template.Sigma);
        var width = l2.Width;

        var matcher = new ShapeMatcher(template, 0.7, -30, 30, 1, 0.3, 30, template.Sigma, true, false);
        var points = template.LevelPoints[2];
        var rot = ShapeMatcher.BuildRotated(
            points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(),
            points.Select(p => p.Dx).ToArray(), points.Select(p => p.Dy).ToArray(), 0.0);

        // 真值位姿 L2 = (49, 117)；角度 0 与网格角度 -2
        foreach (var ang in new[] { 0.0, -2.0, -4.0 })
        {
            var rotP = ShapeMatcher.BuildRotated(
                points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(),
                points.Select(p => p.Dx).ToArray(), points.Select(p => p.Dy).ToArray(), ang);
            var sDispatch = matcher.ScoreAtRot(rotP, dirX, dirY, mag, width, 49, 117, 0);
            var sScalar = matcher.ScoreAtRotScalar(rotP, dirX, dirY, mag, width, 49, 117, 0);
            var sAvx2 = matcher.ScoreAtRotAvx2(rotP, dirX, dirY, mag, width, 49, 117, 0);
            _output.WriteLine($"位姿(49,117,{ang:F0}°): dispatch={sDispatch:F4} scalar={sScalar:F4} avx2={sAvx2:F4}");
        }
        _output.WriteLine($"(点数={points.Count} 幅值长={mag.Length} 宽={width})");

        // 独立实现（不经 ShapeMatcher 任何方法）复核
        var matched = 0;
        var cos = (float)Math.Cos(0.0);
        var sin = (float)Math.Sin(0.0);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var ix = 49 + (int)Math.Round(p.X * cos - p.Y * sin);
            var iy = 117 + (int)Math.Round(p.X * sin + p.Y * cos);
            if (ix >= 0 && iy >= 0 && ix < width)
            {
                var idx = iy * width + ix;
                if (idx < mag.Length && mag[idx] >= 30f)
                {
                    var dot = p.Dx * dirX[idx] + p.Dy * dirY[idx];
                    if (dot >= Math.Cos(45.0 * Math.PI / 180.0)) matched++;
                }
            }
        }
        _output.WriteLine($"独立复核: matched={matched}/{points.Count} = {(double)matched / points.Count:F4}");

        // Find 全流程检查
        var findMatcher = new ShapeMatcher(template, 0.7, -30, 30, 1, 0.3, 30, template.Sigma, usePolarity: true, fastMode: false);
        var found = findMatcher.Find(crop);
        _output.WriteLine($"Find: 粗层=L{findMatcher.LastCoarsestLevel} 候选(限流前)={findMatcher.LastCoarseCandidates.Count} " +
            $"金字塔={findMatcher.LastPyramidMs:F1}ms 粗扫={findMatcher.LastCoarseMs:F1}ms 细化={findMatcher.LastRefineMs:F1}ms 结果={(found.Count == 0 ? "空" : $"{found.Count}个 score={found[0].Score:F3}")}");
        if (findMatcher.LastCoarseCandidates.Count > 0)
        {
            var top = findMatcher.LastCoarseCandidates.OrderByDescending(c => c.Score).Take(3);
            foreach (var c in top)
            {
                _output.WriteLine($"   候选 x={c.X:F1} y={c.Y:F1} ang={c.Angle:F1} score={c.Score:F3}");
            }
        }
    }

    [Fact]
    public void Diag_RealScene_ProbeBreak()
    {
        if (!Directory.Exists(TemplateDir) || !Directory.Exists(FrameDir))
        {
            return; // 真实数据缺失（仅本机诊断用）
        }

        var template = ShapeTemplate.Load(TemplateDir);
        _output.WriteLine(
            $"模板: Levels={template.Levels} 点数=[{string.Join(",", template.LevelPoints.Select(l => l.Count))}] " +
            $"ROI={template.RoiW:F0}x{template.RoiH:F0} Sigma={template.Sigma}");

        foreach (var frame in Directory.GetFiles(FrameDir, "*.jpg").Take(4))
        {
            using var bgr = Cv2.ImRead(frame, ImreadModes.Color);
            if (bgr.Empty()) continue;
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            var (cx, cy, w, h, angle) = new RoiRect(NodeRoi.Cx, NodeRoi.Cy, NodeRoi.W, NodeRoi.H, NodeRoi.Angle)
                .ToPixels(bgr.Width, bgr.Height);
            var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, bgr.Width, bgr.Height);
            using var crop = new Mat(gray, cropRect);
            _output.WriteLine($"— {Path.GetFileName(frame)} 裁剪={cropRect.Width}x{cropRect.Height}");

            // 基准：旧行为（stride1 + 串行）——"效果不变"的对照
            ShapeMatcher.FullCoarseStride2 = false;
            var swRef = System.Diagnostics.Stopwatch.StartNew();
            var reference = new ShapeMatcher(template, minScore: 0.7, angleStart: -30, angleExtent: 30,
                numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: template.Sigma,
                usePolarity: true, fastMode: false, maxWorkers: 1).Find(crop).FirstOrDefault();
            swRef.Stop();

            // 新行为：stride2 + 并行（默认）
            ShapeMatcher.FullCoarseStride2 = true;
            var swNew = System.Diagnostics.Stopwatch.StartNew();
            var optMatcher = new ShapeMatcher(template, minScore: 0.7, angleStart: -30, angleExtent: 30,
                numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: template.Sigma,
                usePolarity: true, fastMode: false);
            var optimized = optMatcher.Find(crop).FirstOrDefault();
            swNew.Stop();

            // 快速模式相位分解（当前默认旋钮：大模板自动退保守采样）
            var fastMatcher = new ShapeMatcher(template, minScore: 0.7, angleStart: -30, angleExtent: 30,
                numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: template.Sigma,
                usePolarity: true, fastMode: true);
            var fastBest = fastMatcher.Find(crop).FirstOrDefault();
            _output.WriteLine($"   快速模式: {(fastBest == null ? "未找到" : $"score={fastBest.Score:F3} x={fastBest.X:F1} y={fastBest.Y:F1} ang={fastBest.Angle:F1}")}" +
                $"  相位: 金字塔={fastMatcher.LastPyramidMs:F1} 粗扫={fastMatcher.LastCoarseMs:F1} 细化={fastMatcher.LastRefineMs:F1} 亚像素={fastMatcher.LastSubMs:F1}");

            var refDesc = reference == null ? "未找到" : $"score={reference.Score:F4} x={reference.X:F2} y={reference.Y:F2} ang={reference.Angle:F3}";
            var newDesc = optimized == null ? "未找到" : $"score={optimized.Score:F4} x={optimized.X:F2} y={optimized.Y:F2} ang={optimized.Angle:F3}";
            _output.WriteLine($"   旧(串行+stride1): {refDesc}  {swRef.Elapsed.TotalMilliseconds:F0}ms");
            _output.WriteLine($"   新(并行+stride2): {newDesc}  {swNew.Elapsed.TotalMilliseconds:F0}ms");
            _output.WriteLine($"   相位: 金字塔={optMatcher.LastPyramidMs:F1}ms 粗扫={optMatcher.LastCoarseMs:F1}ms 细化={optMatcher.LastRefineMs:F1}ms 亚像素={optMatcher.LastSubMs:F1}ms");

            if (reference != null && optimized != null)
            {
                var dx = Math.Abs(reference.X - optimized.X);
                var dy = Math.Abs(reference.Y - optimized.Y);
                var dAng = Math.Abs(reference.Angle - optimized.Angle);
                var dScore = Math.Abs(reference.Score - optimized.Score);
                _output.WriteLine($"   偏差: dx={dx:F3} dy={dy:F3} dAng={dAng:F3} dScore={dScore:F4}");
                Assert.True(dx <= 0.5 && dy <= 0.5, $"位置偏差过大: dx={dx:F3} dy={dy:F3}");
                Assert.True(dAng <= 0.05, $"角度偏差过大: {dAng:F3}");
                Assert.True(dScore <= 0.005, $"分数偏差过大: {dScore:F4}");

                // 端到端节点路径（含 ROI 裁剪 + 灰度转换）：生产实际耗时
                using var node = NodeFactory.Create("ContourMatch", "07 轮廓匹配", new Dictionary<string, string>
                {
                    ["source"] = "@input",
                    ["model_dir"] = TemplateDir,
                    ["own_rois"] = "ROI;0.135,0.5247,0.2477,0.551,0",
                    ["angle_start"] = "-30",
                    ["angle_extent"] = "30",
                });
                var cm = Assert.IsType<ContourMatchNode>(node);
                cm.EnsureLoaded(TemplateDir);
                var swNode = System.Diagnostics.Stopwatch.StartNew();
                var nodeResult = cm.Run(bgr, new PipelineRunContext(bgr));
                swNode.Stop();
                _output.WriteLine($"   节点端到端(裁剪+灰度+匹配): {swNode.Elapsed.TotalMilliseconds:F0}ms 判定={nodeResult.Decision}");
                nodeResult.OutputImage?.Dispose();
            }
            else
            {
                Assert.True(reference == null && optimized == null, "新旧行为找到状态不一致");
            }
        }
    }
}
