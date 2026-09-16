using System.Globalization;
using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Services;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>RoiRect/RoiGeometry 纯逻辑测试 + PatchCore ROI 检测集成测试（模型文件缺失时静默跳过）。</summary>
public class PatchCoreRoiTests
{
    private static readonly string ModelDir =
        @"D:\AiProjects\speaker-inspection\patchcore train\models\foam_patchcore";

    private static bool ModelExists =>
        File.Exists(Path.Combine(ModelDir, "model.onnx"))
        && File.Exists(Path.Combine(ModelDir, "memory_bank.bin"))
        && File.Exists(Path.Combine(ModelDir, "backbone_config.json"));

    // ===== RoiRect 解析/序列化 =====

    [Fact]
    public void Parse_EmptyOrInvalid_ReturnsNull()
    {
        Assert.Null(RoiRect.Parse(null));
        Assert.Null(RoiRect.Parse(""));
        Assert.Null(RoiRect.Parse("0.1,0.2"));
        Assert.Null(RoiRect.Parse("a,b,c,d"));
        Assert.Null(RoiRect.Parse("-0.1,0.2,0.3,0.3"));    // 旧格式负坐标
        Assert.Null(RoiRect.Parse("1.0,1.0,0.5,0.5"));     // 旧格式钳制后零宽高
        Assert.Null(RoiRect.Parse("0.5,0.5,0,0.5,0"));     // 新格式零宽
        Assert.Null(RoiRect.Parse("0.5,0.5,0.2,0.2,abc")); // 角度非法
    }

    [Fact]
    public void Parse_LegacyTopLeft_ConvertsToCenterForm()
    {
        var roi = RoiRect.Parse("0.1,0.25,0.4,0.5");
        Assert.NotNull(roi);
        Assert.Equal(0.3, roi.Value.CenterX, 4);
        Assert.Equal(0.5, roi.Value.CenterY, 4);
        Assert.Equal(0.4, roi.Value.W, 4);
        Assert.Equal(0.5, roi.Value.H, 4);
        Assert.Equal(0, roi.Value.AngleDeg, 4);
    }

    [Fact]
    public void Parse_Serialize_RoundTrip()
    {
        var roi = RoiRect.Parse("0.3,0.5,0.4,0.5,45");
        Assert.NotNull(roi);
        Assert.Equal("0.3,0.5,0.4,0.5,45", roi.Value.Serialize());
        Assert.Equal(45, roi.Value.AngleDeg, 4);
    }

    [Fact]
    public void Parse_NormalizesAngle()
    {
        var roi = RoiRect.Parse("0.5,0.5,0.2,0.2,190");
        Assert.NotNull(roi);
        Assert.Equal(-170, roi.Value.AngleDeg, 4);
        var roi2 = RoiRect.Parse("0.5,0.5,0.2,0.2,-200");
        Assert.NotNull(roi2);
        Assert.Equal(160, roi2.Value.AngleDeg, 4);
    }

    [Fact]
    public void Parse_ClampsOutOfRangeValues()
    {
        var roi = RoiRect.Parse("0.9,0.9,0.5,0.5"); // 旧格式越界部分钳制到 1 边界
        Assert.NotNull(roi);
        Assert.Equal(0.95, roi.Value.CenterX, 4);
        Assert.Equal(0.1, roi.Value.W, 4);
    }

    [Fact]
    public void ToPixels_ClampsRotatedAabbInsideImage()
    {
        // 45° 正方形：外接框大于边长，中心应被钳制使外接框在图像内
        var (cx, cy, w, h, angle) = new RoiRect(0.99, 0.99, 0.5, 0.5, 45).ToPixels(200, 200);
        var a = 45 * Math.PI / 180;
        var aabb = w * Math.Cos(a) + h * Math.Sin(a);
        Assert.InRange(cx, aabb / 2, 200 - aabb / 2);
        Assert.InRange(cy, aabb / 2, 200 - aabb / 2);
        Assert.Equal(45, angle, 4);
        Assert.Equal(100, w); // 0.5 * 200
        Assert.Equal(100, h);
    }

    [Fact]
    public void FromPixelsCenter_RejectsTooSmallRect()
    {
        Assert.Null(RoiRect.FromPixelsCenter(100, 100, 1, 1, 0, 640, 480));
        var roi = RoiRect.FromPixelsCenter(125, 100, 50, 40, 0, 640, 480);
        Assert.NotNull(roi);
        Assert.Equal(125.0 / 640, roi!.Value.CenterX, 6);
    }

    // ===== RoiGeometry 局部系/命中/拖拽 =====

