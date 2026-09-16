using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 圆圆测量节点（VM IMVSC2CMeasureModu 式，订阅模式）：引用上游两个圆（圆查找），
/// 输出 距离（两圆心连线长度）/ 角度（圆心连线与水平线夹角，水平线下方为负）/ 位置关系（内含/内切/相交/外切/外离）/ 交点1,2。
/// 判定限（距离/角度/交点1X,1Y/交点2X,2Y 共 6 组，独立开关）全过 → OK，任一超限 → NG；
/// 上游未找到圆 → ERROR 停线。VM「绘制」模式不支持——找圆用圆查找节点。
/// </summary>
public sealed class CircleCircleMeasureNode : MeasureNodeBase
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "circle1", Label = "圆1来源", Kind = "noderesult", Default = "", SourceTypes = ["CircleFind"] },
        new ParamDef { Key = "circle2", Label = "圆2来源", Kind = "noderesult", Default = "", SourceTypes = ["CircleFind"] },
        new ParamDef { Key = "angle_range", Label = "输出角度范围", Kind = "choice", Default = MeasureAngleRange.Linear, Choices = [MeasureAngleRange.Linear, MeasureAngleRange.Segment] },
        new ParamDef { Key = "dist_check", Label = "距离判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "dist_low", Label = "距离下限(px)", Kind = "double", Default = "0" },
        new ParamDef { Key = "dist_high", Label = "距离上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "angle_check", Label = "角度判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "angle_low", Label = "角度下限(°)", Kind = "double", Default = "-180" },
        new ParamDef { Key = "angle_high", Label = "角度上限(°)", Kind = "double", Default = "180" },
        new ParamDef { Key = "inter1x_check", Label = "交点1X判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "inter1x_low", Label = "交点1X下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "inter1x_high", Label = "交点1X上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "inter1y_check", Label = "交点1Y判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "inter1y_low", Label = "交点1Y下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "inter1y_high", Label = "交点1Y上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "inter2x_check", Label = "交点2X判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "inter2x_low", Label = "交点2X下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "inter2x_high", Label = "交点2X上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "inter2y_check", Label = "交点2Y判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "inter2y_low", Label = "交点2Y下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "inter2y_high", Label = "交点2Y上限(px)", Kind = "double", Default = "99999" },
    ];

    private static Dictionary<string, string> Defaults()
    {
        var d = new Dictionary<string, string>
        {
            ["circle1"] = "",
            ["circle2"] = "",
            ["angle_range"] = MeasureAngleRange.Linear,
        };
        foreach (var key in new[] { "dist", "angle", "inter1x", "inter1y", "inter2x", "inter2y" })
        {
            d[key + "_check"] = "不判断";
        }
        d["dist_low"] = "0";
        d["dist_high"] = "99999";
        d["angle_low"] = "-180";
        d["angle_high"] = "180";
        foreach (var key in new[] { "inter1x", "inter1y", "inter2x", "inter2y" })
        {
            d[key + "_low"] = "-99999";
            d[key + "_high"] = "99999";
        }
        return d;
    }

    public CircleCircleMeasureNode(string name, Dictionary<string, string>? init = null)
        : base(name, init, Defaults())
    {
    }

    public override string Type => "CircleCircleMeasure";
    public override IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;

    public override NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var values = new Dictionary<string, string>();
        var baseImg = BuildBaseImage(bgr); // 透传底图作标注画布（无图则 null）

        var v1 = ResolveRef("circle1", "圆1", ctx, out var name1, out var e1);
        if (v1 == null) return ErrorResult(values, e1!, baseImg);
        var c1 = ExtractCircle(v1, name1!, out var le1);
        if (c1 == null) return ErrorResult(values, le1!, baseImg);

        var v2 = ResolveRef("circle2", "圆2", ctx, out var name2, out var e2);
        if (v2 == null) return ErrorResult(values, e2!, baseImg);
        var c2 = ExtractCircle(v2, name2!, out var le2);
        if (c2 == null) return ErrorResult(values, le2!, baseImg);

        var a = c1.Value;
        var b = c2.Value;
        var dist = Math.Sqrt((b.Cx - a.Cx) * (b.Cx - a.Cx) + (b.Cy - a.Cy) * (b.Cy - a.Cy));
        var angle = MeasureCore.C2CAngle(a.Cx, a.Cy, b.Cx, b.Cy, AngleRange);
        var relation = MeasureCore.CircleCircleRelation(a, b);
        var inters = MeasureCore.CircleCircleIntersections(a, b);

        values["dist"] = F(dist);
        values["angle"] = F(angle);
        values["relation"] = relation;
        values["inter1_x"] = inters.Count > 0 ? F(inters[0].X) : "";
        values["inter1_y"] = inters.Count > 0 ? F(inters[0].Y) : "";
        values["inter2_x"] = inters.Count > 1 ? F(inters[1].X) : "";
        values["inter2_y"] = inters.Count > 1 ? F(inters[1].Y) : "";
        values["c1_x"] = F(a.Cx);
        values["c1_y"] = F(a.Cy);
        values["c1_r"] = F(a.R);
        values["c2_x"] = F(b.Cx);
        values["c2_y"] = F(b.Cy);
        values["c2_r"] = F(b.R);

        var failed = new List<string>();
        PassLimit("dist_check", "dist_low", "dist_high", dist, "距离判断", failed);
        PassLimit("angle_check", "angle_low", "angle_high", angle, "角度判断", failed);
        CheckIntersection("inter1x_check", "inter1y_check", "交点1", inters, 0, failed);
        CheckIntersection("inter2x_check", "inter2y_check", "交点2", inters, 1, failed);

        var nr = BuildResult(values, failed, baseImg);

        // 矢量标注：两个被测圆 + 圆心距线段 + 交点
        var okKind = nr.Decision != "NG" ? NodeShapeKind.Ok : NodeShapeKind.Defect;
        nr.Annotations.Add(CircleShape(a.Cx, a.Cy, a.R, $"距{dist:F2} {relation}", okKind));
        nr.Annotations.Add(CircleShape(b.Cx, b.Cy, b.R, null, okKind));
        nr.Annotations.Add(LineShape(a.Cx, a.Cy, b.Cx, b.Cy, null, okKind));
        nr.Annotations.Add(PointsShape(okKind, (a.Cx, a.Cy), (b.Cx, b.Cy)));
        if (inters.Count > 0)
        {
            nr.Annotations.Add(PointsShape(okKind, inters.Select(p => (p.X, p.Y)).ToArray()));
        }

        return nr;
    }
}
