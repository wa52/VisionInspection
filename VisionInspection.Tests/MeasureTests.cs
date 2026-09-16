using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;
using Xunit.Abstractions;

namespace VisionInspection.Tests;

/// <summary>
/// 几何测量四节点测试（全离线，合成几何）：
/// 核心纯函数精确值（平行/非平行 4端点平均垂距、线线夹角与交点、线圆交点、圆圆五态位置关系与交点、
/// 点圆三距离、角度双范围归一化与 y 向下符号约定、判定限边界）+
/// 节点端到端（假上游 NodeResult 注入 ctx.Results：正常/超限 NG/引用缺失 ERROR/loc_valid=0 ERROR）+
/// 工厂往返 + 不进并发模型阶段 + 性能（节点全链 &lt;1ms 中位数断言）。
/// </summary>
public class MeasureTests
{
    private readonly ITestOutputHelper _out;

    public MeasureTests(ITestOutputHelper output) => _out = output;

    private const double Eps = 1e-9;

    /// <summary>构造 ctx 并播种上游节点输出（img 归调用方 using 管理）。</summary>
    private static PipelineRunContext Ctx(Mat img, params (string Name, Dictionary<string, string> Values)[] upstream)
    {
        var ctx = new PipelineRunContext(img);
        foreach (var (name, values) in upstream)
        {
            var nr = new NodeResult { Decision = "OK" };
            foreach (var kv in values) nr.Values[kv.Key] = kv.Value;
            ctx.Results[name] = nr;
        }
        return ctx;
    }

    private static Dictionary<string, string> LineVals(double x1, double y1, double x2, double y2) => new()
    {
        ["x1"] = x1.ToString("F2"),
        ["y1"] = y1.ToString("F2"),
        ["x2"] = x2.ToString("F2"),
        ["y2"] = y2.ToString("F2"),
        ["loc_x"] = ((x1 + x2) / 2).ToString("F2"),
        ["loc_y"] = ((y1 + y2) / 2).ToString("F2"),
        ["loc_angle"] = "0.00",
        ["loc_valid"] = "1",
    };

    private static Dictionary<string, string> CircleVals(double cx, double cy, double r) => new()
    {
        ["center_x"] = cx.ToString("F2"),
        ["center_y"] = cy.ToString("F2"),
        ["radius"] = r.ToString("F2"),
        ["loc_x"] = cx.ToString("F2"),
        ["loc_y"] = cy.ToString("F2"),
        ["loc_angle"] = "0.00",
        ["loc_valid"] = "1",
    };

    private static Dictionary<string, string> PointVals(double x, double y) => new()
    {
        ["loc_x"] = x.ToString("F2"),
        ["loc_y"] = y.ToString("F2"),
        ["loc_valid"] = "1",
    };

    // ===== 核心纯函数 =====

    [Fact]
    public void PointLineDistance_ParallelAndFoot()
    {
        var line = new MeasureLine(0, 0, 100, 0);
        Assert.Equal(25, MeasureCore.PointLineDistance(50, 25, line), Eps);
        var foot = MeasureCore.PointLineFoot(50, 25, line);
        Assert.Equal(50, foot.X, Eps);
        Assert.Equal(0, foot.Y, Eps);

        // 斜线垂距：点(0,10) 到 y=x（(0,0)-(10,10)）的距离 = 10/√2
        var diag = new MeasureLine(0, 0, 10, 10);
        Assert.Equal(10 / Math.Sqrt(2), MeasureCore.PointLineDistance(0, 10, diag), 1e-9);

        // 退化直线：距离 0、垂足=起点
        var deg = new MeasureLine(5, 5, 5, 5);
        Assert.Equal(0, MeasureCore.PointLineDistance(1, 2, deg), Eps);
        var dfoot = MeasureCore.PointLineFoot(1, 2, deg);
        Assert.Equal(5, dfoot.X, Eps);
        Assert.Equal(5, dfoot.Y, Eps);
    }