    [Fact]
    public void ToLocal_ToImage_RoundTrip()
    {
        var (lx, ly) = RoiGeometry.ToLocal(90, 60, 50, 50, 30);
        var (x, y) = RoiGeometry.ToImage(lx, ly, 50, 50, 30);
        Assert.Equal(90, x, 6);
        Assert.Equal(60, y, 6);
    }

    [Fact]
    public void HitTest_LocalFrame_FindsHandlesAndBody()
    {
        // 局部矩形 (-50,-40,100,80)，容差 5
        Assert.Equal(RoiHandle.TopLeft, RoiGeometry.HitTest(-50, -40, 100, 80, -50, -40, 5));
        Assert.Equal(RoiHandle.Top, RoiGeometry.HitTest(-50, -40, 100, 80, 0, -40, 5));
        Assert.Equal(RoiHandle.Right, RoiGeometry.HitTest(-50, -40, 100, 80, 50, 0, 5));
        Assert.Equal(RoiHandle.BottomRight, RoiGeometry.HitTest(-50, -40, 100, 80, 50, 40, 5));
        Assert.Equal(RoiHandle.Body, RoiGeometry.HitTest(-50, -40, 100, 80, 0, 0, 5));
        Assert.Equal(RoiHandle.None, RoiGeometry.HitTest(-50, -40, 100, 80, 60, 0, 5));
    }

    [Fact]
    public void ApplyRotatedEdgeDrag_RightHandle_ExpandsAndShiftsCenter()
    {
        // 100×80（局部 -50..50），右手柄拖到局部 x=70：新宽 120，中心偏移 +10
        var (w, h, ocx, ocy) = RoiGeometry.ApplyRotatedEdgeDrag(RoiHandle.Right, 100, 80, 70, 0, 2, 400);
        Assert.Equal(120, w, 4);
        Assert.Equal(80, h, 4);
        Assert.Equal(10, ocx, 4);
        Assert.Equal(0, ocy, 4);
    }

    [Fact]
    public void ApplyRotatedEdgeDrag_CornerDrag_ResizesBothAxes()
    {
        // 100×80，左上手柄拖到局部 (-70,-60)：新尺寸 120×100，中心偏移 (-10,-10)
        var (w, h, ocx, ocy) = RoiGeometry.ApplyRotatedEdgeDrag(RoiHandle.TopLeft, 100, 80, -70, -60, 2, 400);
        Assert.Equal(120, w, 4);
        Assert.Equal(100, h, 4);
        Assert.Equal(-10, ocx, 4);
        Assert.Equal(-10, ocy, 4);
    }

    [Fact]
    public void ApplyRotatedEdgeDrag_ClampsMinAndMax()
    {
        // 左手柄越过右缘：钳制到最小尺寸 2
        var (w, _, ocx, _) = RoiGeometry.ApplyRotatedEdgeDrag(RoiHandle.Left, 100, 80, 90, 0, 2, 400);
        Assert.Equal(2, w, 4);
        Assert.Equal(49, ocx, 4);

        // 拖出最大范围：钳制到 maxSize
        var (w2, _, _, _) = RoiGeometry.ApplyRotatedEdgeDrag(RoiHandle.Right, 100, 80, 500, 0, 2, 200);
        Assert.Equal(200, w2, 4);
    }

    [Fact]
    public void UniformFit_LetterboxMath()
    {
        // 640x480 图放进 640x320 显示区：scale=320/480，水平留白
        var (scale, ox, oy) = RoiGeometry.UniformFit(640, 480, 640, 320);
        Assert.Equal(320.0 / 480, scale, 4);
        Assert.Equal(0, oy, 4);
        Assert.True(ox > 0);

        // 图像中心经显示坐标往返应还原
        var display = (ox + 320 * scale, oy + 240 * scale);
        var (cx, cy) = RoiGeometry.DisplayToImage(display.Item1, display.Item2, 640, 480, 640, 320);
        Assert.Equal(320, cx, 2);
        Assert.Equal(240, cy, 2);
    }

    // ===== PatchCore ROI 检测（真实模型集成）=====

