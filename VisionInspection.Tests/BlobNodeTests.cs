using System.Globalization;
using System.IO;
using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using VisionInspection.Services;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>Blob 分析（BlobNode，传统路线）测试：二值化/极性/双阈值/连通性/孔洞填充/特征筛选/排序/
/// 判定纯函数 / 合成图端到端（检测项判定/停用/仅观察/总范围/show_binary/输出值）/ 工厂与方案往返。</summary>
public class BlobNodeTests
{
    // ===== 二值化 =====

    [Fact]
    public void Binarize_Fixed_BrightOnDark()
    {
        using var gray = TwoLevelGray(30, 200);
        using var bin = BlobAnalyzer.Binarize(gray, BlobAnalyzer.ThFixed, brightOnDark: true, 100, 150);
        Assert.Equal(255, bin.Get<byte>(0, 0)); // 200 > 100 → 前景（亮斑）
        Assert.Equal(0, bin.Get<byte>(1, 1));   // 30 → 背景
    }

    [Fact]
    public void Binarize_Fixed_DarkOnBright()
    {
        using var gray = TwoLevelGray(30, 200);
        using var bin = BlobAnalyzer.Binarize(gray, BlobAnalyzer.ThFixed, brightOnDark: false, 100, 150);
        Assert.Equal(255, bin.Get<byte>(1, 1)); // 30 < 100 → 前景（暗斑）
        Assert.Equal(0, bin.Get<byte>(0, 0));
    }

    [Fact]
    public void Binarize_Double_KeepsRange_InvertsForDark()
    {
        using var gray = ThreeLevelGray(20, 120, 240);
        using var bright = BlobAnalyzer.Binarize(gray, BlobAnalyzer.ThDouble, brightOnDark: true, 100, 150);
        Assert.Equal(0, bright.Get<byte>(0, 0));  // 20 区间外
        Assert.Equal(255, bright.Get<byte>(0, 1)); // 120 ∈ [100,150]
        Assert.Equal(0, bright.Get<byte>(0, 2));

        using var dark = BlobAnalyzer.Binarize(gray, BlobAnalyzer.ThDouble, brightOnDark: false, 100, 150);
        Assert.Equal(255, dark.Get<byte>(0, 0));  // 区间外 = 暗斑
        Assert.Equal(0, dark.Get<byte>(0, 1));
        Assert.Equal(255, dark.Get<byte>(0, 2));
    }

    [Fact]
    public void Binarize_None_TreatsNonZeroAsForeground()
    {
        using var gray = TwoLevelGray(0, 255);
        using var bin = BlobAnalyzer.Binarize(gray, BlobAnalyzer.ThNone, brightOnDark: true, 0, 0);
        Assert.Equal(0, bin.Get<byte>(1, 1));
        Assert.Equal(255, bin.Get<byte>(0, 0));
    }

    // ===== 连通性 =====

    [Fact]
    public void Analyze_Connectivity8_MergesDiagonal_4_Splits()
    {
        using var gray = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(0));
        gray.Set(0, 0, 255);
        gray.Set(1, 1, 255);
        using var bin = gray.Clone();
        var filter = new BlobFilter(ByArea: true, MinArea: 1, MaxArea: 999999999);

        var c8 = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, filter, BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        var c4 = BlobAnalyzer.Analyze(gray, bin, 4, 0, 0, filter, BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Single(c8); // 对角接触 8 邻接算一个
        Assert.Equal(2, c4.Count); // 4 邻接分成两个
    }

    // ===== 孔洞填充 =====

    [Fact]
    public void Analyze_FillHoles_FillsOnlyBelowThreshold()
    {
        using var bin = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(bin, new Rect(5, 5, 30, 30), Scalar.All(255), -1);      // 900
        Cv2.Rectangle(bin, new Rect(15, 15, 10, 10), Scalar.All(0), -1);      // 100 孔
        using var gray = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(5, 5, 30, 30), Scalar.All(255), -1);