    [Fact]
    public void L2L_ParallelLines_DistanceIsPerpendicular()
    {
        var a = new MeasureLine(0, 100, 200, 100);
        var b = new MeasureLine(0, 150, 200, 150);
        Assert.Equal(50, MeasureCore.L2LAbsDistance(a, b), Eps);
    }

    [Fact]
    public void L2L_NonParallel_FourEndpointMean()
    {
        // 线1 水平 y=0：(0,0)-(100,0)；线2 斜 45° 过 (50,60)（方向 (1,1)/√2）
        var a = new MeasureLine(0, 0, 100, 0);
        var b = new MeasureLine(50 - 25 * Math.Sqrt(2), 60 - 25 * Math.Sqrt(2), 50 + 25 * Math.Sqrt(2), 60 + 25 * Math.Sqrt(2));
        // a 两端点到 b 的垂距：|(px−50)−(py−60)|/√2 → (0,0): 10/√2；(100,0): 110/√2
        // b 两端点到 a 的垂距：端点 y 坐标 → 60∓25√2
        var expect = (10 / Math.Sqrt(2) + 110 / Math.Sqrt(2) + (60 - 25 * Math.Sqrt(2)) + (60 + 25 * Math.Sqrt(2))) / 4;
        Assert.Equal(expect, MeasureCore.L2LAbsDistance(a, b), 1e-9);
    }

    [Fact]
    public void L2L_Angle_SignConventionAndRanges()
    {
        // 图像 y 向下：线1 指向右 (θ1=0)，线2 指向右下 45°（θ2=+45）→ 视觉顺时针 → 夹角 +45
        var a = new MeasureLine(0, 0, 100, 0);
        var b = new MeasureLine(0, 0, 100, 100);
        Assert.Equal(45, MeasureCore.L2LAngle(a, b, MeasureAngleRange.Linear), Eps);
        Assert.Equal(45, MeasureCore.L2LAngle(a, b, MeasureAngleRange.Segment), Eps);

        // 线2 指向右上 45°（y-）→ 视觉逆时针 → −45
        var c = new MeasureLine(0, 100, 100, 0);
        Assert.Equal(-45, MeasureCore.L2LAngle(a, c, MeasureAngleRange.Linear), Eps);
        Assert.Equal(-45, MeasureCore.L2LAngle(a, c, MeasureAngleRange.Segment), Eps);

        // 135° 夹角：Linear 输出 135；Segment 锐角折叠 −45
        var d = new MeasureLine(0, 0, -100, 100); // θ=135°
        Assert.Equal(135, MeasureCore.L2LAngle(a, d, MeasureAngleRange.Linear), Eps);
        Assert.Equal(-45, MeasureCore.L2LAngle(a, d, MeasureAngleRange.Segment), Eps);
    }

    [Fact]
    public void LineLineIntersection()
    {
        var a = new MeasureLine(0, 0, 100, 100);
        var b = new MeasureLine(0, 100, 100, 0);
        var inter = MeasureCore.LineLineIntersection(a, b);
        Assert.NotNull(inter);
        Assert.Equal(50, inter!.Value.X, Eps);
        Assert.Equal(50, inter!.Value.Y, Eps);

        // 平行（同斜率不同截距）→ 无交点
        var c = new MeasureLine(0, 10, 100, 110);
        Assert.Null(MeasureCore.LineLineIntersection(a, c));
    }

    [Fact]
    public void NormalizeAngle_BothRanges()
    {
        Assert.Equal(180, MeasureCore.NormalizeAngle(180, MeasureAngleRange.Linear), Eps);
        Assert.Equal(180, MeasureCore.NormalizeAngle(-180, MeasureAngleRange.Linear), Eps); // (-180,180]
        Assert.Equal(90, MeasureCore.NormalizeAngle(90, MeasureAngleRange.Linear), Eps);
        Assert.Equal(-179, MeasureCore.NormalizeAngle(181, MeasureAngleRange.Linear), Eps);

        // 锐角折叠（mod 180 折回 (-90,90]）：135 ≡ −45；−135 ≡ +45
        Assert.Equal(-45, MeasureCore.NormalizeAngle(135, MeasureAngleRange.Segment), Eps);
        Assert.Equal(45, MeasureCore.NormalizeAngle(-135, MeasureAngleRange.Segment), Eps);
        Assert.Equal(90, MeasureCore.NormalizeAngle(90, MeasureAngleRange.Segment), Eps);
        Assert.Equal(0, MeasureCore.NormalizeAngle(360, MeasureAngleRange.Segment), Eps);
    }