    [Fact]
    public void PatchCoreNode_WithRoi_DetectsCropAndSaves()
    {
        if (!ModelExists) return;

        var cropDir = Path.Combine(Path.GetTempPath(), $"roi_crop_{Guid.NewGuid():N}");
        try
        {
            using var img = new Mat(320, 320, MatType.CV_8UC3, Scalar.All(60));
            Cv2.Circle(img, 240, 240, 40, new Scalar(0, 0, 255), -1); // 右下角放一个红色缺陷区
            using var input = new Mat();
            var ctx = new PipelineRunContext(input);
            ctx.Images["01 图像源"] = img;

            using var node = new PatchCoreNode("04 PatchCore", new Dictionary<string, string>
            {
                ["model_dir"] = ModelDir,
                ["source"] = "01 图像源",
                ["roi"] = "0.5,0.5,0.5,0.5", // 旧格式：左下区域（含缺陷）
                ["crop_dir"] = cropDir,
            });
            node.EnsureLoaded(Path.GetTempPath());

            var nr = node.Run(input, ctx);
            Assert.True(nr.Values.ContainsKey("score"));
            Assert.Contains(nr.Decision, new[] { "OK", "NG" });

            // 输出图与输入同尺寸（合成图回贴原图）
            Assert.NotNull(nr.OutputImage);
            Assert.Equal(320, nr.OutputImage!.Width);
            Assert.Equal(320, nr.OutputImage.Height);

            // 切图已按判定分目录落盘（*.jpg，不含 sidecar .json）
            var saved = Directory.EnumerateFiles(Path.Combine(cropDir, nr.Decision), "*.jpg").ToList();
            Assert.Single(saved);

            // ROI 分数与全图分数不同（喂给模型的图不同）
            using var fullNode = new PatchCoreNode("04 PatchCore 全图", new Dictionary<string, string>
            {
                ["model_dir"] = ModelDir,
                ["source"] = "01 图像源",
            });
            fullNode.EnsureLoaded(Path.GetTempPath());
            var nrFull = fullNode.Run(input, ctx);
            Assert.NotEqual(
                double.Parse(nr.Values["score"], CultureInfo.InvariantCulture),
                double.Parse(nrFull.Values["score"], CultureInfo.InvariantCulture));
        }
        finally
        {
            try { Directory.Delete(cropDir, true); } catch { /* 清理失败不影响测试 */ }
        }
    }

    [Fact]
    public void PatchCoreNode_WithRotatedRoi_DetectsWarpedCrop()
    {
        if (!ModelExists) return;

        using var img = new Mat(320, 320, MatType.CV_8UC3, Scalar.All(60));
        Cv2.Circle(img, 240, 240, 40, new Scalar(0, 0, 255), -1);
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = img;

        using var node = new PatchCoreNode("04 PatchCore", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
            ["roi"] = "0.75,0.75,0.4,0.4,30", // 新格式：中心 + 30° 旋转
        });
        node.EnsureLoaded(Path.GetTempPath());