        var open = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, new BlobFilter(ByArea: false), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        var filled = BlobAnalyzer.Analyze(gray, bin, 8, 100, 0, new BlobFilter(ByArea: false), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        var bigOnly = BlobAnalyzer.Analyze(gray, bin, 8, 50, 0, new BlobFilter(ByArea: false), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);

        var b = Assert.Single(open);
        Assert.InRange(b.Area, 795, 805); // 900 - 100
        var f = Assert.Single(filled);
        Assert.InRange(f.Area, 895, 905); // 孔被填满
        var half = Assert.Single(bigOnly);
        Assert.InRange(half.Area, 795, 805); // 100 > 50 不填
    }

    // ===== 特征与筛选 =====

    [Fact]
    public void Analyze_Features_SquareAndCircle()
    {
        using var gray = new Mat(60, 120, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(10, 10, 20, 20), Scalar.All(255), -1); // 方形
        Cv2.Circle(gray, 90, 20, 15, Scalar.All(255), -1);                  // 圆形
        using var bin = gray.Clone();

        var blobs = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, new BlobFilter(ByArea: false), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Equal(2, blobs.Count);
        var square = blobs.First(b => Math.Abs(b.CentroidX - 20) < 1 && Math.Abs(b.CentroidY - 20) < 1);
        var circle = blobs.First(b => Math.Abs(b.CentroidX - 90) < 1);

        Assert.InRange(square.Rectangularity, 0.95, 1.0);   // 方形矩形度 ≈ 1
        Assert.InRange(square.Circularity, 0.8, 0.95);      // 方形圆度 ≈ 0.87（离散周长 76）
        Assert.InRange(circle.Circularity, 0.9, 1.0);       // 圆形圆度 ≈ 0.91
        Assert.InRange(square.Area, 395, 405);
        Assert.InRange(square.LongAxis, 18.5, 20.5); // 轮廓像素中心跨度（=像素范围-1）
        Assert.InRange(square.ShortAxis, 18.5, 20.5);
        Assert.True(square.MaxGray >= 250);
    }

    [Fact]
    public void Analyze_Filters_SelectByCircularityAndRect()
    {
        using var gray = new Mat(60, 120, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(10, 10, 20, 20), Scalar.All(255), -1);
        Cv2.Circle(gray, 90, 20, 15, Scalar.All(255), -1);
        using var bin = gray.Clone();

        var roundOnly = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0,
            new BlobFilter(ByArea: false, ByCircularity: true, MinCircularity: 0.9), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Single(roundOnly); // 圆(0.91)过，方(0.87)被筛掉

        var rectOnly = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0,
            new BlobFilter(ByArea: false, ByRectangularity: true, MinRectangularity: 0.95), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Single(rectOnly); // 方过，圆被筛掉
    }

    [Fact]
    public void Analyze_AxisFilter_KeepsElongated()
    {
        using var gray = new Mat(40, 80, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(5, 5, 30, 10), Scalar.All(255), -1); // 30×10 长条
        Cv2.Rectangle(gray, new Rect(50, 20, 10, 10), Scalar.All(255), -1); // 10×10 方块
        using var bin = gray.Clone();

        var longOnly = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0,
            new BlobFilter(ByArea: false, ByLongAxis: true, MinLongAxis: 20), BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Single(longOnly);
        Assert.InRange(longOnly[0].LongAxis, 29, 31);
        Assert.InRange(longOnly[0].ShortAxis, 9, 11);
    }

