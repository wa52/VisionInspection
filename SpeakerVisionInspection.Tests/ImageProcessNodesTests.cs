using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>图像源/二值化/几何变换三个处理节点的行为与失配日志。</summary>
public class ImageProcessNodesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "svi_imgproc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static Mat TwoToneRow() // 1x2 BGR：左黑右白
    {
        var mat = new Mat(1, 2, MatType.CV_8UC3, new Scalar(0, 0, 0));
        Cv2.Rectangle(mat, new Rect(1, 0, 1, 1), new Scalar(255, 255, 255), -1);
        return mat;
    }

    // ===== Binarize =====

    [Fact]
    public void Binarize_Binary_ThresholdsPixels()
    {
        using var src = TwoToneRow();
        var node = new BinarizeNode("二值化", new Dictionary<string, string> { ["source"] = "@input", ["threshold"] = "128" });
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.Equal(1, result.OutputImage!.Channels());
        Assert.True(result.OutputImage.GetArray(out byte[] pixels));
        Assert.Equal(0, pixels[0]);
        Assert.Equal(255, pixels[1]);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Binarize_BinaryInv_InvertsPixels()
    {
        using var src = TwoToneRow();
        var node = new BinarizeNode("二值化", new Dictionary<string, string> { ["source"] = "@input", ["threshold"] = "128", ["type"] = "BinaryInv" });
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.True(result.OutputImage!.GetArray(out byte[] pixels));
        Assert.Equal(255, pixels[0]);
        Assert.Equal(0, pixels[1]);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Binarize_MissingSource_LogsAndReturnsNull()
    {
        var logs = new List<string>();
        var node = new BinarizeNode("二值化", new Dictionary<string, string> { ["source"] = "没有这个节点" }) { Log = logs.Add };
        using var src = TwoToneRow();
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.Null(result.OutputImage);
        Assert.Contains(logs, l => l.Contains("[Binarize]") && l.Contains("图像来源未找到"));
    }

    // ===== Geometry =====

    [Fact]
    public void Geometry_Resize_ScalesDimensions()
    {
        using var src = TwoToneRow(); // 2x1
        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input", ["op"] = "resize", ["scale"] = "0.5" });
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.Equal(1, result.OutputImage!.Width);
        Assert.Equal(1, result.OutputImage!.Height);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_Rotate90_Expand_SwapsDimensions()
    {
        var logs = new List<string>();
        using var src = new Mat(2, 4, MatType.CV_8UC3, new Scalar(10, 20, 30)); // w=4 h=2
        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input", ["op"] = "rotate", ["angle"] = "90" }) { Log = logs.Add };
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.True(result.OutputImage != null, "OutputImage 为空，日志: " + string.Join(" | ", logs));
        Assert.Equal(2, result.OutputImage!.Width);
        Assert.Equal(4, result.OutputImage!.Height);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_Rotate45_Expand_BoundingBox()
    {
        using var src = new Mat(50, 100, MatType.CV_8UC3, new Scalar(10, 20, 30)); // w=100 h=50
        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input", ["op"] = "rotate", ["angle"] = "45" });
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        // 45° 外接框: w=h=|100·cos45|+|50·sin45| ≈ 106
        Assert.Equal(106, result.OutputImage!.Width);
        Assert.Equal(106, result.OutputImage!.Height);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_SetParamAngle_TakesEffectOnNextRun()
    {
        using var src = new Mat(50, 100, MatType.CV_8UC3, new Scalar(10, 20, 30));
        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input", ["op"] = "rotate", ["angle"] = "0" });
        var ctx = new PipelineRunContext(src);

        var r0 = node.Run(src, ctx);
        Assert.Equal(100, r0.OutputImage!.Width); // angle=0 → 原尺寸
        r0.OutputImage.Dispose();

        node.SetParam("angle", "90"); // 参数热更新路径（ApplyNodeParam → SetParam）

        var r1 = node.Run(src, ctx);
        Assert.Equal(50, r1.OutputImage!.Width); // 90° 扩边 → 宽高互换
        Assert.Equal(100, r1.OutputImage!.Height);
        r1.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_CombinedScaleRotateFlip_AllApplyInOrder()
    {
        using var src = new Mat(2, 4, MatType.CV_8UC1, new Scalar(0)); // w=4 h=2
        Cv2.Rectangle(src, new Rect(0, 0, 1, 1), new Scalar(255), -1); // 左上角白块
        var node = new GeometryNode("几何", new Dictionary<string, string>
        {
            ["source"] = "@input",
            ["scale"] = "0.5",     // 4x2 → 2x1
            ["angle"] = "90",      // 2x1 → 1x2（扩边）
            ["flip_code"] = "上下翻转",
        });
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.Equal(1, result.OutputImage!.Width);
        Assert.Equal(2, result.OutputImage!.Height);
        Assert.Contains("scale", result.Values["transforms"]);
        Assert.Contains("angle", result.Values["transforms"]);
        Assert.Contains("flip", result.Values["transforms"]);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_Identity_ClonesInput()
    {
        using var src = new Mat(3, 5, MatType.CV_8UC3, new Scalar(9, 9, 9));
        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input" }); // 全默认：无变换
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.Equal(5, result.OutputImage!.Width);
        Assert.Equal("none", result.Values["transforms"]);
        Assert.NotSame(src, result.OutputImage); // 克隆而非引用
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_FlipHorizontal_MirrorsPixels()
    {
        using var src = new Mat(1, 2, MatType.CV_8UC1, new Scalar(0));
        Cv2.Rectangle(src, new Rect(1, 0, 1, 1), new Scalar(255), -1);
        Assert.True(src.GetArray(out byte[] srcPixels), "源图读取失败");
        Assert.True(srcPixels[1] == 255, $"源图像素异常: [{string.Join(",", srcPixels)}]");

        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "@input", ["op"] = "flip", ["flip_code"] = "Horizontal" });
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.True(result.OutputImage!.GetArray(out byte[] pixels), $"输出读取失败: [{string.Join(",", pixels ?? Array.Empty<byte>())}]");
        Assert.Equal(255, pixels[0]);
        Assert.Equal(0, pixels[1]);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void Geometry_MissingSource_Logs()
    {
        var logs = new List<string>();
        var node = new GeometryNode("几何", new Dictionary<string, string> { ["source"] = "没有这个节点" }) { Log = logs.Add };
        using var src = TwoToneRow();
        var ctx = new PipelineRunContext(src);
        var result = node.Run(src, ctx);
        Assert.Null(result.OutputImage);
        Assert.Contains(logs, l => l.Contains("[Geometry]") && l.Contains("图像来源未找到"));
    }

    // ===== ImageSource（文件模式） =====

    [Fact]
    public void ImageSource_SingleFile_LoadsImage()
    {
        Directory.CreateDirectory(_tempDir);
        using (var mat = new Mat(2, 4, MatType.CV_8UC3, new Scalar(5, 6, 7)))
        {
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, "one.png"), mat));
        }

        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["dir"] = "", ["path"] = Path.Combine(_tempDir, "one.png") });
        using var ctxInput = new Mat(1, 1, MatType.CV_8UC3);
        var ctx = new PipelineRunContext(ctxInput);
        var result = node.Run(ctxInput, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.Equal(4, result.OutputImage!.Width);
        Assert.Equal("one.png", result.Values["current_file"]);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void ImageSource_FolderSequence_AdvancesAndLoops()
    {
        Directory.CreateDirectory(_tempDir);
        using (var a = new Mat(2, 4, MatType.CV_8UC3, new Scalar(0, 0, 0)))
        {
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, "a.png"), a));
        }
        using (var b = new Mat(4, 2, MatType.CV_8UC3, new Scalar(0, 0, 0)))
        {
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, "b.png"), b));
        }

        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["dir"] = _tempDir, ["path"] = "", ["loop"] = "true" });
        using var ctxInput = new Mat(1, 1, MatType.CV_8UC3);
        var ctx = new PipelineRunContext(ctxInput);

        var r1 = node.Run(ctxInput, ctx);
        Assert.Equal("a.png", r1.Values["current_file"]);
        Assert.Equal(4, r1.OutputImage!.Width);
        r1.OutputImage.Dispose();

        var r2 = node.Run(ctxInput, ctx);
        Assert.Equal("b.png", r2.Values["current_file"]);
        Assert.Equal(2, r2.OutputImage!.Width);
        r2.OutputImage.Dispose();

        var r3 = node.Run(ctxInput, ctx); // loop=true 回到第一张
        Assert.Equal("a.png", r3.Values["current_file"]);
        r3.OutputImage.Dispose();
    }

    [Fact]
    public void ImageSource_MissingFile_Logs()
    {
        var logs = new List<string>();
        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["path"] = Path.Combine(_tempDir, "no_such.png") }) { Log = logs.Add };
        using var ctxInput = new Mat(1, 1, MatType.CV_8UC3);
        var ctx = new PipelineRunContext(ctxInput);
        var result = node.Run(ctxInput, ctx);
        Assert.Null(result.OutputImage);
        Assert.Contains(logs, l => l.Contains("[ImageSource]") && l.Contains("图像文件不存在"));
    }

    // ===== ImageSource（相机模式） =====

    [Fact]
    public void ImageSource_CameraMode_EmptyInput_UsesFrameProvider()
    {
        using var frame = new Mat(3, 5, MatType.CV_8UC3, new Scalar(1, 2, 3));
        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["source_kind"] = "相机" })
        {
            FrameProvider = _ => frame.Clone(),
        };
        using var input = new Mat(); // 空输入 = 单次/连续执行场景
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);
        Assert.NotNull(result.OutputImage);
        Assert.Equal(5, result.OutputImage!.Width);
        Assert.Equal("CAMERA", result.Values["current_file"]);
        result.OutputImage.Dispose();
    }

    [Fact]
    public void ImageSource_CameraMode_NonEmptyInput_PassthroughWithoutProvider()
    {
        var called = false;
        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["source_kind"] = "相机" })
        {
            FrameProvider = _ => { called = true; return null; },
        };
        using var input = new Mat(2, 3, MatType.CV_8UC3, new Scalar(9, 9, 9));
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);
        Assert.False(called); // 有真实帧（生产）时不抓帧
        Assert.True(ReferenceEquals(input, result.OutputImage)); // 直接透传
    }

    [Fact]
    public void ImageSource_CameraMode_NoProvider_Logs()
    {
        var logs = new List<string>();
        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["source_kind"] = "相机" }) { Log = logs.Add };
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);
        Assert.Null(result.OutputImage);
        Assert.Contains(logs, l => l.Contains("[ImageSource]") && l.Contains("相机未连接"));
    }

    [Fact]
    public void ImageSource_CameraMode_ProviderReturnsNull_LogsTimeout()
    {
        var logs = new List<string>();
        var node = new ImageSourceNode("图像源", new Dictionary<string, string> { ["source_kind"] = "相机" })
        {
            FrameProvider = _ => null,
            Log = logs.Add,
        };
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);
        Assert.Null(result.OutputImage);
        Assert.Contains(logs, l => l.Contains("[ImageSource]") && l.Contains("取帧超时"));
    }

    [Fact]
    public void NodeFactory_ImageLoadAlias_CreatesImageSource()
    {
        using var node = NodeFactory.Create("ImageLoad", "旧方案节点");
        Assert.IsType<ImageSourceNode>(node);
        Assert.Equal("ImageSource", node.Type);
    }

    // ===== Pipeline 集成 =====

    [Fact]
    public void Pipeline_BinarizeThenDisplay_ExposesDisplayImage()
    {
        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                new RecipeNode { Name = "01 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "@input", ["threshold"] = "128" } },
                new RecipeNode { Name = "02 显示", Type = "Display", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "01 二值化" } },
            },
        };
        var logs = new List<string>();
        using var pipeline = new Pipeline(recipe, logs.Add);
        using var bgr = new Mat(1, 2, MatType.CV_8UC3, new Scalar(0, 0, 0));
        Cv2.Rectangle(bgr, new Rect(1, 0, 1, 1), new Scalar(255, 255, 255), -1);
        var result = pipeline.Run(bgr, "test");
        try
        {
            Assert.NotNull(result.DisplayImage);
            Assert.Equal(1, result.DisplayImage!.Channels());
            Assert.Equal("01 二值化", result.DisplayNodeName); // Display 为兼容空操作，最后图像节点是二值化
            Assert.Equal("OK", result.Decision);
            Assert.DoesNotContain(logs, l => l.Contains("[校验]"));
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }

    [Fact]
    public void Pipeline_ImageSourceMissingDir_LogsValidation()
    {
        var missingDir = Path.Combine(_tempDir, "no_such_dir");
        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                // Type 用旧名 ImageLoad：覆盖 NodeFactory 别名兼容
                new RecipeNode { Name = "01 读取", Type = "ImageLoad", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = missingDir } },
            },
        };
        var logs = new List<string>();
        using var pipeline = new Pipeline(recipe, logs.Add);
        Assert.Contains(logs, l => l.Contains("[校验]") && l.Contains("图片目录不存在"));
    }

    [Fact]
    public void Pipeline_EmptyInputWithImageSource_RunsChain()
    {
        Directory.CreateDirectory(_tempDir);
        using (var mat = new Mat(2, 4, MatType.CV_8UC3, new Scalar(0, 0, 0)))
        {
            Cv2.Rectangle(mat, new Rect(1, 0, 1, 1), new Scalar(255, 255, 255), -1);
            Assert.True(Cv2.ImWrite(Path.Combine(_tempDir, "one.png"), mat));
        }

        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = _tempDir } },
                new RecipeNode { Name = "02 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["threshold"] = "128" } },
                new RecipeNode { Name = "03 显示", Type = "Display", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "02 二值化" } },
            },
        };
        var logs = new List<string>();
        using var pipeline = new Pipeline(recipe, logs.Add);
        using var input = new Mat(); // 空输入：图像由图像源节点提供
        var result = pipeline.Run(input, "test");
        try
        {
            Assert.Equal("OK", result.Decision);
            Assert.NotNull(result.DisplayImage);
            Assert.Equal(2, result.NodeImages.Count); // 图像源 + 二值化
            Assert.Equal("one.png", result.NodeValues["01 图像源"]["current_file"]);
            Assert.DoesNotContain(logs, l => l.Contains("[校验]"));
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }
}