        var nr = node.Run(input, ctx);
        Assert.True(nr.Values.ContainsKey("score"));
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(320, nr.OutputImage!.Width);
        Assert.Equal(320, nr.OutputImage.Height);
    }

    [Fact]
    public void PatchCoreNode_WithGrayBinarizedInput_ConvertsToBgr()
    {
        if (!ModelExists) return;

        // 模拟上游二值化节点：单通道二值图直接喂给 PatchCore
        using var gray = new Mat(320, 320, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(gray, 160, 160, 60, Scalar.All(255), -1);
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = gray;

        using var node = new PatchCoreNode("05 PatchCore", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
        });
        node.EnsureLoaded(Path.GetTempPath());

        var nr = node.Run(input, ctx); // 之前在热力合成 AddWeighted(1ch, 3ch) 处抛异常
        Assert.True(nr.Values.ContainsKey("score"));
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(3, nr.OutputImage!.Channels());
    }

    [Fact]
    public void PatchCoreNode_WithInvalidRoi_FallsBackToFullImage()
    {
        if (!ModelExists) return;

        using var img = new Mat(320, 320, MatType.CV_8UC3, Scalar.All(128));
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = img;

        using var node = new PatchCoreNode("04 PatchCore", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
            ["roi"] = "garbage",
        });
        node.EnsureLoaded(Path.GetTempPath());
        var nr = node.Run(input, ctx);
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(320, nr.OutputImage!.Width);
    }

    /// <summary>Phase 1 良品切图采集：用部署真实链路（几何/二值化/ROI）对 P7 良品图切图，已采集够则跳过。</summary>
    [Fact]
    public void ExpCut_GoodCrops_FromP7()
    {
        var recipePath = @"D:\AiProjects\speaker-inspection\VisionInspection\bin\Debug\net8.0-windows\recipe.json";
        if (!File.Exists(recipePath)) return;
        var srcDir = @"C:\Users\feng\Desktop\目标\数据\B7-02目标检测\图片数据\03-16\P7";
        if (!Directory.Exists(srcDir)) return;
        var cropDir = @"C:\Users\feng\Desktop\扬声器\良品切图";
        var cropDirOk = Path.Combine(cropDir, "OK");
        if (Directory.Exists(cropDirOk) && Directory.EnumerateFiles(cropDirOk, "*.jpg").Count() >= 50) return; // 已采集

        var recipe = RecipeStore.LoadFile(recipePath);
        recipe.BaseDir = Path.GetDirectoryName(recipePath)!;
        recipe.Nodes.RemoveAll(n => n.Type == "Decision"); // 切图不需要判定节点
        foreach (var n in recipe.Nodes)
        {
            if (n.Type == "PatchCore")
            {
                n.Params["crop_dir"] = cropDir;
                n.Params["threshold"] = "999"; // 全部判 OK → 切图进 OK 子目录
            }
        }
        using var pipeline = new Pipeline(recipe, _ => { });

        var files = Directory.EnumerateFiles(srcDir)
            .Where(f => new[] { ".jpg", ".png", ".bmp" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.True(files.Length > 0, "P7 目录没有图片");

        for (var pass = 0; pass < 5; pass++)
        {
            foreach (var file in files)
            {
                using var bgr = ImagePreprocessService.LoadBgr(file);
                var overrides = new Dictionary<string, Mat> { [recipe.Nodes[0].Name] = bgr };
                var result = pipeline.Run(bgr, Path.GetFileName(file), sourceOverrides: overrides);
                foreach (var m in result.NodeImages.Values) m.Dispose();
            }
        }
    }

    /// <summary>同图对比：部署端 C# 对产线模型良品切图打分，结果写临时文件供与 Python 对照。</summary>
    [Fact]
    public void ParityCheck_ModelsOK_OnSavedNgImage()
    {
        var modelDir = @"D:\AiProjects\speaker-inspection\patchcore train\models\产线模型";
        if (!File.Exists(Path.Combine(modelDir, "model.onnx"))) return; // 模型缺失时跳过
        var ngDir = @"C:\Users\feng\Desktop\扬声器\良品切图\OK";
        if (!Directory.Exists(ngDir)) return;

        var files = Directory.EnumerateFiles(ngDir)
            .Where(f => new[] { ".jpg", ".png", ".bmp" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return;

        using var node = new PatchCoreNode("parity", new Dictionary<string, string>
        {
            ["model_dir"] = modelDir,
            ["source"] = "01 图像源",
        });
        node.EnsureLoaded(Path.GetTempPath());

        var lines = new List<string>();
        using var input = new Mat();
        foreach (var file in files)
        {
            using var bgr = ImagePreprocessService.LoadBgr(file);
            var ctx = new PipelineRunContext(input);
            ctx.Images["01 图像源"] = bgr;
            var nr = node.Run(input, ctx);
            lines.Add($"{Path.GetFileName(file)} size={bgr.Width}x{bgr.Height} score={nr.Values.GetValueOrDefault("score")} decision={nr.Decision}");
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "parity_modelsOK.txt"), lines);
    }

    /// <summary>多 ROI（本节点私有检测项）：每 ROI 独立检测，score=最大值，Values 暴露每个 ROI 分数。</summary>
    [Fact]
    public void PatchCoreNode_MultiRoi_OwnRois_MaxScoreAndPerRoiValues()
    {
        if (!ModelExists) return;

        using var img = new Mat(320, 320, MatType.CV_8UC3, Scalar.All(60));
        Cv2.Circle(img, 240, 240, 40, new Scalar(0, 0, 255), -1); // 右下角缺陷
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = img;

        using var node = new PatchCoreNode("05 PatchCore", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
            ["own_rois"] = NodeRois.SerializeOwn(new List<(string, RoiRect)>
            {
                ("左上", new RoiRect(0.25, 0.25, 0.25, 0.25, 0)),
                ("右下", new RoiRect(0.75, 0.75, 0.3, 0.3, 0)),
            }),
        });
        node.EnsureLoaded(Path.GetTempPath());

        var nr = node.Run(input, ctx);
        Assert.True(nr.Values.ContainsKey("roi_左上"));
        Assert.True(nr.Values.ContainsKey("roi_右下"));
        var max = Math.Max(
            double.Parse(nr.Values["roi_左上"], CultureInfo.InvariantCulture),
            double.Parse(nr.Values["roi_右下"], CultureInfo.InvariantCulture));
        Assert.Equal(max, double.Parse(nr.Values["score"], CultureInfo.InvariantCulture), 4);
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(320, nr.OutputImage!.Width);
    }

    [Fact]
    public void SetParam_NonModelDir_DoesNotDisposeRuntime()
    {
        if (!ModelExists) return;

        using var img = new Mat(320, 320, MatType.CV_8UC3, Scalar.All(128));
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = img;

        using var node = new PatchCoreNode("04 PatchCore", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
        });
        node.EnsureLoaded(Path.GetTempPath());

        // 改非 model_dir 参数后不重新 EnsureLoaded 也能直接 Run —— 证明运行时未销毁
        node.SetParam("roi", "0.3,0.5,0.2,0.2,15");
        node.SetParam("threshold", "2.0");
        node.SetParam("display_image", "检测图");
        var nr = node.Run(input, ctx);
        Assert.NotNull(nr.OutputImage);
    }
}
