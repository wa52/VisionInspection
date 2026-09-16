using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>YoloSegPostprocess 纯逻辑单测 + SegNode 真实 ONNX 冒烟测试（模型缺失时静默跳过）。</summary>
public class SegNodeTests
{
    private static readonly string ModelDir = Environment.GetEnvironmentVariable("VISION_INSPECTION_SEG_MODEL_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "test-data", "seg");
    private static bool ModelExists =>
        File.Exists(Path.Combine(ModelDir, "best.onnx")) && File.Exists(Path.Combine(ModelDir, "classes.txt"));

    // ===== 后处理纯逻辑 =====

    /// <summary>构造 output0 展平数据 [channels, count]（行主序 data[c*n+i]）。</summary>
    private static float[] BuildDetData(int count, int numClasses, int numCoeffs,
        (int Anchor, double Cx, double Cy, double W, double H, int Cls, float Conf, float Coef0)[] anchors)
    {
        var channels = 4 + numClasses + numCoeffs;
        var data = new float[channels * count];
        foreach (var a in anchors)
        {
            data[a.Anchor] = (float)a.Cx;
            data[count + a.Anchor] = (float)a.Cy;
            data[2 * count + a.Anchor] = (float)a.W;
            data[3 * count + a.Anchor] = (float)a.H;
            data[(4 + a.Cls) * count + a.Anchor] = a.Conf;
            data[(4 + numClasses) * count + a.Anchor] = a.Coef0;
        }
        return data;
    }

    [Fact]
    public void ParseSegAndNms_FiltersConf_SuppressesOverlap_ExtractsCoeffs()
    {
        // 3 个 anchor：0/1 同类重叠（NMS 抑制低分），2 低于置信度
        var data = BuildDetData(3, 1, 32,
        [
            (0, 10, 10, 20, 20, 0, 0.9f, 1.5f),
            (1, 11, 11, 20, 20, 0, 0.8f, 2.5f),
            (2, 200, 200, 20, 20, 0, 0.2f, 3.0f),
        ]);
        var kept = YoloSegPostprocess.ParseSegAndNms(data, 37, 3, 1, 32, 0.25f, 0.45f);

        var det = Assert.Single(kept);
        Assert.Equal(0, det.ClassIndex);
        Assert.Equal(0.9f, det.Conf, 5);
        Assert.Equal(0, det.AnchorIndex);
        Assert.Equal(1.5f, det.Coeffs[0], 5);
        Assert.Equal(10, det.Cx, 1);
        Assert.Equal(20, det.W, 1);
    }

    [Fact]
    public void ParseSegAndNms_DifferentClasses_BothKept()
    {
        var data = BuildDetData(2, 2, 32,
        [
            (0, 10, 10, 20, 20, 0, 0.9f, 1.0f),
            (1, 12, 12, 20, 20, 1, 0.8f, 2.0f),
        ]);
        var kept = YoloSegPostprocess.ParseSegAndNms(data, 38, 2, 2, 32, 0.25f, 0.45f);

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, d => d.ClassIndex == 0 && d.Coeffs[0] == 1.0f);
        Assert.Contains(kept, d => d.ClassIndex == 1 && d.Coeffs[0] == 2.0f);
    }

    [Fact]
    public void ComposeMaskLogits_WeightedSumOverProtoChannels()
    {
        // proto: k=0 → [1,2,3,4]；k=1 → [10,0,0,0]；coeffs=[2,1] → logits=[12,4,6,8]
        var proto = new float[] { 1, 2, 3, 4, 10, 0, 0, 0 };
        var logits = YoloSegPostprocess.ComposeMaskLogits([2f, 1f], proto, protoH: 2, protoW: 2);

        Assert.Equal(new float[] { 12, 4, 6, 8 }, logits);
    }

    [Fact]
    public void ComposeMaskLogits_ZeroCoeffSkipped_LayoutRowMajor()
    {
        // proto: k=0 → [1,0,0,0]；k=1 → [0,0,0,1]；coeffs=[0,3] → 只有 k=1 生效，[1,0] 位置应为 0
        var proto = new float[] { 1, 0, 0, 0, 0, 0, 0, 1 };
        var logits = YoloSegPostprocess.ComposeMaskLogits([0f, 3f], proto, protoH: 2, protoW: 2);

        Assert.Equal(new float[] { 0, 0, 0, 3 }, logits);
    }

