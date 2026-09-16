using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 点圆测量节点（VM IMVSP2CMeasureModu 式，订阅模式）：点（引用上游定位节点的 loc_x/loc_y 契约，
/// 或按坐标手填）+ 圆（引用上游圆查找，或按参数手填圆心半径），
/// 输出 中心距离/最近距离（点到圆周最近，点在圆内为负）/最远距离/角度（圆心→点连线，水平线下方为负）。
/// 判定限（中心距离/最近距离/最远距离/角度 共 4 组，独立开关）全过 → OK，任一超限 → NG；
/// 引用缺失/定位无效 → ERROR 停线。
/// </summary>
public sealed class PointCircleMeasureNode : MeasureNodeBase
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "point_mode", Label = "点输入方式", Kind = "choice", Default = "引用节点", Choices = ["引用节点", "按坐标"] },
        new ParamDef { Key = "point_node", Label = "点来源", Kind = "noderesult", Default = "", SourceTypes = ["ContourMatch", "FastMatch", "LineFind", "CircleFind"] },
        new ParamDef { Key = "point_x", Label = "点X(按坐标模式)", Kind = "double", Default = "0" },
        new ParamDef { Key = "point_y", Label = "点Y(按坐标模式)", Kind = "double", Default = "0" },
        new ParamDef { Key = "circle_mode", Label = "圆输入方式", Kind = "choice", Default = "引用节点", Choices = ["引用节点", "按参数"] },
        new ParamDef { Key = "circle_node", Label = "圆来源", Kind = "noderesult", Default = "", SourceTypes = ["CircleFind"] },
        new ParamDef { Key = "circle_cx", Label = "圆心X(按参数模式)", Kind = "double", Default = "0" },
        new ParamDef { Key = "circle_cy", Label = "圆心Y(按参数模式)", Kind = "double", Default = "0" },
        new ParamDef { Key = "circle_r", Label = "半径(按参数模式)", Kind = "double", Default = "0" },
        new ParamDef { Key = "angle_range", Label = "输出角度范围", Kind = "choice", Default = MeasureAngleRange.Linear, Choices = [MeasureAngleRange.Linear, MeasureAngleRange.Segment] },
        new ParamDef { Key = "center_check", Label = "中心距离判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "center_low", Label = "中心距离下限(px)", Kind = "double", Default = "0" },
        new ParamDef { Key = "center_high", Label = "中心距离上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "closest_check", Label = "最近距离判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "closest_low", Label = "最近距离下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "closest_high", Label = "最近距离上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "farthest_check", Label = "最远距离判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "farthest_low", Label = "最远距离下限(px)", Kind = "double", Default = "0" },
        new ParamDef { Key = "farthest_high", Label = "最远距离上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "angle_check", Label = "角度判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "angle_low", Label = "角度下限(°)", Kind = "double", Default = "-180" },
        new ParamDef { Key = "angle_high", Label = "角度上限(°)", Kind = "double", Default = "180" },
    ];

    private static Dictionary<string, string> Defaults() => new()
    {
        ["point_mode"] = "引用节点",
        ["point_node"] = "",
        ["point_x"] = "0",
        ["point_y"] = "0",
        ["circle_mode"] = "引用节点",
        ["circle_node"] = "",
        ["circle_cx"] = "0",
        ["circle_cy"] = "0",
        ["circle_r"] = "0",
        ["angle_range"] = MeasureAngleRange.Linear,
        ["center_check"] = "不判断",
        ["center_low"] = "0",
        ["center_high"] = "99999",
        ["closest_check"] = "不判断",
        ["closest_low"] = "-99999",
        ["closest_high"] = "99999",
        ["farthest_check"] = "不判断",
        ["farthest_low"] = "0",
        ["farthest_high"] = "99999",
        ["angle_check"] = "不判断",
        ["angle_low"] = "-180",
        ["angle_high"] = "180",
    };

    public PointCircleMeasureNode(string name, Dictionary<string, string>? init = null)
        : base(name, init, Defaults())
    {
    }

    public override string Type => "PointCircleMeasure";
    public override IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;

    public override NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var values = new Dictionary<string, string>();
        var baseImg = BuildBaseImage(bgr); // 透传底图作标注画布（无图则 null）

        // 点来源：引用上游定位节点（loc_x/loc_y 契约）或按坐标手填
        (double X, double Y)? point;
        if (string.Equals(_params.GetValueOrDefault("point_mode"), "按坐标", StringComparison.Ordinal))
        {
            point = (P("point_x", 0), P("point_y", 0));
        }
        else
        {
            var vp = ResolveRef("point_node", "点", ctx, out var pointName, out var ep);
            if (vp == null) return ErrorResult(values, ep!, baseImg);
            point = ExtractPoint(vp, pointName!, out var ep2);
            if (point == null) return ErrorResult(values, ep2!, baseImg);
        }

        // 圆来源：引用上游圆查找（center_x/center_y/radius）或按参数手填
        MeasureCircle? circle;
        if (string.Equals(_params.GetValueOrDefault("circle_mode"), "按参数", StringComparison.Ordinal))
        {
            circle = new MeasureCircle(P("circle_cx", 0), P("circle_cy", 0), P("circle_r", 0));
            if (circle.Value.R <= 0)
            {
                return ErrorResult(values, "按参数模式半径必须大于 0（请填写 circle_r）", baseImg);
            }
        }
        else
        {
            var vc = ResolveRef("circle_node", "圆", ctx, out var circleName, out var ec);
            if (vc == null) return ErrorResult(values, ec!, baseImg);
            circle = ExtractCircle(vc, circleName!, out var ec2);
            if (circle == null) return ErrorResult(values, ec2!, baseImg);
        }

        var c = circle.Value;
        var (center, closest, farthest) = MeasureCore.P2CDistances(point.Value.X, point.Value.Y, c);
        var angle = MeasureCore.P2CAngle(point.Value.X, point.Value.Y, c.Cx, c.Cy, AngleRange);

        values["center_dist"] = F(center);
        values["closest_dist"] = F(closest);
        values["farthest_dist"] = F(farthest);
        values["angle"] = F(angle);
        values["point_x"] = F(point.Value.X);
        values["point_y"] = F(point.Value.Y);
        values["circle_x"] = F(c.Cx);
        values["circle_y"] = F(c.Cy);
        values["radius"] = F(c.R);

        var failed = new List<string>();
        PassLimit("center_check", "center_low", "center_high", center, "中心距离判断", failed);
        PassLimit("closest_check", "closest_low", "closest_high", closest, "最近距离判断", failed);
        PassLimit("farthest_check", "farthest_low", "farthest_high", farthest, "最远距离判断", failed);
        PassLimit("angle_check", "angle_low", "angle_high", angle, "角度判断", failed);

        var nr = BuildResult(values, failed, baseImg);

        // 矢量标注：测量圆 + 测量点 + 圆心→点距离段
        var okKind = nr.Decision != "NG" ? NodeShapeKind.Ok : NodeShapeKind.Defect;
        nr.Annotations.Add(CircleShape(c.Cx, c.Cy, c.R, $"中距{center:F2}", okKind));
        nr.Annotations.Add(LineShape(c.Cx, c.Cy, point.Value.X, point.Value.Y, null, okKind));
        nr.Annotations.Add(PointsShape(okKind, (point.Value.X, point.Value.Y), (c.Cx, c.Cy)));

        return nr;
    }
}