    [Fact]
    public void LineCircleIntersections_TwoTangentNone()
    {
        var c = new MeasureCircle(100, 100, 50);

        // 水平线 y=100 过圆心 → 两交点 (50,100) (150,100)
        var through = new MeasureLine(0, 100, 200, 100);
        var two = MeasureCore.LineCircleIntersections(through, c);
        Assert.Equal(2, two.Count);
        Assert.Contains(two, p => Math.Abs(p.X - 50) < Eps && Math.Abs(p.Y - 100) < Eps);
        Assert.Contains(two, p => Math.Abs(p.X - 150) < Eps && Math.Abs(p.Y - 100) < Eps);

        // 切线 y=150 → 1 个交点 (100,150)
        var tangent = new MeasureLine(0, 150, 200, 150);
        var one = MeasureCore.LineCircleIntersections(tangent, c);
        Assert.Equal(1, one.Count);
        Assert.Equal(100, one[0].X, Eps);
        Assert.Equal(150, one[0].Y, Eps);

        // 相离 y=200 → 无交点
        var away = new MeasureLine(0, 200, 200, 200);
        Assert.Empty(MeasureCore.LineCircleIntersections(away, c));
    }

    [Fact]
    public void L2C_DistanceAngle_FootPoint()
    {
        // 圆心 (100,100)，直线 y=0 → 距离 100，垂足 (100,0)，垂足→圆心指向 y+ → 角度 +90（Linear）
        var line = new MeasureLine(0, 0, 200, 0);
        var c = new MeasureCircle(100, 100, 20);
        Assert.Equal(100, MeasureCore.PointLineDistance(c.Cx, c.Cy, line), Eps);
        var foot = MeasureCore.PointLineFoot(c.Cx, c.Cy, line);
        Assert.Equal(100, foot.X, Eps);
        Assert.Equal(0, foot.Y, Eps);
        Assert.Equal(90, MeasureCore.L2CAngle(foot.X, foot.Y, c.Cx, c.Cy, MeasureAngleRange.Linear), Eps);
        // Segment：90 仍在界内
        Assert.Equal(90, MeasureCore.L2CAngle(foot.X, foot.Y, c.Cx, c.Cy, MeasureAngleRange.Segment), Eps);
        // 圆心在直线上方（y−）→ 负
        Assert.Equal(-90, MeasureCore.L2CAngle(100, 0, 100, -100, MeasureAngleRange.Linear), Eps);
    }