    [Fact]
    public void Analyze_SortAndFindNum()
    {
        using var gray = new Mat(90, 60, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(10, 10, 10, 10), Scalar.All(255), -1); // 100
        Cv2.Rectangle(gray, new Rect(10, 40, 7, 7), Scalar.All(255), -1);   // 49
        Cv2.Rectangle(gray, new Rect(10, 70, 5, 5), Scalar.All(255), -1);   // 25
        using var bin = gray.Clone();
        var filter = new BlobFilter(ByArea: false);

        var desc = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, filter, BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Equal(new List<double> { 100, 49, 25 }, desc.Select(b => b.Area).ToList());
        var asc = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, filter, BlobAnalyzer.SortArea, BlobAnalyzer.SortAsc);
        Assert.Equal(new List<double> { 25, 49, 100 }, asc.Select(b => b.Area).ToList());
        var none = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, filter, BlobAnalyzer.SortArea, BlobAnalyzer.SortNone);
        Assert.Equal(new List<double> { 100, 49, 25 }, none.Select(b => b.Area).ToList()); // 不排序=标记顺序（自上而下）
        var limited = BlobAnalyzer.Analyze(gray, bin, 8, 0, 2, filter, BlobAnalyzer.SortArea, BlobAnalyzer.SortDesc);
        Assert.Equal(2, limited.Count); // find_num=2 截取前 2 个
    }

    [Fact]
    public void Analyze_SortByCentroid()
    {
        using var gray = new Mat(40, 80, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(50, 5, 10, 10), Scalar.All(255), -1);  // x=55
        Cv2.Rectangle(gray, new Rect(10, 25, 10, 10), Scalar.All(255), -1); // x=15
        using var bin = gray.Clone();
        var blobs = BlobAnalyzer.Analyze(gray, bin, 8, 0, 0, new BlobFilter(ByArea: false), BlobAnalyzer.SortCentroidX, BlobAnalyzer.SortAsc);
        Assert.True(blobs[0].CentroidX < blobs[1].CentroidX);
    }

    // ===== 判定纯函数 =====

    private static BlobNode.RoiResult Item(string name, int count, double maxArea, RoiMeta? meta = null)
    {
        var blobs = Enumerable.Range(0, count)
            .Select(_ => new BlobFeature(maxArea, 10, 0, 0, new Rect(0, 0, 1, 1), 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 255, Array.Empty<Point>()))
            .ToList();
        return new BlobNode.RoiResult(name, true, blobs, meta ?? new RoiMeta(), new Rect(0, 0, 10, 10));
    }

    [Fact]
    public void CheckPass_AllModes()
    {
        // 检出即NG：有斑不过
        Assert.False(BlobNode.CheckPass(BlobNode.DecisionDetectNg, 1, 100, 1));
        Assert.True(BlobNode.CheckPass(BlobNode.DecisionDetectNg, 0, 0, 1));
        // 缺失即NG：无斑不过
        Assert.False(BlobNode.CheckPass(BlobNode.DecisionMissingNg, 0, 0, 1));
        Assert.True(BlobNode.CheckPass(BlobNode.DecisionMissingNg, 2, 50, 1));
        // 数量≥阈值
        Assert.False(BlobNode.CheckPass(BlobNode.DecisionCountNg, 2, 0, 2));
        Assert.True(BlobNode.CheckPass(BlobNode.DecisionCountNg, 1, 0, 2));
        // 最大面积≥阈值
        Assert.False(BlobNode.CheckPass(BlobNode.DecisionAreaNg, 1, 100, 100));
        Assert.True(BlobNode.CheckPass(BlobNode.DecisionAreaNg, 1, 99, 100));
    }

    [Fact]
    public void ComputeDecision_Observe_Disabled_AndPerItem()
    {
        Assert.Equal("OK", BlobNode.ComputeDecision(BlobNode.DecisionObserveOnly, [Item("A", 5, 999)], 1));
        Assert.Equal("NG", BlobNode.ComputeDecision(BlobNode.DecisionDetectNg, [Item("A", 1, 10)], 1));
        Assert.Equal("OK", BlobNode.ComputeDecision(BlobNode.DecisionDetectNg, [Item("A", 0, 0)], 1));

        // 仅观察/停用项不参与
        var observe = Item("观察", 5, 999, new RoiMeta(Judge: "仅观察"));
        var disabled = new BlobNode.RoiResult("停用", false, [], new RoiMeta(Enabled: false), default);
        Assert.Equal("OK", BlobNode.ComputeDecision(BlobNode.DecisionDetectNg, [observe, disabled], 1));

        // 检测项级阈值生效（数量模式：阈值 3 → 2 个过，1 个不过由另一项触发）
        var relaxed = Item("A", 2, 0, new RoiMeta(Threshold: "3"));
        Assert.Equal("OK", BlobNode.ComputeDecision(BlobNode.DecisionCountNg, [relaxed], 1));
        var strict = Item("A", 2, 0, new RoiMeta(Threshold: "2"));
        Assert.Equal("NG", BlobNode.ComputeDecision(BlobNode.DecisionCountNg, [strict], 1));
    }

    // ===== 端到端（合成图） =====

    /// <summary>黑底白斑合成图：左上 10×10（面积100）、右下 5×5（面积25）。</summary>
    private static Mat BlobsImage()
    {
        var img = new Mat(100, 120, MatType.CV_8UC3, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(10, 10, 10, 10), new Scalar(255, 255, 255), -1);
        Cv2.Rectangle(img, new Rect(100, 80, 5, 5), new Scalar(255, 255, 255), -1);
        return img;
    }

    private static BlobNode NewNode(params (string Key, string Value)[] extra)
    {
        var node = new BlobNode("01 Blob分析");
        foreach (var (k, v) in extra) node.SetParam(k, v);
        return node;
    }

    [Fact]
    public void Run_WholeImage_DetectNg()
    {
        using var img = BlobsImage();
        using var node = NewNode(("min_area", "1"));
        var ctx = new PipelineRunContext(img);
        var nr = node.Run(img, ctx);

        Assert.Equal("NG", nr.Decision);
        Assert.Equal("2", nr.Values["count"]);
        Assert.Equal("125", nr.Values["total_area"]);
        Assert.Equal("100", nr.Values["max_area"]);
        Assert.Equal(2, nr.Annotations.Count(s => s.Kind == NodeShapeKind.Defect)); // 整图模式：两条红色轮廓标注
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(3, nr.OutputImage!.Channels());
    }

    [Fact]
    public void Run_RoiDetectAndMissing()
    {
        using var img = BlobsImage();
        // 检测项盖住左上斑点 → 检出即NG
        using var node1 = NewNode(("own_rois", NodeRois.SerializeOwn([("检测项", new RoiRect(0.1, 0.1, 0.2, 0.2, 0))])), ("min_area", "1"));
        {
            var ctx = new PipelineRunContext(img);
            var nr = node1.Run(img, ctx);
            Assert.Equal("NG", nr.Decision);
            Assert.Equal("1", nr.Values["roi_检测项"]);
            Assert.Equal("100", nr.Values["roi_检测项_area"]);
            Assert.True(nr.Annotations.Any(s => s.Kind == NodeShapeKind.Defect));
        }
        // 空区域 → 检出即NG 过
        using var node2 = NewNode(("own_rois", NodeRois.SerializeOwn([("空白", new RoiRect(0.4, 0.4, 0.1, 0.1, 0))])), ("min_area", "1"));
        {
            var ctx = new PipelineRunContext(img);
            var nr = node2.Run(img, ctx);
            Assert.Equal("OK", nr.Decision);
            Assert.Equal("0", nr.Values["roi_空白"]);
        }
        // 同一空区域 → 缺失即NG 不过
        using var node3 = NewNode(
            ("own_rois", NodeRois.SerializeOwn([("空白", new RoiRect(0.4, 0.4, 0.1, 0.1, 0))])),
            ("min_area", "1"),
            ("decision_mode", BlobNode.DecisionMissingNg));
        {
            var ctx = new PipelineRunContext(img);
            Assert.Equal("NG", node3.Run(img, ctx).Decision);
        }
    }

    [Fact]
    public void Run_CountAndAreaThresholdModes()
    {
        using var img = BlobsImage();
        // 大 ROI 同时盖住两个斑（整图 0.1~0.95）→ 数量≥阈值
        var bigRoi = NodeRois.SerializeOwn([("检测项", new RoiRect(0.5, 0.5, 0.9, 0.9, 0))]);
        using var node1 = NewNode(("own_rois", bigRoi), ("min_area", "1"), ("decision_mode", BlobNode.DecisionCountNg));
        {
            var ctx = new PipelineRunContext(img);
            node1.SetParam("own_rois", NodeRois.SerializeOwnFull([new RoiItem("检测项", new RoiRect(0.5, 0.5, 0.9, 0.9, 0), new RoiMeta(Threshold: "2"))]));
            Assert.Equal("NG", node1.Run(img, ctx).Decision);
            node1.SetParam("own_rois", NodeRois.SerializeOwnFull([new RoiItem("检测项", new RoiRect(0.5, 0.5, 0.9, 0.9, 0), new RoiMeta(Threshold: "3"))]));
            Assert.Equal("OK", node1.Run(img, ctx).Decision);
        }
        // 最大面积≥阈值：100px 斑，阈值 100 → NG，101 → OK
        using var node2 = NewNode(("own_rois", bigRoi), ("min_area", "1"), ("decision_mode", BlobNode.DecisionAreaNg));
        {
            var ctx = new PipelineRunContext(img);
            node2.SetParam("own_rois", NodeRois.SerializeOwnFull([new RoiItem("检测项", new RoiRect(0.5, 0.5, 0.9, 0.9, 0), new RoiMeta(Threshold: "100"))]));
            Assert.Equal("NG", node2.Run(img, ctx).Decision);
            node2.SetParam("own_rois", NodeRois.SerializeOwnFull([new RoiItem("检测项", new RoiRect(0.5, 0.5, 0.9, 0.9, 0), new RoiMeta(Threshold: "101"))]));
            Assert.Equal("OK", node2.Run(img, ctx).Decision);
        }
    }

    [Fact]
    public void Run_DisabledAndObserveItems()
    {
        using var img = BlobsImage();
        var rois = NodeRois.SerializeOwnFull(
        [
            new RoiItem("停用项", new RoiRect(0.1, 0.1, 0.2, 0.2, 0), new RoiMeta(Enabled: false)),
            new RoiItem("观察项", new RoiRect(0.1, 0.1, 0.2, 0.2, 0), new RoiMeta(Judge: "仅观察")),
        ]);
        using var node = NewNode(("own_rois", rois), ("min_area", "1"));
        var ctx = new PipelineRunContext(img);
        var nr = node.Run(img, ctx);

        Assert.Equal("OK", nr.Decision); // 停用/观察不参与判定
        Assert.Equal("停用", nr.Values["roi_停用项"]);
        Assert.Equal("1", nr.Values["roi_观察项"]); // 观察项仍输出值
    }

    [Fact]
    public void Run_ScopeFilter_RemovesOutOfScopeBlobs()
    {
        using var img = BlobsImage();
        // 两个检测项都盖住各自斑点；总范围 = 左上项（仅观察，自身不判定）→ 右下斑被移除
        var rois = NodeRois.SerializeOwnFull(
        [
            new RoiItem("范围", new RoiRect(0.1, 0.1, 0.2, 0.2, 0), new RoiMeta(Judge: "仅观察")),
            new RoiItem("检测项", new RoiRect(0.9, 0.85, 0.15, 0.15, 0), new RoiMeta()),
        ]);
        using var node = NewNode(("own_rois", rois), ("min_area", "1"), ("scope_index", "0"));
        var ctx = new PipelineRunContext(img);
        var nr = node.Run(img, ctx);

        Assert.Equal("OK", nr.Decision); // 右下斑在总范围外被移除，检出即NG 过
        Assert.Equal("0", nr.Values["roi_检测项"]);
    }

    [Fact]
    public void Run_AreaFilter_ParamsTakeEffect()
    {
        using var img = BlobsImage();
        // 最小面积 50：只剩 100px 大斑
        using var node = NewNode(("min_area", "50"));
        var ctx = new PipelineRunContext(img);
        var nr = node.Run(img, ctx);
        Assert.Equal("1", nr.Values["count"]);
    }

    [Fact]
    public void Run_ShowBinary_OutputsBinaryPreview()
    {
        using var img = BlobsImage();
        using var node = NewNode(("show_binary", "true"), ("threshold", "100"));
        var ctx = new PipelineRunContext(img);
        var nr = node.Run(img, ctx);
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(3, nr.OutputImage!.Channels());
        // 预览图：斑点处白（255 三通道）、背景黑
        Assert.Equal(255, nr.OutputImage.Get<byte>(15, 15));
        Assert.Equal(0, nr.OutputImage.Get<byte>(5, 5));
    }

    [Fact]
    public void Run_PositionCorrection_MovesRoi()
    {
        using var img = BlobsImage();
        using var node = NewNode(("own_rois", NodeRois.SerializeOwn([("检测项", new RoiRect(0.5, 0.5, 0.2, 0.2, 0))])), ("min_area", "1"));
        // 基准(0,0,0) → 当前(0,0)+0°，只平移到斑点上：ROI 中心(60,50) → (15,15)
        var correction = new PoseCorrection("定位", ["01 Blob分析"], 60, 50, 0, 15, 15, 0);
        var ctx = new PipelineRunContext(img) { PoseCorrection = correction };
        var nr = node.Run(img, ctx);
        Assert.Equal("NG", nr.Decision); // ROI 跟随修正后盖住左上斑
        Assert.Equal("1", nr.Values["roi_检测项"]);
    }

    [Fact]
    public void Run_SourceMissing_Throws()
    {
        using var img = BlobsImage();
        using var node = NewNode(("source", "不存在的节点"));
        var ctx = new PipelineRunContext(img);
        Assert.Throws<InvalidOperationException>(() => node.Run(img, ctx));
    }

    // ===== 工厂与方案往返 =====

    [Fact]
    public void Factory_RoundTrip_Dispose()
    {
        using var node = NodeFactory.Create("Blob", "01 Blob分析");
        Assert.IsType<BlobNode>(node);
        Assert.Equal("Blob", node.Type);
        Assert.DoesNotContain(node.ParamDefs, d => d.Key == "model_dir"); // 无外部模型

        node.SetParam("source", "00 图像源");
        node.SetParam("threshold", "170");
        node.SetParam("own_rois", NodeRois.SerializeOwn(
        [
            ("螺钉", new RoiRect(0.2, 0.3, 0.1, 0.1, 0)),
            ("螺钉", new RoiRect(0.7, 0.6, 0.1, 0.1, 15)),
        ]));

        var recipe = new Recipe { Name = "Blob往返" };
        recipe.Nodes.Add(new RecipeNode
        {
            Name = node.Name,
            Type = node.Type,
            Enabled = true,
            Params = new Dictionary<string, string>(node.Params),
        });
        var path = Path.Combine(Path.GetTempPath(), $"blob_rt_{Guid.NewGuid():N}.json");
        try
        {
            RecipeStore.Save(recipe, path);
            var back = Assert.Single(RecipeStore.LoadFile(path).Nodes);
            Assert.Equal("Blob", back.Type);
            Assert.Equal("170", back.Params.GetValueOrDefault("threshold"));
            var rois = NodeRois.ParseOwn(back.Params.GetValueOrDefault("own_rois"));
            Assert.Equal(2, rois.Count);
            Assert.Equal("螺钉", rois[0].Name);
            Assert.Equal(15, rois[1].Rect.AngleDeg, 4);
        }
        finally
        {
            File.Delete(path);
        }

        node.Dispose();
        node.Dispose(); // 幂等
    }

    // ===== 辅助 =====

    /// <summary>2×2 双灰度图：(0,0)=high，(1,1)=low，其余 0。</summary>
    private static Mat TwoLevelGray(byte low, byte high)
    {
        var gray = new Mat(2, 2, MatType.CV_8UC1, Scalar.All(0));
        gray.Set(0, 0, high);
        gray.Set(1, 1, low);
        return gray;
    }

    /// <summary>1×3 三灰度图：low / mid / high。</summary>
    private static Mat ThreeLevelGray(byte low, byte mid, byte high)
    {
        var gray = new Mat(1, 3, MatType.CV_8UC1, Scalar.All(0));
        gray.Set(0, 0, low);
        gray.Set(0, 1, mid);
        gray.Set(0, 2, high);
        return gray;
    }
}
