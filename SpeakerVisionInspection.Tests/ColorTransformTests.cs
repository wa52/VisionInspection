using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>颜色变换节点：灰度化方式（加权/平均/单分量）、透传、来源解析、下游可用。</summary>
public class ColorTransformTests
{
    /// <summary>2×2 BGR 已知像素图。</summary>
    private static Mat MakeBgr(params (byte B, byte G, byte R)[] pixels)
    {
        var mat = new Mat(2, 2, MatType.CV_8UC3);
        for (var i = 0; i < pixels.Length && i < 4; i++)
        {
            mat.At<Vec3b>(i / 2, i % 2) = new Vec3b(pixels[i].B, pixels[i].G, pixels[i].R);
        }
        return mat;
    }

    private static Mat RunTransform(Mat input, string method)
    {
        using var node = new ColorTransformNode("颜色变换1");
        node.SetParam("method", method);
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);
        Assert.Equal("OK", result.Decision);
        Assert.True(result.OutputImage != null, result.Error ?? "无输出图像（无 Error）");
        var output = result.OutputImage!;
        Assert.Equal(result.Values["out_w"], output.Width.ToString());
        Assert.Equal(result.Values["out_h"], output.Height.ToString());
        return output; // 所有权移交调用方
    }

    [Fact]
    public void Transform_WeightedMatchesFormula()
    {
        using var bgr = MakeBgr((10, 20, 30), (100, 150, 200), (0, 255, 128), (64, 64, 64));
        using var gray = RunTransform(bgr, ColorTransformNode.MethodWeighted);
        Assert.Equal(1, gray.Channels());
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var p = bgr.At<Vec3b>(y, x);
                var expected = 0.114 * p[0] + 0.587 * p[1] + 0.299 * p[2];
                Assert.InRange(gray.At<byte>(y, x), expected - 1, expected + 1);
            }
        }
    }

    [Fact]
    public void Transform_AverageMatchesFormula()
    {
        using var bgr = MakeBgr((10, 20, 30), (100, 150, 200), (0, 255, 128), (250, 250, 250));
        using var gray = RunTransform(bgr, ColorTransformNode.MethodAverage);
        Assert.Equal(1, gray.Channels());
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var p = bgr.At<Vec3b>(y, x);
                var expected = (p[0] + p[1] + p[2]) / 3.0;
                Assert.InRange(gray.At<byte>(y, x), expected - 1, expected + 1);
            }
        }
    }

    [Fact]
    public void Transform_ChannelExtract()
    {
        using var bgr = MakeBgr((10, 20, 30), (100, 150, 200), (0, 255, 128), (64, 64, 64));
        using var r = RunTransform(bgr, ColorTransformNode.MethodR);
        using var g = RunTransform(bgr, ColorTransformNode.MethodG);
        using var b = RunTransform(bgr, ColorTransformNode.MethodB);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var p = bgr.At<Vec3b>(y, x);
                Assert.Equal(p[2], r.At<byte>(y, x)); // R 分量
                Assert.Equal(p[1], g.At<byte>(y, x)); // G 分量
                Assert.Equal(p[0], b.At<byte>(y, x)); // B 分量
            }
        }
    }

    [Fact]
    public void Transform_GrayPassthrough()
    {
        using var src = new Mat(4, 5, MatType.CV_8UC1, Scalar.All(77));
        using var gray = RunTransform(src, ColorTransformNode.MethodWeighted);
        Assert.Equal(1, gray.Channels());
        Assert.Equal(77, Cv2.Mean(gray).Val0);
    }

    [Fact]
    public void Transform_BgraInput()
    {
        using var bgra = new Mat(2, 2, MatType.CV_8UC4, new Scalar(10, 20, 30, 255));
        using var gray = RunTransform(bgra, ColorTransformNode.MethodWeighted);
        Assert.Equal(1, gray.Channels());
        var expected = 0.114 * 10 + 0.587 * 20 + 0.299 * 30;
        Assert.InRange(gray.At<byte>(0, 0), expected - 1, expected + 1);
    }

    [Fact]
    public void Run_MissingSource_NoCrash()
    {
        using var node = new ColorTransformNode("颜色变换1");
        node.SetParam("source", "图像源9");
        using var input = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(50));
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);
        Assert.Equal("OK", result.Decision);
        Assert.Null(result.OutputImage);
    }

    [Fact]
    public void NodeFactory_RegistersColorTransform()
    {
        using var node = NodeFactory.Create("ColorTransform", "02 颜色变换");
        Assert.IsType<ColorTransformNode>(node);
        Assert.Equal("ColorTransform", node.Type);
    }

    [Fact]
    public void GrayOutput_FeedsShapeMatcher()
    {
        // 集成：彩色图 → 颜色变换灰度 → 轮廓匹配位姿恢复
        using var templateGray = new Mat(320, 400, MatType.CV_8UC1, Scalar.All(40));
        templateGray.Rectangle(new Rect(150, 110, 100, 100), new Scalar(220), -1);
        using var patch = new Mat(templateGray, new Rect(140, 100, 120, 120)).Clone();
        var template = ShapeTemplateBuilder.Build(patch, 60, 60, 1.0, 30, 4);

        using var searchGray = new Mat(320, 400, MatType.CV_8UC1, Scalar.All(40));
        searchGray.Rectangle(new Rect(200, 110, 100, 100), new Scalar(220), -1);
        using var searchBgr = new Mat();
        Cv2.CvtColor(searchGray, searchBgr, ColorConversionCodes.GRAY2BGR);

        using var grayOut = RunTransform(searchBgr, ColorTransformNode.MethodWeighted);
        var matcher = new ShapeMatcher(template, minScore: 0.6, angleStart: -10, angleExtent: 20,
            numMatches: 1, maxOverlap: 0.3, minContrast: 30, sigma: 1.0, usePolarity: true);
        var best = Assert.Single(matcher.Find(grayOut));
        Assert.InRange(best.X, 246, 254);
        Assert.InRange(best.Y, 156, 164);
    }
}