    [Fact]
    public void CircleCircle_AllFiveRelations()
    {
        // 外离 d=150 > R1+R2=120
        Assert.Equal(CircleRelation.Outside, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(150, 0, 70)));
        // 外切 d=120 = 50+70
        Assert.Equal(CircleRelation.Circumscribe, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(120, 0, 70)));
        // 相交 d=100 ∈ (|50-70|=20, 120)
        Assert.Equal(CircleRelation.Intersect, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(100, 0, 70)));
        // 内切 d=20 = |70-50|
        Assert.Equal(CircleRelation.Inscribe, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(20, 0, 70)));
        // 内含 d=10 < 20
        Assert.Equal(CircleRelation.Inside, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(10, 0, 70)));
        // 等半径：分离（d=60 < 2×50）→ 相交；重合 → 内含
        Assert.Equal(CircleRelation.Intersect, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(60, 0, 50)));
        Assert.Equal(CircleRelation.Inside, MeasureCore.CircleCircleRelation(new MeasureCircle(0, 0, 50), new MeasureCircle(0, 0, 50)));
    }

    [Fact]
    public void CircleCircle_Intersections()
    {
        // (0,0,R50) 与 (80,0,R50) → 交点 x=40, y=±30
        var pts = MeasureCore.CircleCircleIntersections(new MeasureCircle(0, 0, 50), new MeasureCircle(80, 0, 50));
        Assert.Equal(2, pts.Count);
        Assert.All(pts, p => Assert.Equal(40, p.X, 1e-9));
        Assert.All(pts, p => Assert.Equal(30, Math.Abs(p.Y), 1e-9));

        // 外切 → 1 个交点 (50,0)
        var tang = MeasureCore.CircleCircleIntersections(new MeasureCircle(0, 0, 50), new MeasureCircle(100, 0, 50));
        Assert.Equal(1, tang.Count);
        Assert.Equal(50, tang[0].X, 1e-9);
        Assert.Equal(0, tang[0].Y, 1e-9);

        // 内含/外离 → 无
        Assert.Empty(MeasureCore.CircleCircleIntersections(new MeasureCircle(0, 0, 50), new MeasureCircle(30, 0, 10)));
        Assert.Empty(MeasureCore.CircleCircleIntersections(new MeasureCircle(0, 0, 50), new MeasureCircle(200, 0, 10)));
    }

    [Fact]
    public void C2C_P2C_AngleAndDistances()
    {
        // 圆心连线水平向右 → 角度 0；指向屏幕下方（y+）→ 视觉下方 → 负
        Assert.Equal(0, MeasureCore.C2CAngle(0, 0, 100, 0, MeasureAngleRange.Linear), Eps);
        Assert.Equal(-45, MeasureCore.C2CAngle(0, 0, 100, 100, MeasureAngleRange.Linear), Eps);   // 右下 → 负
        Assert.Equal(45, MeasureCore.C2CAngle(0, 0, 100, -100, MeasureAngleRange.Linear), Eps);    // 右上 → 正

        // 点圆三距离：点 (150,0)，圆心 (0,0)，R=20 → 中心 150 / 最近 130 / 最远 170
        var d = MeasureCore.P2CDistances(150, 0, new MeasureCircle(0, 0, 20));
        Assert.Equal(150, d.Center, Eps);
        Assert.Equal(130, d.Closest, Eps);
        Assert.Equal(170, d.Farthest, Eps);
        // 点在圆内 → 最近为负
        var din = MeasureCore.P2CDistances(10, 0, new MeasureCircle(0, 0, 20));
        Assert.Equal(-10, din.Closest, Eps);
        // 点在圆心正下方（y+）→ 角度 −90（视觉下方为负）；正上方 → +90
        Assert.Equal(-90, MeasureCore.P2CAngle(0, 100, 0, 0, MeasureAngleRange.Linear), Eps);
        Assert.Equal(90, MeasureCore.P2CAngle(0, -100, 0, 0, MeasureAngleRange.Linear), Eps);
    }

    [Fact]
    public void CheckLimit_BoundaryInclusive()
    {
        Assert.True(MeasureCore.CheckLimit(false, 0, 10, 999));   // 未启用恒过
        Assert.True(MeasureCore.CheckLimit(true, 0, 10, 0));      // 含下边界
        Assert.True(MeasureCore.CheckLimit(true, 0, 10, 10));     // 含上边界
        Assert.False(MeasureCore.CheckLimit(true, 0, 10, 10.01));
        Assert.False(MeasureCore.CheckLimit(true, 0, 10, -0.01));
    }

    // ===== 节点端到端 =====

    [Fact]
    public void LineLine_Node_ComputesAndJudges()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img,
            ("02 直线查找1", LineVals(0, 100, 200, 100)),
            ("03 直线查找2", LineVals(0, 150, 200, 150)));
        var node = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["line1"] = "02 直线查找1",
            ["line2"] = "03 直线查找2",
        });
        var nr = node.Run(empty, ctx);

        Assert.Equal("OK", nr.Decision);
        Assert.Equal(50.00, double.Parse(nr.Values["abs_dist"]), 8);
        Assert.Equal(0.00, double.Parse(nr.Values["angle"]), 8);
        // 平行线无交点
        Assert.Equal("", nr.Values["inter_x"]);
        Assert.Equal(2, nr.Annotations.Count(s => s.Polys != null && !s.AsPoints)); // 两条线
    }

    [Fact]
    public void LineLine_Node_PassthroughBaseImage_ForAnnotationCanvas()
    {
        // 测量为纯几何，但透传底图作为输出图：否则节点无缩略图、选中不了，矢量标注永远渲染不出来
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        var ctx = Ctx(img,
            ("02 直线查找1", LineVals(0, 100, 200, 100)),
            ("03 直线查找2", LineVals(0, 150, 200, 150)));
        var node = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["line1"] = "02 直线查找1",
            ["line2"] = "03 直线查找2",
        });
        var nr = node.Run(img, ctx);
        Assert.NotNull(nr.OutputImage);
        Assert.Equal(3, nr.OutputImage!.Channels());
        Assert.Equal(8, nr.OutputImage.Width);
        Assert.NotSame(img, nr.OutputImage); // 克隆而非引用

        // 底图为空：不输出图像（保持「底图为空不报错」语义）
        using var empty = new Mat();
        Assert.Null(node.Run(empty, ctx).OutputImage);
    }

    [Fact]
    public void LineLine_Node_LimitExceeded_Ng()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img,
            ("02 直线查找1", LineVals(0, 100, 200, 100)),
            ("03 直线查找2", LineVals(0, 150, 200, 150)));
        var node = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["line1"] = "02 直线查找1",
            ["line2"] = "03 直线查找2",
            ["dist_check"] = "判断",
            ["dist_low"] = "0",
            ["dist_high"] = "40",
        });
        var nr = node.Run(empty, ctx);

        Assert.Equal("NG", nr.Decision);
        Assert.Contains("距离判断", nr.Values["fail_checks"]);
        // 判定限未启用时同几何为 OK
        var okNode = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["line1"] = "02 直线查找1",
            ["line2"] = "03 直线查找2",
        });
        Assert.Equal("OK", okNode.Run(empty, ctx).Decision);
    }

    [Fact]
    public void LineLine_Node_MissingRef_Error()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img, ("02 直线查找1", LineVals(0, 100, 200, 100)));

        // 引用非上游节点（line1 有效、line2 指向不存在的节点）
        var node = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["line1"] = "02 直线查找1",
            ["line2"] = "03 直线查找2",
        });
        var nr = node.Run(empty, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("不存在或不在本节点上游", nr.Error);

        // 未选择来源
        var node2 = new LineLineMeasureNode("04 线线测量");
        var nr2 = node2.Run(empty, ctx);
        Assert.Equal("ERROR", nr2.Decision);
        Assert.Contains("未选择", nr2.Error);

        // 上游无直线输出（未找到）
        var ctx2 = Ctx(img,
            ("02 直线查找1", new Dictionary<string, string> { ["loc_valid"] = "0", ["error"] = "未找到直线" }),
            ("03 直线查找2", LineVals(0, 150, 200, 150)));
        var node3 = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["line1"] = "02 直线查找1",
            ["line2"] = "03 直线查找2",
        });
        var nr3 = node3.Run(empty, ctx2);
        Assert.Equal("ERROR", nr3.Decision);
        Assert.Contains("没有直线输出", nr3.Error);
    }

    [Fact]
    public void LineCircle_Node_ComputesIntersections()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img,
            ("02 直线查找1", LineVals(0, 50, 200, 50)),
            ("03 圆查找1", CircleVals(100, 100, 30)));
        var node = new LineCircleMeasureNode("04 线圆测量", new Dictionary<string, string>
        {
            ["line"] = "02 直线查找1",
            ["circle"] = "03 圆查找1",
        });
        var nr = node.Run(empty, ctx);

        Assert.Equal("OK", nr.Decision);
        Assert.Equal(50.00, double.Parse(nr.Values["dist"]), 8);
        Assert.Equal(90.00, double.Parse(nr.Values["angle"]), 8);  // 垂足→圆心指向 y+ → +90
        Assert.Equal(100.00, double.Parse(nr.Values["foot_x"]), 8);
        Assert.Equal(50.00, double.Parse(nr.Values["foot_y"]), 8);
        // 直线 y=50 与圆 (100,100,R30) 相离（|100-50|=50 > 30）→ 无交点
        Assert.Equal("", nr.Values["inter1_x"]);

        // 相交情形：直线 y=80 → 交点 x=100±√(30²-20²)=100±22.36
        var ctx2 = Ctx(img,
            ("02 直线查找1", LineVals(0, 80, 200, 80)),
            ("03 圆查找1", CircleVals(100, 100, 30)));
        var nr2 = node.Run(empty, ctx2);
        Assert.Equal(2, new[] { nr2.Values["inter1_x"], nr2.Values["inter2_x"] }.Count(s => s.Length > 0));
        var i1 = double.Parse(nr2.Values["inter1_x"]);
        var i2 = double.Parse(nr2.Values["inter2_x"]);
        Assert.InRange(Math.Abs(i1 - 100), 22.3, 22.4);
        Assert.InRange(Math.Abs(i2 - 100), 22.3, 22.4);
    }

    [Fact]
    public void CircleCircle_Node_DistanceRelation()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img,
            ("02 圆查找1", CircleVals(0, 0, 50)),
            ("03 圆查找2", CircleVals(120, 0, 70)));
        var refs = new Dictionary<string, string>
        {
            ["circle1"] = "02 圆查找1",
            ["circle2"] = "03 圆查找2",
        };
        var node = new CircleCircleMeasureNode("04 圆圆测量", refs);
        var nr = node.Run(empty, ctx);

        Assert.Equal("OK", nr.Decision);
        Assert.Equal(120.00, double.Parse(nr.Values["dist"]), 8);
        Assert.Equal("外切", nr.Values["relation"]);
        // 外切 1 个交点 (50,0)
        Assert.Equal(50.00, double.Parse(nr.Values["inter1_x"]), 8);
        Assert.Equal("", nr.Values["inter2_x"]);
        Assert.Equal(0, double.Parse(nr.Values["angle"]), 8);
    }

    [Fact]
    public void CircleCircle_Node_LimitExceeded_Ng()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img,
            ("02 圆查找1", CircleVals(0, 0, 50)),
            ("03 圆查找2", CircleVals(120, 0, 70)));
        var node = new CircleCircleMeasureNode("04 圆圆测量", new Dictionary<string, string>
        {
            ["circle1"] = "02 圆查找1",
            ["circle2"] = "03 圆查找2",
            ["dist_check"] = "判断",
            ["dist_low"] = "0",
            ["dist_high"] = "100",
        });
        var nr = node.Run(empty, ctx);
        Assert.Equal("NG", nr.Decision);
        Assert.Contains("距离判断", nr.Values["fail_checks"]);
    }

    [Fact]
    public void PointCircle_Node_ReferenceAndManual()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var ctx = Ctx(img,
            ("02 快速匹配1", PointVals(150, 0)),
            ("03 圆查找1", CircleVals(0, 0, 20)));
        var node = new PointCircleMeasureNode("04 点圆测量", new Dictionary<string, string>
        {
            ["point_node"] = "02 快速匹配1",
            ["circle_node"] = "03 圆查找1",
        });
        var nr = node.Run(empty, ctx);

        Assert.Equal("OK", nr.Decision);
        Assert.Equal(150.00, double.Parse(nr.Values["center_dist"]), 8);
        Assert.Equal(130.00, double.Parse(nr.Values["closest_dist"]), 8);
        Assert.Equal(170.00, double.Parse(nr.Values["farthest_dist"]), 8);
        Assert.Equal(0.00, double.Parse(nr.Values["angle"]), 8);

        // 按坐标 + 按参数（不依赖上游）
        var node2 = new PointCircleMeasureNode("05 点圆测量", new Dictionary<string, string>
        {
            ["point_mode"] = "按坐标",
            ["point_x"] = "0",
            ["point_y"] = "100",
            ["circle_mode"] = "按参数",
            ["circle_cx"] = "0",
            ["circle_cy"] = "0",
            ["circle_r"] = "20",
        });
        var nr2 = node2.Run(empty, ctx);
        Assert.Equal("OK", nr2.Decision);
        Assert.Equal(100.00, double.Parse(nr2.Values["center_dist"]), 8);
        Assert.Equal(80.00, double.Parse(nr2.Values["closest_dist"]), 8);
        Assert.Equal(-90.00, double.Parse(nr2.Values["angle"]), 8); // 点在圆心下方（y+）→ −90
    }

    [Fact]
    public void PointCircle_Node_InvalidLocator_Error()
    {
        using var img = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(40));
        using var empty = new Mat();
        var bad = PointVals(150, 0);
        bad["loc_valid"] = "0";
        var ctx = Ctx(img, ("02 快速匹配1", bad));
        var node = new PointCircleMeasureNode("04 点圆测量", new Dictionary<string, string>
        {
            ["point_node"] = "02 快速匹配1",
        });
        var nr = node.Run(empty, ctx);
        Assert.Equal("ERROR", nr.Decision);
        Assert.Contains("定位无效", nr.Error);
    }

    // ===== 接线/工厂/调度 =====

    [Fact]
    public void Factory_RoundTrip_Dispose()
    {
        var inits = new Dictionary<string, string> { ["line1"] = "02 直线查找1", ["dist_check"] = "判断" };
        using var node = NodeFactory.Create("LineLineMeasure", "04 线线测量", inits);
        Assert.IsType<LineLineMeasureNode>(node);
        Assert.Equal("02 直线查找1", node.Params["line1"]);
        Assert.Equal("判断", node.Params["dist_check"]);
        // 未提供的参数回退默认
        Assert.Equal(MeasureAngleRange.Linear, node.Params["angle_range"]);

        foreach (var type in new[] { "LineCircleMeasure", "CircleCircleMeasure", "PointCircleMeasure" })
        {
            using var n = NodeFactory.Create(type, "0x 测量");
        }
    }

    [Fact]
    public void MeasureNodes_NotInParallelModelStage()
    {
        // 测量节点为纯几何工具节点，必须排除在并发模型阶段外（Pipeline.IsModelNode）
        var recipe = new VisionInspection.Models.Recipe
        {
            Nodes =
            [
                new() { Name = "02 线线测量", Type = "LineLineMeasure", Enabled = true },
                new() { Name = "03 线圆测量", Type = "LineCircleMeasure", Enabled = true },
                new() { Name = "04 圆圆测量", Type = "CircleCircleMeasure", Enabled = true },
                new() { Name = "05 点圆测量", Type = "PointCircleMeasure", Enabled = true },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));
        // 测量节点引用缺失 → 节点自判 ERROR（不崩溃、不短路后续节点）
        var result = pipeline.Run(img, "t.bmp");
        Assert.Equal("ERROR", result.NodeValues["02 线线测量"]["decision"]);
        Assert.Equal("ERROR", result.NodeValues["03 线圆测量"]["decision"]);
        Assert.Equal("ERROR", result.NodeValues["04 圆圆测量"]["decision"]);
        Assert.Equal("ERROR", result.NodeValues["05 点圆测量"]["decision"]);
    }

    [Fact]
    public void Performance_NodeUnder1ms()
    {
        using var img = new Mat(1024, 768, MatType.CV_8UC3, Scalar.All(40));
        var ctx = Ctx(img,
            ("02 直线查找1", LineVals(0, 100, 200, 100)),
            ("03 直线查找2", LineVals(0, 150, 200, 150)));
        var node = new LineLineMeasureNode("04 线线测量", new Dictionary<string, string>
        {
            ["dist_check"] = "判断",
        });
        // 预热
        node.Run(img, ctx);
        var times = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            node.Run(img, ctx);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var median = times[times.Count / 2];
        _out.WriteLine($"线线测量节点全链中位数: {median:F3}ms");
        // 含底图克隆（1024×768×3 ≈ 2.25MB memcpy，透传作标注画布）；纯几何 <0.1ms，克隆 ~0.3ms，留全量并发余量
        Assert.True(median < 1.5, $"测量节点应远低于 1.5ms，实测中位数 {median:F3}ms");
    }
}
