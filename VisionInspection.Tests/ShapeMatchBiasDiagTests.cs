using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>诊断：网格点误差向量（方向恒定=建模/绘制系统偏差；随机=算法量化）。</summary>
public class ShapeMatchBiasDiagTests
{
    private readonly ITestOutputHelper _out;

    public ShapeMatchBiasDiagTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void BiasVectors()
    {
        using var patch = ShapeMatchAccuracyTests.DrawFixtureForDiag(400, 400, 200, 200, 0, 220);
        var template = ShapeTemplateBuilder.Build(patch, 200, 200, 1.0, 30, 4);
        var matcher = new ShapeMatcher(template, 0.3, -30, 60, 1, 0.3, 30, 1.0, true);

        foreach (var (cx, cy) in new[] { (500, 450), (350, 300), (650, 600), (512.5, 384.5) })
        {
            using var gray = ShapeMatchAccuracyTests.DrawFixtureForDiag(1024, 768, cx, cy, 0, 220);
            var m = matcher.Find(gray)[0];
            _out.WriteLine($"真值({cx},{cy}) → 匹配({m.X:F2},{m.Y:F2}) 角度={m.Angle:F2} 分数={m.Score:F3} 偏差=({m.X - cx:+0.00;-0.00},{m.Y - cy:+0.00;-0.00}) 模={Math.Sqrt((m.X - cx) * (m.X - cx) + (m.Y - cy) * (m.Y - cy)):F2}");
        }
    }
}
