using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>YoloPostprocess 纯逻辑单测 + YoloNode 真实 ONNX 冒烟测试（模型缺失时静默跳过）。</summary>
public class YoloNodeTests
{
    private static readonly string ModelDir = Environment.GetEnvironmentVariable("VISION_INSPECTION_YOLO_MODEL_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "test-data", "yolo");
    private static bool ModelExists =>
        File.Exists(Path.Combine(ModelDir, "best.onnx")) && File.Exists(Path.Combine(ModelDir, "classes.txt"));

    // ===== 后处理纯逻辑 =====

    [Fact]
    public void LetterboxFit_ScaleAndPadding()
    {
        // 100×50 → 640×640：scale=6.4，垂直居中留白
        var fit = YoloPostprocess.LetterboxFit(100, 50, 640, 640);
        Assert.Equal(6.4, fit.Scale, 4);
        Assert.Equal(0, fit.Dx, 4);
        Assert.Equal((640 - 50 * 6.4) / 2, fit.Dy, 4);

        // 模型坐标 → 源图坐标往返
        var mx = fit.Dx + 50 * fit.Scale;
        var my = fit.Dy + 25 * fit.Scale;
        var mapped = YoloPostprocess.MapToSource(mx, my, 0, 0, fit.Scale, fit.Dx, fit.Dy);
        Assert.Equal(50, mapped.Item1, 4);
        Assert.Equal(25, mapped.Item2, 4);
    }

    [Fact]
    public void ParseAndNms_FiltersLowConfAndSuppressesOverlap()
    {
        // 2 通道 = 4 框坐标行已被省略的简化：这里手工构造 [1, 5, 3]（1 类，nc=1）
        // 列: (cx,cy,w,h,cls)；3 个候选：A(0.9)、B(0.8, 与 A 重叠→被 NMS)、C(0.7, 不重叠)
        var data = new float[5 * 3];
        void Set(int col, float cx, float cy, float w, float h, float conf)
        {
            data[0 * 3 + col] = cx;
            data[1 * 3 + col] = cy;
            data[2 * 3 + col] = w;
            data[3 * 3 + col] = h;
            data[4 * 3 + col] = conf;
        }
        Set(0, 100, 100, 50, 50, 0.9f);
        Set(1, 102, 102, 50, 50, 0.8f);  // 与 A IoU≈0.83 > 0.45 → 被抑制
        Set(2, 300, 300, 50, 50, 0.7f);

        var kept = YoloPostprocess.ParseAndNms(data, 5, 3, 0.25f, 0.45f);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0.9f, kept[0].Conf);
        Assert.Equal(0.7f, kept[1].Conf);
    }

    [Fact]
    public void ParseAndNms_RespectsConfThreshold()
    {
        var data = new float[5 * 1];
        data[0] = 100; data[1] = 100; data[2] = 50; data[3] = 50; data[4] = 0.1f;
        Assert.Empty(YoloPostprocess.ParseAndNms(data, 5, 1, 0.25f, 0.45f));
    }

    [Fact]
    public void Iou_IdenticalBoxes_IsOne()
    {
        Assert.Equal(1.0, YoloPostprocess.Iou(10, 10, 20, 20, 10, 10, 20, 20), 6);
        Assert.Equal(0.0, YoloPostprocess.Iou(0, 0, 10, 10, 100, 100, 10, 10), 6);
    }

    // ===== 判定模式 =====

    [Fact]
    public void ComputeDecision_DefaultMode_DetectMeansNg()
    {
        // 检出即NG（默认，缺陷检测）：检出>0 → NG；未检出 → OK；未知模式回退默认
        Assert.Equal("NG", YoloNode.ComputeDecision(YoloNode.DecisionDetectNg, 1, hasPositiveFilter: true));
        Assert.Equal("OK", YoloNode.ComputeDecision(YoloNode.DecisionDetectNg, 0, hasPositiveFilter: true));
        Assert.Equal("NG", YoloNode.ComputeDecision(null, 1, hasPositiveFilter: false));
        Assert.Equal("OK", YoloNode.ComputeDecision("", 0, hasPositiveFilter: false));
        Assert.Equal("NG", YoloNode.ComputeDecision("未知模式", 2, hasPositiveFilter: true));
    }

    [Fact]
    public void ComputeDecision_MissingMode_MissingMeansNg()
    {
        // 缺失即NG（缺料/漏装检测）：一个都没检出 → NG；检出 → OK
        Assert.Equal("NG", YoloNode.ComputeDecision(YoloNode.DecisionMissingNg, 0, hasPositiveFilter: true));
        Assert.Equal("OK", YoloNode.ComputeDecision(YoloNode.DecisionMissingNg, 1, hasPositiveFilter: true));
        Assert.Equal("OK", YoloNode.ComputeDecision(YoloNode.DecisionMissingNg, 3, hasPositiveFilter: false));
        // 模式值带空白也能识别
        Assert.Equal("OK", YoloNode.ComputeDecision(" 缺失即NG ", 1, hasPositiveFilter: true));
    }

    [Fact]
    public void NodeParams_UseRoiAndOutputNodeForWholeImageSaving()
    {
        var save = Assert.Single(YoloNode.StaticParamDefs, p => p.Key == "save_mode");
        Assert.Equal("检测框切图保存类型", save.Label);
        Assert.Equal(new[] { "全部", "仅OK", "仅NG", "不保存" }, save.Choices);
        Assert.Contains(YoloNode.StaticParamDefs, p => p.Key == "crop_dir");
    }

    // ===== YoloNode 冒烟（真实 ONNX）=====

    [Fact]
    public void YoloNode_Runs_InferencePipeline()
    {
        if (!ModelExists) return;

        using var img = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(200, 150, 220, 180), new Scalar(0, 200, 255), -1);
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = img;

        using var node = new YoloNode("06 YOLO 检测", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
            ["conf"] = "0.25",
        });
        node.EnsureLoaded(Path.GetTempPath());

        var nr = node.Run(input, ctx);
        Assert.True(nr.Values.ContainsKey("count"));
        Assert.True(nr.Values.ContainsKey("det_all"));
        Assert.Contains(nr.Decision, new[] { "OK", "NG" });
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(640, nr.OutputImage!.Width);
    }
}
