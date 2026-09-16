using System.Diagnostics;
using System.Globalization;
using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 轮廓匹配精度标定实验（合成图已知真值，非单元断言——产出统计表供真机对比）：
/// 盆架状目标（外圆环+8筋条留1缺口+中心孔），真值网格扫描 → 位置/角度误差分布、噪声/对比度退化、确定性、耗时。
/// 结果写 %TEMP%\svi_shape_accuracy.txt。约 1m40s，默认全量不跑：dotnet test --filter "Category=Performance"
/// </summary>
[Trait("Category", "Performance")]
public class ShapeMatchAccuracyTests
{
    private const int W = 1024;
    private const int H = 768;
    private const string OutPath = "svi_shape_accuracy.txt";
    private readonly ITestOutputHelper _out;
    private readonly List<string> _report = [];

    public ShapeMatchAccuracyTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void AccuracyReport()
    {
        // 守卫：默认全量跳过（~1m40s）；启用：dotnet test --filter "FullyQualifiedName~ShapeMatchAccuracyTests" 时设环境变量 SVI_ACCURACY=1
        if (Environment.GetEnvironmentVariable("SVI_ACCURACY") != "1")
        {
            _out.WriteLine("精度标定实验默认跳过（SVI_ACCURACY=1 启用；约 1m40s）");
            return;
        }

        try
        {
            // 模板：0° 标准位目标，patch 400×400，基准点=目标中心
            using var templatePatch = DrawFixture(400, 400, 200, 200, 0, brightness: 220);
            var template = ShapeTemplateBuilder.Build(templatePatch, 200, 200, sigma: 1.0, minContrast: 30, maxLevels: 4);
            Line($"模板点数: L0={template.LevelPoints[0].Count} 层数={template.Levels} (最粗层点数={template.LevelPoints[^1].Count})");
            Line("");

            // 1) 位置精度（角度 0°，网格扫描含亚像素真值）
            var positions = new List<(double X, double Y)>();
            for (var x = 200; x <= 800; x += 150)
                for (var y = 150; y <= 600; y += 150)
                    positions.Add((x, y));
            positions.Add((512.5, 384.5)); // 亚像素真值
            var posStats = RunSweep(template, positions.Select(p => (p.X, p.Y, 0.0)), noiseSigma: 0, brightness: 220, tag: "位置精度(0°,干净)");
            Report(posStats);

            // 2) 角度精度（中心固定，-30°~+30° 每 5°，外加亚角 0.3/0.7/24.6/-29.4）
            var angles = new List<double>();
            for (var a = -30.0; a <= 30.0; a += 5.0) angles.Add(a);
            angles.AddRange(new[] { 0.3, 0.7, 24.6, -29.4 });
            var angStats = RunSweep(template, angles.Select(a => (512.0, 384.0, a)), noiseSigma: 0, brightness: 220, tag: "角度精度(±30°,干净)");
            Report(angStats);

            // 3) 噪声鲁棒性：中心位姿（含 0.5px 亚像素）× 高斯噪声
            foreach (var sigma in new[] { 5, 10, 15, 20 })
            {
                var s = RunSweep(template, new[] { (512.5, 384.5, 0.0), (400.0, 300.0, 17.0), (700.0, 500.0, -22.0) },
                    noiseSigma: sigma, brightness: 220, tag: $"噪声σ={sigma}");
                Report(s);
            }

            // 4) 边缘对比度退化（干净图）：亮度 220/180/140，背景恒 60 → 梯度幅值近似 160/120/80
            foreach (var brightness in new[] { 180, 140 })
            {
                var s = RunSweep(template, new[] { (512.5, 384.5, 0.0), (400.0, 300.0, 17.0) },
                    noiseSigma: 0, brightness: brightness, tag: $"对比度退化(前景{brightness}/背景60)");
                Report(s);
            }

            // 5) 确定性：同一位姿 5 次
            var det = RunSweep(template, Enumerable.Repeat((512.5, 384.5, 12.3), 5), noiseSigma: 0, brightness: 220, tag: "确定性(同位姿×5)");
            Report(det);
        }
        finally
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), OutPath);
            System.IO.File.WriteAllLines(path, _report);
            _out.WriteLine("REPORT: " + path);
        }
    }

    /// <summary>单轮扫描：逐位姿匹配 → 误差统计。</summary>
    private SweepStats RunSweep(
        ShapeTemplate template, IEnumerable<(double X, double Y, double Angle)> poses,
        double noiseSigma, int brightness, string tag)
    {
        var matcher = new ShapeMatcher(template, minScore: 0.3, angleStart: -30, angleExtent: 60,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var errs = new List<double>();
        var angleErrs = new List<double>();
        var scores = new List<double>();
        var detected = 0; // minScore=0.7（节点默认门槛）下仍检出
        var totalMs = 0.0;
        var runs = 0;

        foreach (var (cx, cy, angle) in poses)
        {
            using var gray = DrawFixture(W, H, cx, cy, angle, brightness);
            AddNoise(gray, noiseSigma);
            var sw = Stopwatch.StartNew();
            var matches = matcher.Find(gray);
            sw.Stop();
            totalMs += sw.Elapsed.TotalMilliseconds;
            runs++;
            if (matches.Count == 0) continue;
            var best = matches[0];
            if (best.Score >= 0.7) detected++;
            scores.Add(best.Score);
            errs.Add(Math.Sqrt((best.X - cx) * (best.X - cx) + (best.Y - cy) * (best.Y - cy)));
            angleErrs.Add(Math.Abs(NormAngle(best.Angle - angle)));
        }

        var ms = runs == 0 ? 0 : totalMs / runs;
        return new SweepStats(tag, runs, detected, errs, angleErrs, scores, ms);
    }

    private void Report(SweepStats s)
    {
        var errPart = s.Errs.Count == 0 ? "全部丢失" :
            $"位置误差 px: 均值={s.Errs.Average():F2} P95={Percentile(s.Errs, 95):F2} 最大={s.Errs.Max():F2} | " +
            $"角度误差°: 均值={s.AngleErrs.Average():F2} 最大={s.AngleErrs.Max():F2} | " +
            $"分数: 均值={s.Scores.Average():F3} 最低={s.Scores.Min():F3}";
        var lost = s.Errs.Count == 0 ? s.Runs : s.Runs - s.Scores.Count;
        Line($"[{s.Tag}] 样本={s.Runs} 检出(score≥0.7)={s.Detected}/{s.Runs}{(lost > 0 ? $" 匹配失败={lost}" : "")} 耗时={s.MsPerRun:F0}ms/次");
        Line($"    {errPart}");
        Line("");
    }

    /// <summary>盆架状目标：外圆环(r120) + 8 根筋条(留 1 缺口) + 中心孔(r25)，背景 60。</summary>
    internal static Mat DrawFixtureForDiag(int w, int h, double cx, double cy, double angleDeg, int brightness) =>
        DrawFixture(w, h, cx, cy, angleDeg, brightness);

    private static Mat DrawFixture(int w, int h, double cx, double cy, double angleDeg, int brightness)
    {
        using var mat = new Mat(h, w, MatType.CV_8UC1, new Scalar(60));
        FillCircle(mat, cx, cy, 120, brightness);
        FillCircle(mat, cx, cy, 100, 60);   // 环宽 20
        FillCircle(mat, cx, cy, 25, 60);    // 中心孔
        for (var i = 0; i < 8; i++)
        {
            if (i == 3) continue; // 缺口：非对称特征，保证角度可观
            var a = (angleDeg + i * 45.0) * Math.PI / 180.0;
            var mx = cx + Math.Cos(a) * 80;
            var my = cy + Math.Sin(a) * 80;
            FillRotatedRect(mat, (float)mx, (float)my, 62, 12, angleDeg + i * 45.0, brightness);
        }
        var gray = new Mat();
        mat.ConvertTo(gray, MatType.CV_8UC1);
        return gray;
    }

    private static void FillCircle(Mat mat, double cx, double cy, double r, double value) =>
        Cv2.Circle(mat, (int)Math.Round(cx), (int)Math.Round(cy), (int)r, new Scalar(value), -1);

    private static void FillRotatedRect(Mat mat, float cx, float cy, float w, float h, double angleDeg, double value)
    {
        var rect = new RotatedRect(new Point2f(cx, cy), new Size2f(w, h), (float)angleDeg);
        var pts = rect.Points().Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y)).ToArray();
        Cv2.FillConvexPoly(mat, pts, new Scalar(value));
    }

    private static void AddNoise(Mat gray, double sigma)
    {
        if (sigma <= 0) return;
        using var noise = new Mat(gray.Rows, gray.Cols, MatType.CV_16SC1);
        Cv2.Randn(noise, new Scalar(0), new Scalar(sigma));
        using var wide = new Mat();
        gray.ConvertTo(wide, MatType.CV_16SC1);
        Cv2.Add(wide, noise, wide);
        wide.ConvertTo(gray, MatType.CV_8UC1);
    }

    private static double NormAngle(double a)
    {
        while (a > 180) a -= 360;
        while (a < -180) a += 360;
        return a;
    }

    private static double Percentile(List<double> values, int p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(sorted.Count * p / 100.0) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private void Line(string text) => _report.Add(text);

    private sealed record SweepStats(
        string Tag, int Runs, int Detected, List<double> Errs, List<double> AngleErrs,
        List<double> Scores, double MsPerRun);
}
