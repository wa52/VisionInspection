using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 直线查找测试（合成图，全离线）：角度/端点/亚像素恢复、极性/边缘类型、离群剔除、未找到语义、
/// 定位契约输出、工厂往返 + ≤5ms 性能断言（常规集内跑，中位数计时）。
/// </summary>
public class LineFindTests
{
    private readonly ITestOutputHelper _out;

    public LineFindTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// 合成条带图：沿 angleDeg 方向过 (cx,cy) 的亮带（半宽 halfWidth），背景暗。
    /// 边缘带 blur px 平滑过渡（smoothstep，50% 灰度点=真实边缘，模拟镜头点扩散）——
    /// 理想硬阶跃 + 双线性采样存在 ±0.5px 相位歧义，真实相机边缘总有模糊。
    /// </summary>
    private static byte[] MakeStrip(int w, int h, double angleDeg, double cx, double cy, byte bg = 20, byte fg = 220, double halfWidth = 6, double blur = 2)
    {
        var buf = new byte[w * h];
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var perp = -sin * dx + cos * dy;
                var up = Math.Clamp((perp + halfWidth + blur / 2) / blur, 0, 1);   // 上缘：暗→亮过渡，50% 点在 -halfWidth
                var down = Math.Clamp((halfWidth + blur / 2 - perp) / blur, 0, 1); // 下缘：亮→暗过渡，50% 点在 +halfWidth
                var t = Math.Min(up, down);
                buf[y * w + x] = (byte)(bg + (fg - bg) * t * t * (3 - 2 * t)); // smoothstep
            }
        }
        return buf;
    }

    /// <summary>在条带图上叠加圆形亮斑（离群点用）。</summary>
    private static void PaintBlob(byte[] buf, int w, int h, double cx, double cy, double radius, byte val = 220)
    {
        for (var y = Math.Max(0, (int)(cy - radius)); y <= Math.Min(h - 1, (int)(cy + radius)); y++)
        {
            for (var x = Math.Max(0, (int)(cx - radius)); x <= Math.Min(w - 1, (int)(cx + radius)); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    buf[y * w + x] = val;
                }
            }
        }
    }

    private static Mat ToMat(byte[] buf, int w, int h)
    {
        var mat = new Mat(h, w, MatType.CV_8UC1);
        mat.SetArray(buf); // ⚠ 写入后必须 SetArray 才落回 Mat
        return mat;
    }

    private static LineFindParams Params(
        string polarity = LineFindPolarity.DarkToBright,
        string edgeType = LineFindEdgeType.First,
        int calipers = 16,
        int rejectNum = 0,
        double searchLength = 32,
        string fitMethod = LineFindFitMethod.Lsq) => new()
    {
        Polarity = polarity,
        EdgeThreshold = 30,
        FilterSize = 3,
        CaliperNum = calipers,
        SearchLength = searchLength,
        EdgeType = edgeType,
        RejectNum = rejectNum,
        RejectDist = 5,
        FitMethod = fitMethod,
    };

    /// <summary>点到拟合直线的垂距（用起点/终点方向）。</summary>
    private static double DistanceToLine(LineFindResult r, double px, double py)
    {
        var dx = r.X2 - r.X1;
        var dy = r.Y2 - r.Y1;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return Math.Abs((px - r.X1) * dy - (py - r.Y1) * dx) / len;
    }

    [Fact]
    public void Find_HorizontalStrip_RecoversAngleAndOffset()
    {
        var buf = MakeStrip(600, 200, 0, 300, 100);
        var region = new LineSearchRegion(300, 100, 200, 30, 0);
        var r = LineFindCore.Find(buf, 600, 200, region, Params());

        Assert.True(r.Found, r.Error);
        Assert.Equal(0, r.AngleDeg, 2); // ±0.01°
        // 黑到白沿 +Y → 找到条带上缘（真实边缘 y=94，2px 过渡带 50% 灰度点），垂距 6±0.3
        var dist = DistanceToLine(r, 300, 100);
        Assert.InRange(dist, 5.7, 6.4);
        Assert.True(r.Score >= 0.9);
        // 2px smoothstep 过渡（幅值 200）的峰值梯度 ≈ 130
        Assert.InRange(r.MeanContrast, 80, 180);
        Assert.Equal(16, r.Edges.Count);
    }

    [Theory]
    [InlineData(15.0)]
    [InlineData(-75.0)]
    [InlineData(170.0)]
    public void Find_RotatedStrip_RecoversNormalizedAngle(double axisDeg)
    {
        var buf = MakeStrip(640, 480, axisDeg, 320, 240);
        var axisRad = axisDeg * Math.PI / 180.0;
        var region = new LineSearchRegion(320, 240, 260, 30, axisRad);
        var r = LineFindCore.Find(buf, 640, 480, region, Params(calipers: 24));

        Assert.True(r.Found, r.Error);
        var expected = LineFindCore.NormalizeLineAngle(axisDeg);
        Assert.Equal(expected, r.AngleDeg, 1); // ±0.1°
        Assert.InRange(DistanceToLine(r, 320, 240), 5.7, 6.4);
    }

    [Fact]
    public void Find_Subpixel_ShiftDetected()
    {
        const int w = 600, h = 200;
        var region = new LineSearchRegion(300, 100, 200, 30, 0);
        var p = Params();
        var r1 = LineFindCore.Find(MakeStrip(w, h, 0, 300, 100.0), w, h, region, p);
        var r2 = LineFindCore.Find(MakeStrip(w, h, 0, 300, 100.5), w, h, region, p);

        Assert.True(r1.Found && r2.Found);
        var d1 = DistanceToLine(r1, 300, 100);
        var d2 = DistanceToLine(r2, 300, 100);
        Assert.InRange(Math.Abs(d1 - d2), 0.2, 0.8); // 亚像素 0.5px 位移可分辨
    }

    [Fact]
    public void Find_Polarity_SelectsOppositeSides()
    {
        const int w = 600, h = 200;
        var buf = MakeStrip(w, h, 0, 300, 100);
        var region = new LineSearchRegion(300, 100, 200, 30, 0);

        var darkToBright = LineFindCore.Find(buf, w, h, region, Params(polarity: LineFindPolarity.DarkToBright));
        var brightToDark = LineFindCore.Find(buf, w, h, region, Params(polarity: LineFindPolarity.BrightToDark));
        var any = LineFindCore.Find(buf, w, h, region, Params(polarity: LineFindPolarity.Any));

        Assert.True(darkToBright.Found);
        Assert.True(brightToDark.Found);
        Assert.True(any.Found);
        Assert.InRange(DistanceToLine(darkToBright, 300, 100), 5.7, 6.4); // 上缘
        Assert.InRange(DistanceToLine(brightToDark, 300, 100), 5.7, 6.4); // 下缘（反向搜索）
        // 两侧线不重合（相距 ≈ 条带宽度 12px）
        Assert.True(Math.Abs(darkToBright.MidY - brightToDark.MidY) > 8);
    }

    [Fact]
    public void Find_EdgeType_FirstAndLast_SelectDifferentBands()
    {
        const int w = 600, h = 220;
        // 两条平行亮带：y=75 与 y=145（半宽 5）；区域中心 y=110，搜索长度 100 → 剖面覆盖 60..160
        var buf = MakeStrip(w, h, 0, 300, 75, halfWidth: 5);
        var lower = MakeStrip(w, h, 0, 300, 145, halfWidth: 5);
        for (var i = 0; i < buf.Length; i++)
        {
            if (lower[i] == 220) buf[i] = 220;
        }
        var region = new LineSearchRegion(300, 110, 200, 60, 0);

        var first = LineFindCore.Find(buf, w, h, region, Params(edgeType: LineFindEdgeType.First, searchLength: 100));
        var last = LineFindCore.Find(buf, w, h, region, Params(edgeType: LineFindEdgeType.Last, searchLength: 100));
        var strongest = LineFindCore.Find(buf, w, h, region, Params(edgeType: LineFindEdgeType.Strongest, searchLength: 100));

        Assert.True(first.Found && last.Found && strongest.Found);
        Assert.Equal(0, first.AngleDeg, 1);
        Assert.Equal(0, last.AngleDeg, 1);
        // 黑到白边缘：上带外缘 y≈70、下带外缘 y≈140
        Assert.True(Math.Abs(first.MidY - 70) < 2, $"first midY={first.MidY}");
        Assert.True(Math.Abs(last.MidY - 140) < 2, $"last midY={last.MidY}");
    }

    [Fact]
    public void Find_RobustPrior_StaysOnTrackedParallelBand()
    {
        const int w = 600, h = 220;
        var buf = MakeStrip(w, h, 0, 300, 75, halfWidth: 5);
        var lower = MakeStrip(w, h, 0, 300, 145, halfWidth: 5);
        for (var i = 0; i < buf.Length; i++)
        {
            if (lower[i] == 220) buf[i] = 220;
        }

        var region = new LineSearchRegion(300, 110, 200, 60, 0);
        var r = LineFindCore.Find(
            buf, w, h, region,
            Params(polarity: LineFindPolarity.DarkToBright, edgeType: LineFindEdgeType.Strongest, searchLength: 100, fitMethod: LineFindFitMethod.Robust),
            new LineFindPrior(300, 70, 0));

        Assert.True(r.Found, r.Error);
        Assert.True(Math.Abs(r.MidY - 70) < 2, $"tracked midY={r.MidY}");
    }

    [Fact]
    public void Find_RejectOutlier_KeepsLineStraight()
    {
        const int w = 600, h = 200;
        var buf = MakeStrip(w, h, 0, 300, 100);
        // 在一个卡尺正上方（条带外缘之上约 20px）放亮斑：该卡尺「第一条」会先撞到亮斑 → 离群点
        // num=8, halfLen=150 → 卡尺 s3 = -150 + 300*3/7 ≈ -21.43；搜索长度 64 → 剖面 y 70..130 覆盖亮斑入口
        PaintBlob(buf, w, h, 300 - 21.43, 80, 6);

        var withReject = LineFindCore.Find(buf, w, h, new LineSearchRegion(300, 100, 150, 30, 0), Params(calipers: 8, rejectNum: 1, searchLength: 64));
        var noReject = LineFindCore.Find(buf, w, h, new LineSearchRegion(300, 100, 150, 30, 0), Params(calipers: 8, rejectNum: 0, searchLength: 64));

        Assert.True(withReject.Found && noReject.Found);
        Assert.Equal(0, withReject.AngleDeg, 1);
        Assert.InRange(DistanceToLine(withReject, 300, 100), 5.7, 6.5); // 剔除后回到真实条带缘
        Assert.Equal(7, withReject.Inliers.Count); // 8 卡尺剔 1 离群
        // 不剔除：离群点把线拉偏（垂距偏差 > 1px、角度偏差 > 0.05°）
        Assert.True(Math.Abs(DistanceToLine(noReject, 300, 100) - 6) > 1.0,
            $"noReject dist={DistanceToLine(noReject, 300, 100)}");
        Assert.True(Math.Abs(noReject.AngleDeg) > 0.05, $"noReject angle={noReject.AngleDeg}");
    }

    [Fact]
    public void Find_RobustFit_IgnoresOutliersWithoutReject()
    {
        // 与 Find_RejectOutlier 同一离群场景：鲁棒拟合（Tukey IRLS）免配置自动降权；最小二乘不剔除则被拉偏
        const int w = 600, h = 200;
        var buf = MakeStrip(w, h, 0, 300, 100);
        PaintBlob(buf, w, h, 300 - 21.43, 80, 6);
        var region = new LineSearchRegion(300, 100, 150, 30, 0);

        var robust = LineFindCore.Find(buf, w, h, region, Params(calipers: 8, fitMethod: LineFindFitMethod.Robust, searchLength: 64));
        var lsq = LineFindCore.Find(buf, w, h, region, Params(calipers: 8, searchLength: 64));

        Assert.True(robust.Found && lsq.Found);
        Assert.Equal(0, robust.AngleDeg, 1);
        Assert.InRange(DistanceToLine(robust, 300, 100), 5.7, 6.4);
        Assert.True(Math.Abs(DistanceToLine(lsq, 300, 100) - 6) > 1.0, $"lsq dist={DistanceToLine(lsq, 300, 100)}");
    }

    [Fact]
    public void Find_NoEdge_NotFound()
    {
        var buf = new byte[600 * 200]; // 纯黑
        var r = LineFindCore.Find(buf, 600, 200, new LineSearchRegion(300, 100, 200, 30, 0), Params());
        Assert.False(r.Found);
        Assert.Contains("有效边缘点不足", r.Error);
    }

    [Fact]
    public void Find_NoisyRepeats_RobustFit_StableMidpoint()
    {
        // VM FitFun=Huber 对齐的稳定性回归：同一静态边缘 + 每帧噪声，鲁棒拟合中点重复性必须远好于一个像素
        const int w = 600, h = 200;
        var region = new LineSearchRegion(300, 100, 200, 30, 0);
        var p = Params(fitMethod: LineFindFitMethod.Robust, edgeType: LineFindEdgeType.Strongest);
        var rng = new Random(20260908);
        var dists = new List<double>();
        for (var k = 0; k < 30; k++)
        {
            var clean = MakeStrip(w, h, 0, 300, 100);
            var buf = new byte[w * h];
            for (var i = 0; i < buf.Length; i++)
            {
                var v = clean[i] + (int)((rng.NextDouble() * 2 - 1) * 12);
                buf[i] = (byte)Math.Clamp(v, 0, 255);
            }
            var r = LineFindCore.Find(buf, w, h, region, p);
            Assert.True(r.Found, r.Error);
            dists.Add(DistanceToLine(r, 300, 100));
        }
        var mean = dists.Average();
        var std = Math.Sqrt(dists.Sum(d => (d - mean) * (d - mean)) / (dists.Count - 1));
        Assert.True(std <= 0.15, $"30 帧重复性 std={std:F3}px 超过 0.15");
    }

    [Fact]
    public void Find_NoEdge_Diagnostics_InError()
    {
        // 平坦图：诊断里必须看到 采样半宽/阈值/剖面梯度，指引降阈值或加大范围
        var buf = new byte[600 * 200];
        var r = LineFindCore.Find(buf, 600, 200, new LineSearchRegion(300, 100, 200, 30, 0), Params());
        Assert.False(r.Found);
        Assert.Contains("采样半宽 ±16px", r.Error); // SearchLength=32 → ±16
        Assert.Contains("边缘阈值 30", r.Error);
        Assert.Contains("剖面几乎无灰度变化", r.Error);
    }

    [Fact]
    public void Find_LowContrastNearThreshold_UsesFallback()
    {
        // 低对比度/宽过渡边缘的峰值略低于阈值：不能因单次量化或平滑就完全丢失。
        var p = Params() with { EdgeThreshold = 15 };
        var r = LineFindCore.Find(
            MakeStrip(600, 200, 0, 300, 100, bg: 20, fg: 80, halfWidth: 6, blur: 10),
            600, 200,
            new LineSearchRegion(300, 100, 200, 30, 0),
            p);

        Assert.True(r.Found, r.Error);
        Assert.True(r.Edges.Count >= 2);
    }

    [Fact]
    public void Find_SearchLengthClampedByRegion_Diagnostics()
    {
        // ROI 短边 10px < 搜索长度 32 → 诊断说明钳制来源（真实场景最易踩的坑）
        var buf = new byte[600 * 200];
        var r = LineFindCore.Find(buf, 600, 200, new LineSearchRegion(300, 100, 200, 5, 0), Params());
        Assert.False(r.Found);
        Assert.Contains("采样半宽 ±5px（搜索长度 32 被 ROI 短边宽度 10 钳制）", r.Error);
    }

    [Fact]
    public void Find_PolarityMismatch_Diagnostics_SuggestFlip()
    {
        const int w = 600, h = 200;
        var buf = new byte[w * h];
        for (var y = 96; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                buf[y * w + x] = 200; // 上暗下亮：沿 +Y 唯一 黑→白 过阈边缘
            }
        }
        // 极性 白到黑 → 与唯一边缘方向不符 → 未找到且诊断建议改回 黑到白
        var r = LineFindCore.Find(buf, w, h, new LineSearchRegion(300, 110, 180, 20, 0), Params(polarity: LineFindPolarity.BrightToDark));
        Assert.False(r.Found);
        Assert.Contains("改为「黑到白」", r.Error);
        Assert.Contains("剖面最大梯度", r.Error);
    }

    [Fact]
    public void NormalizeLineAngle_Wraps()
    {
        Assert.Equal(0, LineFindCore.NormalizeLineAngle(0), 6);
        Assert.Equal(90, LineFindCore.NormalizeLineAngle(90), 6);
        Assert.Equal(90, LineFindCore.NormalizeLineAngle(-90), 6);
        Assert.Equal(0, LineFindCore.NormalizeLineAngle(180), 6);
        Assert.Equal(-45, LineFindCore.NormalizeLineAngle(135), 6);
        Assert.Equal(45, LineFindCore.NormalizeLineAngle(-135), 6);
        Assert.Equal(90, LineFindCore.NormalizeLineAngle(270), 6);
        Assert.Equal(-10, LineFindCore.NormalizeLineAngle(170), 6);
    }

    [Fact]
    public void FitLine_CollinearPoints()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (10, 10), (20, 20) };
        var (angle, cx, cy) = LineFindCore.FitLine(pts, Enumerable.Range(0, 3).ToList());
        Assert.Equal(45, angle * 180 / Math.PI, 4);
        Assert.Equal(10, cx, 4);
        Assert.Equal(10, cy, 4);
    }

    // ===== 节点级 =====

    private static LineFindNode NewNode(params (string Key, string Value)[] init)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in init) dict[k] = v;
        return new LineFindNode("直线查找1", dict);
    }

    [Fact]
    public void Run_FindsLine_OutputsContractAndAnnotations()
    {
        using var img = ToMat(MakeStrip(600, 200, 0, 300, 100), 600, 200);
        var ctx = new PipelineRunContext(img);
        // 区域 400×60 中心 (300,100)
        var node = NewNode(("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(0.5, 0.5, 400.0 / 600, 60.0 / 200, 0))])));
        var nr = node.Run(img, ctx);

        Assert.Equal("OK", nr.Decision);
        Assert.Equal("1", nr.Values["loc_valid"]);
        Assert.Equal("0.00", nr.Values["loc_angle"]);
        Assert.Equal("0.00", nr.Values["angle"]);
        Assert.Equal(16, int.Parse(nr.Values["count"]));
        Assert.Equal(1.0, double.Parse(nr.Values["score"]), 3);
        Assert.NotNull(nr.OutputImage);
        // 标注：线段 + 卡尺点 + 区域四边形
        Assert.Contains(nr.Annotations, a => a.Polys is { } polys && polys.Count == 1 && polys[0].Length == 2);
        Assert.Contains(nr.Annotations, a => a.AsPoints);
        Assert.Contains(nr.Annotations, a => a.Kind == NodeShapeKind.Info);
    }

    [Fact]
    public void Run_NoRegion_SelfErrors_WithPassthroughImage()
    {
        // 无 ROI：自判 ERROR 停线，但必须透传源图——显示区有图才能画框（旧实现提前抛异常导致永远无图可画）
        using var img = ToMat(MakeStrip(600, 200, 0, 300, 100), 600, 200);
        var ctx = new PipelineRunContext(img);
        var node = NewNode();
        var nr = node.Run(img, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.False(string.IsNullOrWhiteSpace(nr.Error));
        Assert.NotNull(nr.OutputImage);
        Assert.Equal("0", nr.Values["loc_valid"]);
        Assert.False(string.IsNullOrWhiteSpace(nr.Values["error"]));
    }

    [Fact]
    public void Run_NotFound_NG_LocInvalid()
    {
        using var img = ToMat(new byte[600 * 200], 600, 200);
        var ctx = new PipelineRunContext(img);
        var node = NewNode(("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(0.5, 0.5, 400.0 / 600, 60.0 / 200, 0))])));
        var nr = node.Run(img, ctx);

        Assert.Equal("NG", nr.Decision);
        Assert.Equal("0", nr.Values["loc_valid"]);
        Assert.Contains("有效边缘点不足", nr.Error);
    }

    [Fact]
    public void Run_DecisionFoundNg_Inverts()
    {
        using var img = ToMat(MakeStrip(600, 200, 0, 300, 100), 600, 200);
        var ctx = new PipelineRunContext(img);
        var node = NewNode(
            ("decision_mode", "找到即NG"),
            ("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(0.5, 0.5, 400.0 / 600, 60.0 / 200, 0))])));
        var nr = node.Run(img, ctx);
        Assert.Equal("NG", nr.Decision);

        var node2 = NewNode(
            ("decision_mode", "找到即NG"),
            ("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(0.5, 0.5, 400.0 / 600, 60.0 / 200, 0))])));
        var ctx2 = new PipelineRunContext(ToMat(new byte[600 * 200], 600, 200));
        var nr2 = node2.Run(ctx2.Input, ctx2);
        Assert.Equal("OK", nr2.Decision);
        ctx2.Input.Dispose();
    }

    [Fact]
    public void Run_MultiRegion_UsesFirstOnly()
    {
        using var img = ToMat(MakeStrip(600, 200, 0, 300, 100), 600, 200);
        var ctx = new PipelineRunContext(img);
        var node = NewNode(("own_rois", NodeRois.SerializeOwn(
        [
            ("条带区", new RoiRect(0.5, 0.5, 400.0 / 600, 60.0 / 200, 0)),
            ("空白区", new RoiRect(0.5, 0.9, 200.0 / 600, 20.0 / 200, 0)),
        ])));
        var nr = node.Run(img, ctx);
        Assert.Equal("OK", nr.Decision); // 只用第一个区域（有条带）
        Assert.True(int.Parse(nr.Values["count"]) > 0);
    }

    [Fact]
    public void Factory_RoundTrip_Dispose()
    {
        var node = NodeFactory.Create("LineFind", "直线查找1");
        Assert.Equal("LineFind", node.Type);
        Assert.Contains(node.ParamDefs, d => d.Key == "edge_polarity");
        node.SetParam("edge_threshold", "45");
        node.Name = "02 直线查找";
        Assert.Equal("45", node.Params["edge_threshold"]);
        node.Dispose();
        node.Dispose(); // 幂等
    }

    // ===== 性能（≤5ms 常规断言） =====

    private static double MedianMs(List<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    [Fact]
    public void Performance_CoreUnder5ms()
    {
        const int w = 1024, h = 768;
        var buf = MakeStrip(w, h, 10, w / 2.0, h / 2.0);
        var region = new LineSearchRegion(w / 2.0, h / 2.0, 300, 40, 10 * Math.PI / 180);
        var p = Params(calipers: 32);

        for (var i = 0; i < 3; i++) LineFindCore.Find(buf, w, h, region, p); // 预热
        var samples = new List<double>();
        for (var i = 0; i < 11; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = LineFindCore.Find(buf, w, h, region, p);
            sw.Stop();
            Assert.True(r.Found);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        var median = MedianMs(samples);
        _out.WriteLine($"直线查找核 1024×768/32卡尺×64采样: 中位数 {median:F3}ms");
        Assert.True(median < 5.0, $"直线查找核中位数 {median:F3}ms 超出 5ms 预算");
    }

    [Fact]
    public void Performance_NodeUnder5ms()
    {
        // 节点全链（裁剪+灰度+GetArray+核心+底图克隆），3 通道 BGR 输入贴近生产
        const int w = 1024, h = 768;
        var strip = MakeStrip(w, h, 10, w / 2.0, h / 2.0);
        using var grayImg = ToMat(strip, w, h);
        using var img = new Mat();
        Cv2.CvtColor(grayImg, img, ColorConversionCodes.GRAY2BGR);

        var node = NewNode(("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(0.5, 0.5, 600.0 / w, 80.0 / h, 10))])));
        var ctx = new PipelineRunContext(img);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        node.Run(img, ctx).OutputImage?.Dispose(); // 预热（JIT + 分配器）；输出图即用即释放
        sw.Stop();
        var samples = new List<double> { sw.Elapsed.TotalMilliseconds };
        for (var i = 0; i < 11; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var nr = node.Run(img, ctx);
            watch.Stop();
            Assert.Equal("OK", nr.Decision);
            samples.Add(watch.Elapsed.TotalMilliseconds);
            nr.OutputImage?.Dispose();
        }
        var median = MedianMs(samples);
        _out.WriteLine($"直线查找节点全链 1024×768 BGR: 中位数 {median:F3}ms");
        Assert.True(median < 5.0, $"直线查找节点中位数 {median:F3}ms 超出 5ms 预算");
    }

    [Fact]
    public void Run_PoseCorrectionTarget_RegionFollows()
    {
        // 工件从基准 (300,320) 移到 (300,380)：修正把搜索区域跟过去 → 命中；无修正 → 条带出搜索带 → NG
        using var img = ToMat(MakeStrip(640, 480, 0, 300, 380), 640, 480);
        var node = NewNode(("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(300.0 / 640, 320.0 / 480, 400.0 / 640, 60.0 / 480, 0))])));
        var ctx = new PipelineRunContext(img)
        {
            PoseCorrection = new PoseCorrection("定位", ["直线查找1"], 300, 320, 0, 300, 380, 0),
        };
        var nr = node.Run(img, ctx);
        Assert.Equal("OK", nr.Decision);
        Assert.Equal("1", nr.Values["loc_valid"]);
        Assert.Equal("0.00", nr.Values["loc_angle"]);
        Assert.InRange(double.Parse(nr.Values["loc_y"]), 373.5, 374.5); // 条带上缘 380−6

        // 无修正：区域留在标称位置，条带在搜索带外 → 未找到 NG
        using var img2 = ToMat(MakeStrip(640, 480, 0, 300, 380), 640, 480);
        var node2 = NewNode(("own_rois", NodeRois.SerializeOwn([("区域1", new RoiRect(300.0 / 640, 320.0 / 480, 400.0 / 640, 60.0 / 480, 0))])));
        var nr2 = node2.Run(img2, new PipelineRunContext(img2));
        Assert.Equal("NG", nr2.Decision);
        Assert.Equal("0", nr2.Values["loc_valid"]);
    }
}
