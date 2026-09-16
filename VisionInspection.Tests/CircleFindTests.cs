using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 圆查找测试（合成图，全离线）：圆心/半径恢复、亚像素、极性选内外缘、边缘类型、离群剔除、未找到语义、
/// 定位契约输出、工厂往返 + ≤5ms 性能断言（常规集内跑，中位数计时）。
/// 合成约定与直线查找同款：亮环带 + smoothstep 2px 过渡（50% 灰度点=真实边缘，模拟镜头点扩散）。
/// </summary>
public class CircleFindTests
{
    private readonly ITestOutputHelper _out;

    public CircleFindTests(ITestOutputHelper output) => _out = output;

    /// <summary>合成亮圆环：|dist−radius| ≤ halfWidth 处亮，内外缘 smoothstep blur 过渡。</summary>
    private static byte[] MakeRing(int w, int h, double cx, double cy, double radius, byte bg = 20, byte fg = 220, double halfWidth = 6, double blur = 2)
    {
        var buf = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var d = Math.Sqrt(dx * dx + dy * dy);
                var inn = Math.Clamp((d - (radius - halfWidth) + blur / 2) / blur, 0, 1);  // 内缘：暗→亮
                var outv = Math.Clamp(((radius + halfWidth) - d + blur / 2) / blur, 0, 1); // 外缘：亮→暗
                var t = Math.Min(inn, outv);
                buf[y * w + x] = (byte)(bg + (fg - bg) * t * t * (3 - 2 * t));
            }
        }
        return buf;
    }

    /// <summary>在图上叠加实心亮斑（离群点用）。</summary>
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

    private static CircleFindParams Params(
        string polarity = LineFindPolarity.DarkToBright,
        string edgeType = LineFindEdgeType.First,
        int calipers = 24,
        int rejectNum = 0,
        double rejectDist = 5,
        string fitMethod = LineFindFitMethod.Lsq) => new()
    {
        Polarity = polarity,
        EdgeThreshold = 30,
        FilterSize = 3,
        CaliperNum = calipers,
        EdgeType = edgeType,
        RejectNum = rejectNum,
        RejectDist = rejectDist,
        FitMethod = fitMethod,
    };

    // ===== 核心级 =====

    [Fact]
    public void Find_Ring_RecoversCenterAndRadius()
    {
        var buf = MakeRing(640, 480, 320, 240, 100);
        var r = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 115, 85), Params());

        Assert.True(r.Found, r.Error);
        // 黑到白沿径向向外 → 找到环内缘（真实半径 100−6=94，2px 过渡带 50% 灰度点）
        Assert.InRange(r.Radius, 93.7, 94.35);
        Assert.InRange(r.CenterX, 319.7, 320.3);
        Assert.InRange(r.CenterY, 239.7, 240.3);
        Assert.Equal(1.0, r.Score, 3);
        // 2px smoothstep 过渡（幅值 200）的峰值梯度 ≈ 130
        Assert.InRange(r.MeanContrast, 80, 180);
        Assert.Equal(24, r.Edges.Count);
    }

    [Fact]
    public void Find_Subpixel_RadiusShiftDetected()
    {
        var region = new CircleSearchRegion(320, 240, 115, 85);
        var p = Params();
        var r1 = CircleFindCore.Find(MakeRing(640, 480, 320, 240, 100.0), 640, 480, region, p);
        var r2 = CircleFindCore.Find(MakeRing(640, 480, 320, 240, 100.5), 640, 480, region, p);

        Assert.True(r1.Found && r2.Found);
        Assert.InRange(Math.Abs(r1.Radius - r2.Radius), 0.2, 0.8); // 亚像素 0.5px 位移可分辨
    }

    [Fact]
    public void Find_Polarity_SelectsInnerAndOuterEdge()
    {
        var buf = MakeRing(640, 480, 320, 240, 100);
        var region = new CircleSearchRegion(320, 240, 115, 85);

        var darkToBright = CircleFindCore.Find(buf, 640, 480, region, Params(polarity: LineFindPolarity.DarkToBright));
        var brightToDark = CircleFindCore.Find(buf, 640, 480, region, Params(polarity: LineFindPolarity.BrightToDark));
        var any = CircleFindCore.Find(buf, 640, 480, region, Params(polarity: LineFindPolarity.Any));

        Assert.True(darkToBright.Found);
        Assert.True(brightToDark.Found);
        Assert.True(any.Found);
        Assert.InRange(darkToBright.Radius, 93.7, 94.35);  // 内缘（暗→亮）
        Assert.InRange(brightToDark.Radius, 105.65, 106.3); // 外缘（亮→暗）
        Assert.True(Math.Abs(darkToBright.Radius - brightToDark.Radius) > 8); // 两侧相距 ≈ 环宽 12px
    }

    [Fact]
    public void Find_EdgeType_FirstAndLast_SelectDifferentRings()
    {
        // 两条同心亮环：R=60 与 R=110（半宽 4）；区域径向覆盖 40..130
        var inner = MakeRing(640, 480, 320, 240, 60, halfWidth: 4);
        var outer = MakeRing(640, 480, 320, 240, 110, halfWidth: 4);
        var buf = new byte[inner.Length];
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = Math.Max(inner[i], outer[i]);
        }
        var region = new CircleSearchRegion(320, 240, 130, 40);

        var first = CircleFindCore.Find(buf, 640, 480, region, Params(edgeType: LineFindEdgeType.First));
        var last = CircleFindCore.Find(buf, 640, 480, region, Params(edgeType: LineFindEdgeType.Last));
        var strongest = CircleFindCore.Find(buf, 640, 480, region, Params(edgeType: LineFindEdgeType.Strongest));

        Assert.True(first.Found && last.Found && strongest.Found);
        Assert.InRange(first.Radius, 54, 58);   // 内环内缘 60−4=56
        Assert.InRange(last.Radius, 104, 108);  // 外环内缘 110−4=106
    }

    [Fact]
    public void Find_RejectOutlier_KeepsCircle()
    {
        // 12 卡尺 θ3=90° 处卡尺中心 (320,340)；在径向 90（y=330）放亮斑：其暗→亮缘（径向≈86）先于环内缘（94）被
        // 「第一条」选中 → 离群点（残差 ≈8px）
        var buf = MakeRing(640, 480, 320, 240, 100);
        PaintBlob(buf, 640, 480, 320, 330, 4);

        var withReject = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 115, 85), Params(calipers: 12, rejectNum: 1));
        var noReject = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 115, 85), Params(calipers: 12));

        Assert.True(withReject.Found && noReject.Found);
        Assert.Equal(11, withReject.Inliers.Count); // 12 卡尺剔 1 离群
        Assert.InRange(withReject.Radius, 93.6, 94.4); // 剔除后回到真实环内缘
        // 不剔除：离群点把拟合半径拉偏
        Assert.True(Math.Abs(noReject.Radius - 94) > 0.5, $"noReject radius={noReject.Radius}");
    }

    [Fact]
    public void Find_RobustFit_IgnoresOutliersWithoutReject()
    {
        // 与 Find_RejectOutlier 同一离群场景：鲁棒拟合（Tukey IRLS）免配置自动降权；最小二乘不剔除则被拉偏
        var buf = MakeRing(640, 480, 320, 240, 100);
        PaintBlob(buf, 640, 480, 320, 330, 4);
        var region = new CircleSearchRegion(320, 240, 115, 85);

        var robust = CircleFindCore.Find(buf, 640, 480, region, Params(calipers: 12, fitMethod: LineFindFitMethod.Robust));
        var lsq = CircleFindCore.Find(buf, 640, 480, region, Params(calipers: 12));

        Assert.True(robust.Found && lsq.Found);
        Assert.InRange(robust.Radius, 93.6, 94.4);
        Assert.True(Math.Abs(lsq.Radius - 94) > 0.5, $"lsq radius={lsq.Radius}");
    }

    [Fact]
    public void Find_RobustFit_UsesMajorityCircleWhenStrongDistractorIsPartial()
    {
        var w = 640;
        var h = 480;
        var cx = 320.0;
        var cy = 240.0;
        var buf = MakeRing(w, h, cx, cy, 100, halfWidth: 6);
        // A small, high-contrast false ring segment is stronger than the real edge on six calipers.
        // It must not win against the majority circle merely because per-caliper selection prefers contrast.
        for (var i = 0; i < 6; i++)
        {
            var angle = 2 * Math.PI * i / 24;
            PaintBlob(buf, w, h, cx + 60 * Math.Cos(angle), cy + 60 * Math.Sin(angle), 4, 255);
        }

        var region = new CircleSearchRegion(cx, cy, 115, 45);
        var robust = CircleFindCore.Find(buf, w, h, region,
            Params(edgeType: LineFindEdgeType.Strongest, calipers: 24, fitMethod: LineFindFitMethod.Robust));
        var lsq = CircleFindCore.Find(buf, w, h, region,
            Params(edgeType: LineFindEdgeType.Strongest, calipers: 24, fitMethod: LineFindFitMethod.Lsq));

        Assert.True(robust.Found, robust.Error);
        Assert.InRange(robust.Radius, 93.5, 94.5);
        Assert.True(lsq.Found, lsq.Error);
        Assert.True(Math.Abs(lsq.Radius - 94) > 0.5, $"lsq radius={lsq.Radius}");
    }

    [Fact]
    public void Find_RobustFit_AutomaticallyRecoversOppositePolarity()
    {
        var w = 320;
        var h = 240;
        var buf = new byte[w * h];
        Array.Fill(buf, (byte)20);
        PaintBlob(buf, w, h, 160, 120, 27, 220);

        var result = CircleFindCore.Find(buf, w, h, new CircleSearchRegion(160, 120, 35, 20),
            new CircleFindParams
            {
                EdgeThreshold = 90,
                Polarity = LineFindPolarity.DarkToBright,
                FitMethod = LineFindFitMethod.Robust,
            });

        Assert.True(result.Found, result.Error);
        Assert.InRange(result.Radius, 26.3, 27.7);
        Assert.Equal(1.0, result.Score, 3);
    }

    [Fact]
    public void Find_NoEdge_NotFound()
    {
        var buf = new byte[640 * 480]; // 纯黑
        var r = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 115, 85), Params());
        Assert.False(r.Found);
        Assert.Contains("有效边缘点不足", r.Error);
    }

    [Fact]
    public void Find_NoEdge_Diagnostics_InError()
    {
        // 平坦图：诊断里必须看到 径向采样范围/阈值/剖面梯度，指引降阈值或调整环带
        var buf = new byte[640 * 480];
        var r = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 115, 85), Params());
        Assert.False(r.Found);
        Assert.Contains("径向采样 85~115px", r.Error);
        Assert.Contains("边缘阈值 30", r.Error);
        Assert.Contains("剖面几乎无灰度变化", r.Error);
    }

    [Fact]
    public void Find_LowContrastNearThreshold_UsesFallback()
    {
        // 低对比度/宽过渡圆环的梯度略低于阈值：保留近阈值边缘供圆拟合。
        var p = Params() with { EdgeThreshold = 15 };
        var r = CircleFindCore.Find(
            MakeRing(640, 480, 320, 240, 100, bg: 20, fg: 80, halfWidth: 6, blur: 10),
            640, 480,
            new CircleSearchRegion(320, 240, 115, 85),
            p);

        Assert.True(r.Found, r.Error);
        Assert.True(r.Edges.Count >= 3);
    }

    [Fact]
    public void Find_PartialArc_TooFewPoints_NotFound()
    {
        // 24 卡尺 θ0=0° 在 (420,240)：只放一个亮斑 → 仅 1 条边缘 <3 → 未找到
        var buf = new byte[640 * 480];
        PaintBlob(buf, 640, 480, 420, 240, 10);
        var r = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 115, 85), Params());
        Assert.False(r.Found);
        Assert.Contains("有效边缘点不足", r.Error);
    }

    [Fact]
    public void Find_RingWidthTooNarrow_NotFound()
    {
        var buf = MakeRing(640, 480, 320, 240, 100);
        var r = CircleFindCore.Find(buf, 640, 480, new CircleSearchRegion(320, 240, 101, 99), Params());
        Assert.False(r.Found);
        Assert.Contains("环带宽度不足", r.Error);
    }

    [Fact]
    public void FitCircle_KnownPoints_Exact()
    {
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i < 8; i++)
        {
            var a = 2 * Math.PI * i / 8;
            pts.Add((50 + 30 * Math.Cos(a), -20 + 30 * Math.Sin(a)));
        }
        var fit = CircleFindCore.FitCircle(pts, Enumerable.Range(0, 8).ToList());
        Assert.True(fit.Ok);
        Assert.Equal(50, fit.Cx, 6);
        Assert.Equal(-20, fit.Cy, 6);
        Assert.Equal(30, fit.Radius, 6);
    }

    [Fact]
    public void FitCircle_CollinearPoints_Fails()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (10, 10), (20, 20), (30, 30) };
        var fit = CircleFindCore.FitCircle(pts, Enumerable.Range(0, 4).ToList());
        Assert.False(fit.Ok);
    }

    // ===== 节点级 =====

    private static CircleFindNode NewNode(params (string Key, string Value)[] init)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in init) dict[k] = v;
        return new CircleFindNode("圆查找1", dict);
    }

    [Fact]
    public void Run_FindsCircle_OutputsContractAndAnnotations()
    {
        using var img = ToMat(MakeRing(600, 600, 300, 300, 150), 600, 600);
        var ctx = new PipelineRunContext(img);
        // 圆环 R170/R130（归一化按图像宽 600）：中圆 150 = 标称环
        var node = NewNode(("own_rings", NodeRings.Serialize([("环1", new RoiRing(0.5, 0.5, 170.0 / 600, 130.0 / 600))])));
        var nr = node.Run(img, ctx);

        Assert.Equal("OK", nr.Decision);
        Assert.Equal("1", nr.Values["loc_valid"]);
        Assert.Equal("0.00", nr.Values["loc_angle"]);
        Assert.InRange(double.Parse(nr.Values["center_x"]), 299.5, 300.5);
        Assert.InRange(double.Parse(nr.Values["center_y"]), 299.5, 300.5);
        Assert.InRange(double.Parse(nr.Values["radius"]), 143.5, 144.5);
        Assert.Equal(1.0, double.Parse(nr.Values["score"]), 3);
        Assert.NotNull(nr.OutputImage);
        // 标注：拟合圆（72 段闭合）+ 卡尺点 + 搜索圆环（内外双圆 Info）
        Assert.Contains(nr.Annotations, a => a.Polys is { } polys && polys.Count == 1 && polys[0].Length == 72);
        Assert.Contains(nr.Annotations, a => a.AsPoints);
        Assert.Contains(nr.Annotations, a => a.Kind == NodeShapeKind.Info && a.Polys is { } ps && ps.Count == 2);
    }

    [Fact]
    public void Run_NoRing_SelfErrors_WithPassthroughImage()
    {
        // 无圆环：自判 ERROR 停线，但必须透传源图——显示区有图才能画框（旧实现提前抛异常导致永远无图可画）
        using var img = ToMat(MakeRing(600, 600, 300, 300, 150), 600, 600);
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
        using var img = ToMat(new byte[600 * 600], 600, 600);
        var ctx = new PipelineRunContext(img);
        var node = NewNode(("own_rings", NodeRings.Serialize([("环1", new RoiRing(0.5, 0.5, 170.0 / 600, 130.0 / 600))])));
        var nr = node.Run(img, ctx);

        Assert.Equal("NG", nr.Decision);
        Assert.Equal("0", nr.Values["loc_valid"]);
        Assert.Contains("有效边缘点不足", nr.Error);
    }

    [Fact]
    public void Run_DecisionFoundNg_Inverts()
    {
        var rings = NodeRings.Serialize([("环1", new RoiRing(0.5, 0.5, 170.0 / 600, 130.0 / 600))]);
        using var img = ToMat(MakeRing(600, 600, 300, 300, 150), 600, 600);
        var node = NewNode(("decision_mode", "找到即NG"), ("own_rings", rings));
        var nr = node.Run(img, new PipelineRunContext(img));
        Assert.Equal("NG", nr.Decision);

        using var img2 = ToMat(new byte[600 * 600], 600, 600);
        var node2 = NewNode(("decision_mode", "找到即NG"), ("own_rings", rings));
        var nr2 = node2.Run(img2, new PipelineRunContext(img2));
        Assert.Equal("OK", nr2.Decision);
    }

    [Fact]
    public void Run_MultiRing_UsesFirstOnly()
    {
        using var img = ToMat(MakeRing(600, 600, 300, 300, 150), 600, 600);
        var ctx = new PipelineRunContext(img);
        var node = NewNode(("own_rings", NodeRings.Serialize(
        [
            ("标称环", new RoiRing(0.5, 0.5, 170.0 / 600, 130.0 / 600)),
            ("空白环", new RoiRing(0.5, 0.5, 60.0 / 600, 30.0 / 600)),
        ])));
        var nr = node.Run(img, ctx);
        Assert.Equal("OK", nr.Decision); // 只用第一个圆环（有亮环）
        Assert.True(int.Parse(nr.Values["count"]) > 0);
    }

    [Fact]
    public void Factory_RoundTrip_Dispose()
    {
        var node = NodeFactory.Create("CircleFind", "圆查找1");
        Assert.Equal("CircleFind", node.Type);
        Assert.Contains(node.ParamDefs, d => d.Key == "edge_polarity");
        node.SetParam("edge_threshold", "45");
        node.Name = "02 圆查找";
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
        var buf = MakeRing(w, h, w / 2.0, h / 2.0, 200);
        var region = new CircleSearchRegion(w / 2.0, h / 2.0, 216, 184);
        var p = Params(calipers: 32);

        for (var i = 0; i < 3; i++) CircleFindCore.Find(buf, w, h, region, p); // 预热
        var samples = new List<double>();
        for (var i = 0; i < 11; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = CircleFindCore.Find(buf, w, h, region, p);
            sw.Stop();
            Assert.True(r.Found);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        var median = MedianMs(samples);
        _out.WriteLine($"圆查找核 1024×768/32卡尺×65采样: 中位数 {median:F3}ms");
        Assert.True(median < 5.0, $"圆查找核中位数 {median:F3}ms 超出 5ms 预算");
    }

    [Fact]
    public void Performance_NodeUnder5ms()
    {
        // 节点全链（裁剪+灰度+GetArray+核心+底图克隆），3 通道 BGR 输入贴近生产
        const int w = 1024, h = 768;
        using var grayImg = ToMat(MakeRing(w, h, w / 2.0, h / 2.0, 200), w, h);
        using var img = new Mat();
        Cv2.CvtColor(grayImg, img, ColorConversionCodes.GRAY2BGR);

        var node = NewNode(("own_rings", NodeRings.Serialize([("环1", new RoiRing(0.5, 0.5, 216.0 / w, 184.0 / w))])));
        var ctx = new PipelineRunContext(img);
        node.Run(img, ctx).OutputImage?.Dispose(); // 预热（JIT + 分配器）
        var samples = new List<double>();
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
        _out.WriteLine($"圆查找节点全链 1024×768 BGR: 中位数 {median:F3}ms");
        Assert.True(median < 5.0, $"圆查找节点中位数 {median:F3}ms 超出 5ms 预算");
    }

    [Fact]
    public void Run_PoseCorrectionTarget_RingFollows()
    {
        // 工件从基准 (300,300) 移到 (500,500)：修正把标称环（[Ri,Ro]=[50,70] @ 300,300）跟过去 → 命中；
        // 无修正 → 实际环（R=60 @ 500,500，最近径向距 ≈217px）远在环带外 → 0 边缘 NG
        using var img = ToMat(MakeRing(600, 600, 500, 500, 60), 600, 600);
        var node = NewNode(("own_rings", NodeRings.Serialize([("环1", new RoiRing(0.5, 0.5, 70.0 / 600, 50.0 / 600))])));
        var ctx = new PipelineRunContext(img)
        {
            PoseCorrection = new PoseCorrection("定位", ["圆查找1"], 300, 300, 0, 500, 500, 0),
        };
        var nr = node.Run(img, ctx);
        Assert.Equal("OK", nr.Decision);
        Assert.Equal("1", nr.Values["loc_valid"]);
        Assert.InRange(double.Parse(nr.Values["center_x"]), 499.5, 500.5);
        Assert.InRange(double.Parse(nr.Values["center_y"]), 499.5, 500.5);
        Assert.InRange(double.Parse(nr.Values["radius"]), 53.5, 54.5);

        // 无修正：标称环留在 (300,300)，环带 [50,70] 内全暗 → NG
        using var img2 = ToMat(MakeRing(600, 600, 500, 500, 60), 600, 600);
        var node2 = NewNode(("own_rings", NodeRings.Serialize([("环1", new RoiRing(0.5, 0.5, 70.0 / 600, 50.0 / 600))])));
        var nr2 = node2.Run(img2, new PipelineRunContext(img2));
        Assert.Equal("NG", nr2.Decision);
        Assert.Equal("0", nr2.Values["loc_valid"]);
    }
}