    // ===== 判定边界 =====

    [Fact]
    public void ComputeDecision_RatioThresholdBoundary()
    {
        // 检测项级阈值：任一启用且参与判定的检测项 占比 ≥ 该项阈值 → NG
        Assert.Equal("NG", SegNode.ComputeDecision(SegNode.DecisionRatioNg, new List<(double, double)> { (0.5, 0.5) }, 3));   // 等于阈值即 NG
        Assert.Equal("NG", SegNode.ComputeDecision(SegNode.DecisionRatioNg, new List<(double, double)> { (1.234, 0.5) }, 0)); // 无实例但占比超限 → NG
        Assert.Equal("OK", SegNode.ComputeDecision(SegNode.DecisionRatioNg, new List<(double, double)> { (0.499, 0.5) }, 1));
        Assert.Equal("NG", SegNode.ComputeDecision(SegNode.DecisionRatioNg, new List<(double, double)> { (0.0, 0.0) }, 0));   // 阈值 0 → NG
        Assert.Equal("NG", SegNode.ComputeDecision(null, new List<(double, double)> { (1.0, 0.5) }, 0)); // 未配置模式 → 按占比
        Assert.Equal("OK", SegNode.ComputeDecision(SegNode.DecisionRatioNg, new List<(double, double)> { (9.9, 0.5), (0.1, 0.2) }.Where(r => r.Item1 < 1).ToList(), 0)); // 全部低于各自阈值 → OK
    }

    [Fact]
    public void ComputeDecision_DetectAndMissingModes()
    {
        Assert.Equal("NG", SegNode.ComputeDecision(SegNode.DecisionDetectNg, new List<(double, double)>(), 1));   // 检出即NG
        Assert.Equal("OK", SegNode.ComputeDecision(SegNode.DecisionDetectNg, new List<(double, double)> { (9.9, 0.5) }, 0));
        Assert.Equal("NG", SegNode.ComputeDecision(SegNode.DecisionMissingNg, new List<(double, double)>(), 0));  // 缺失即NG
        Assert.Equal("OK", SegNode.ComputeDecision(SegNode.DecisionMissingNg, new List<(double, double)> { (9.9, 0.5) }, 2));
    }

    [Fact]
    public void NodeParams_UseRoiAndOutputNodeForWholeImageSaving()
    {
        var save = Assert.Single(SegNode.StaticParamDefs, p => p.Key == "save_mode");
        Assert.Equal("缺陷区切图保存类型", save.Label);
        Assert.Equal(new[] { "全部", "仅OK", "仅NG", "不保存" }, save.Choices);
        Assert.Contains(SegNode.StaticParamDefs, p => p.Key == "crop_dir");
    }

    // ===== SegNode 冒烟（真实 ONNX）=====

    [Fact]
    public void SegNode_Runs_InferencePipeline()
    {
        if (!ModelExists) return;

        using var img = new Mat(240, 320, MatType.CV_8UC3, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(100, 80, 120, 90), new Scalar(0, 200, 255), -1);
        using var input = new Mat();
        var ctx = new PipelineRunContext(input);
        ctx.Images["01 图像源"] = img;

        using var node = new SegNode("06 实例分割", new Dictionary<string, string>
        {
            ["model_dir"] = ModelDir,
            ["source"] = "01 图像源",
            ["conf"] = "0.1",
            ["percent"] = "0.1",
        });
        node.EnsureLoaded(Path.GetTempPath());

        var nr = node.Run(input, ctx);
        Assert.Contains(nr.Decision, new[] { "OK", "NG" });
        Assert.True(nr.Values.ContainsKey("max_ratio"));
        Assert.True(nr.Values.ContainsKey("defect_pixels"));
        Assert.True(nr.Values.ContainsKey("instances"));
        Assert.True(nr.Values.ContainsKey("count"));
        Assert.True(nr.Values.ContainsKey("classes"));
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(320, nr.OutputImage!.Width);
    }
}
