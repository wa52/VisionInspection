using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>中间单一图像区域：最后一个有图像输出的节点结果自动上屏（PatchCore 时即热图）。</summary>
public class AutoDisplayTests
{
    private static NodeResult ImageResult(Mat img) => new()
    {
        Decision = "OK",
        OutputImage = img,
        Values = { ["decision"] = "OK" },
    };

    [Fact]
    public void Pipeline_LastImageNode_BecomesDisplayImage_WithNodeName()
    {
        NodeFactory.Register("FakeImg", (n, _) => new FakeNode(n, ctx => ImageResult(new Mat(8, 8, MatType.CV_8UC3, Scalar.All(50)))));
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "01 图像节点", Type = "FakeImg", Enabled = true },
                new RecipeNode { Name = "02 旧显示", Type = "Display", Enabled = true }, // 兼容空操作
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(50));
        var result = pipeline.Run(img, "t.bmp");
        try
        {
            Assert.NotNull(result.DisplayImage);
            Assert.Equal(8, result.DisplayImage!.Width);
            Assert.Equal("01 图像节点", result.DisplayNodeName); // 空操作 Display 不参与
            Assert.Single(result.NodeImages);
            Assert.Equal("OK", result.Decision);
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }

    [Fact]
    public void Pipeline_MultipleImageNodes_LastOneWins()
    {
        NodeFactory.Register("FakeImg", (n, _) => new FakeNode(n, ctx => ImageResult(new Mat(4, 4, MatType.CV_8UC3, Scalar.All(50)))));
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "01 图像源", Type = "FakeImg", Enabled = true },
                new RecipeNode { Name = "02 处理", Type = "FakeImg", Enabled = true },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(50));
        var result = pipeline.Run(img, "t.bmp");
        try
        {
            Assert.Equal(2, result.NodeImages.Count); // 每个节点图像都保留
            Assert.Equal("02 处理", result.DisplayNodeName); // 默认显示最后一个
            Assert.True(ReferenceEquals(result.NodeImages["02 处理"], result.DisplayImage));
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }

    [Fact]
    public void Pipeline_SingleImageNode_DisplayOnly_NoDoubleDispose()
    {
        NodeFactory.Register("FakeImg", (n, _) => new FakeNode(n, ctx => ImageResult(new Mat(4, 4, MatType.CV_8UC3, Scalar.All(50)))));
        var recipe = new Recipe
        {
            Nodes = [new RecipeNode { Name = "01 图像源", Type = "FakeImg", Enabled = true }],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(50));
        var result = pipeline.Run(img, "t.bmp");
        try
        {
            Assert.NotNull(result.DisplayImage);
            Assert.Equal("01 图像源", result.DisplayNodeName);
            Assert.Single(result.NodeImages);
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }

    [Fact]
    public void Pipeline_NoImageNodes_DisplayImagesNull()
    {
        NodeFactory.Register("FakeOk", (n, _) => new FakeNode(n, _ => new NodeResult { Decision = "OK", Values = { ["decision"] = "OK" } }));
        var recipe = new Recipe
        {
            Nodes = [new RecipeNode { Name = "01 检测", Type = "FakeOk", Enabled = true }],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(50));
        var result = pipeline.Run(img, "t.bmp");
        Assert.Null(result.DisplayImage);
        Assert.Null(result.DisplayNodeName);
    }

    [Fact]
    public void Pipeline_NodeAnnotations_FlowToResult()
    {
        // 节点矢量标注随结果透传（UI 屏幕常量渲染用）
        NodeFactory.Register("FakeAnn", (n, _) => new FakeNode(n, _ => new NodeResult
        {
            Decision = "OK",
            OutputImage = ImageResult(new Mat(4, 4, MatType.CV_8UC3, Scalar.All(50))).OutputImage,
            Annotations =
            {
                new NodeShape { Box = new OpenCvSharp.Rect(1, 1, 2, 2), Label = "obj 0.90", Kind = NodeShapeKind.Defect },
                new NodeShape { Polys = [new[] { new OpenCvSharp.Point(0, 0), new OpenCvSharp.Point(3, 0), new OpenCvSharp.Point(3, 3) }], Kind = NodeShapeKind.Info },
            },
        }));
        var recipe = new Recipe
        {
            Nodes = [new RecipeNode { Name = "01 检测", Type = "FakeAnn", Enabled = true }],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(50));
        var result = pipeline.Run(img, "t.bmp");
        try
        {
            var shapes = Assert.Single(result.NodeAnnotations["01 检测"].Where(s => s.Box != null));
            Assert.Equal(NodeShapeKind.Defect, shapes.Kind);
            Assert.Equal(2, result.NodeAnnotations["01 检测"].Count);
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }

    [Fact]
    public void Pipeline_ModelNodeImage_PreferredOverTrailingImageSource()
    {
        // 多图像源流程：检测节点之后还有图像源时，自动上屏仍选检测节点（不被尾部的图像源原图盖掉）
        NodeFactory.Register("FakeDet", (n, _) => new FakeNode(n, ctx => ImageResult(new Mat(6, 6, MatType.CV_8UC3, Scalar.All(50)))));
        var srcDir = Path.Combine(Path.GetTempPath(), "svi_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            using (var mat = new Mat(2, 4, MatType.CV_8UC3, Scalar.All(10)))
            {
                Assert.True(Cv2.ImWrite(Path.Combine(srcDir, "a.png"), mat));
            }

            var recipe = new Recipe
            {
                Nodes =
                [
                    new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = srcDir } },
                    new RecipeNode { Name = "02 检测", Type = "FakeDet", Enabled = true },
                    new RecipeNode { Name = "03 图像源B", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = srcDir } },
                ],
            };
            using var pipeline = new Pipeline(recipe);
            using var input = new Mat();
            var result = pipeline.Run(input, "t");
            try
            {
                Assert.Equal(3, result.NodeImages.Count); // 每个节点图像都保留在缩略图条
                Assert.Equal("02 检测", result.DisplayNodeName);
                Assert.True(ReferenceEquals(result.NodeImages["02 检测"], result.DisplayImage));
            }
            finally
            {
                foreach (var m in result.NodeImages.Values) m.Dispose();
            }
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }

    [Fact]
    public void Pipeline_NoModelNode_FallsBackToLastImageNode()
    {
        // 纯处理流程（无检测节点）：保持旧行为，显示最后一个有图节点
        var srcDir = Path.Combine(Path.GetTempPath(), "svi_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            using (var mat = new Mat(2, 4, MatType.CV_8UC3, Scalar.All(10)))
            {
                Assert.True(Cv2.ImWrite(Path.Combine(srcDir, "a.png"), mat));
            }

            var recipe = new Recipe
            {
                Nodes =
                [
                    new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = srcDir } },
                    new RecipeNode { Name = "02 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["threshold"] = "128" } },
                ],
            };
            using var pipeline = new Pipeline(recipe);
            using var input = new Mat();
            var result = pipeline.Run(input, "t");
            try
            {
                Assert.Equal(2, result.NodeImages.Count);
                Assert.Equal("02 二值化", result.DisplayNodeName);
            }
            finally
            {
                foreach (var m in result.NodeImages.Values) m.Dispose();
            }
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }

    [Fact]
    public void DisplayNode_CompatShim_NoOutputNoCrash()
    {
        // 旧方案里残留的 Display 节点：加载不报错、运行空操作
        var node = NodeFactory.Create("Display", "01 显示", new Dictionary<string, string> { ["source"] = "@input" });
        Assert.IsType<DisplayNode>(node);
        using var img = new Mat(4, 4, MatType.CV_8UC3);
        var ctx = new PipelineRunContext(img);
        var nr = node.Run(img, ctx);
        Assert.Equal("OK", nr.Decision);
        Assert.Null(nr.OutputImage);
        node.Dispose();
    }
}
