using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 线圆测量节点（VM IMVSL2CMeasureModu 式，订阅模式）：引用上游一条直线（直线查找）+ 一个圆（圆查找），
/// 输出 距离（圆心到直线的垂距）/ 角度（垂足→圆心向量，指向图像 y+ 为正）/ 垂足 / 交点1,2（直线与圆）。
/// 判定限（距离/角度/交点1X,1Y/交点2X,2Y/垂足X,Y 共 8 组，独立开关）全过 → OK，任一超限 → NG；
/// 上游未找到线/圆 → ERROR 停线。VM「绘制」模式不支持——找线/找圆用直线查找/圆查找节点。
/// </summary>
public sealed class LineCircleMeasureNode : MeasureNodeBase
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "line", Label = "直线来源", Kind = "noderesult", Default = "", SourceTypes = ["LineFind"] },
        new ParamDef { Key = "circle", Label = "圆来源", Kind = "noderesult", Default = "", SourceTypes = ["CircleFind"] },
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
        new ParamDef { Key = "projx_check", Label = "垂足X判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "projx_low", Label = "垂足X下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "projx_high", Label = "垂足X上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "projy_check", Label = "垂足Y判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "projy_low", Label = "垂足Y下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "projy_high", Label = "垂足Y上限(px)", Kind = "double", Default = "99999" },
    ];

    private static Dictionary<string, string> Defaults()
    {
        var d = new Dictionary<string, string>
        {
            ["line"] = "",
            ["circle"] = "",
            ["angle_range"] = MeasureAngleRange.Linear,
        };
        foreach (var group in new[]
        {
            (Check: "dist_check", Low: "dist_low", High: "dist_high", DefLow: "0", DefHigh: "99999"),
            (Check: "angle_check", Low: "angle_low", High: "angle_high", DefLow: "-180", DefHigh: "180"),
        })
        {
            d[group.Check] = "不判断";
            d[group.Low] = group.DefLow;
            d[group.High] = group.DefHigh;
        }
        foreach (var key in new[]
        {
            "inter1x", "inter1y", "inter2x", "inter2y", "projx", "projy",
        })
        {
            d[key + "_check"] = "不判断";
            d[key + "_low"] = "-99999";
            d[key + "_high"] = "99999";
        }
        return d;
    }

    public LineCircleMeasureNode(string name, Dictionary<string, string>? init = null)
        : base(name, init, Defaults())
    {
    }

    public override string Type => "LineCircleMeasure";
    public override IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;

    public override NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var values = new Dictionary<string, string>();
        var baseImg = BuildBaseImage(bgr); // 透传底图作标注画布（无图则 null）

        var vl = ResolveRef("line", "直线", ctx, out var lineName, out var el);
        if (vl == null) return ErrorResult(values, el!, baseImg);
        var line = ExtractLine(vl, lineName!, out var el2);
        if (line == null) return ErrorResult(values, el2!, baseImg);

        var vc = ResolveRef("circle", "圆", ctx, out var circleName, out var ec);
        if (vc == null) return ErrorResult(values, ec!, baseImg);
        var circle = ExtractCircle(vc, circleName!, out var ec2);
        if (circle == null) return ErrorResult(values, ec2!, baseImg);

        var c = circle.Value;
        var dist = MeasureCore.PointLineDistance(c.Cx, c.Cy, line.Value);
        var foot = MeasureCore.PointLineFoot(c.Cx, c.Cy, line.Value);
        var angle = MeasureCore.L2CAngle(foot.X, foot.Y, c.Cx, c.Cy, AngleRange);
        var inters = MeasureCore.LineCircleIntersections(line.Value, c);

        values["dist"] = F(dist);
        values["angle"] = F(angle);
        values["foot_x"] = F(foot.X);
        values["foot_y"] = F(foot.Y);
        values["inter1_x"] = inters.Count > 0 ? F(inters[0].X) : "";
        values["inter1_y"] = inters.Count > 0 ? F(inters[0].Y) : "";
        values["inter2_x"] = inters.Count > 1 ? F(inters[1].X) : "";
        values["inter2_y"] = inters.Count > 1 ? F(inters[1].Y) : "";
        values["circle_x"] = F(c.Cx);
        values["circle_y"] = F(c.Cy);
        values["radius"] = F(c.R);
        values["line_angle"] = F(MeasureCore.NormalizeAngle(MeasureCore.DirectionAngleDeg(line.Value), MeasureAngleRange.Segment));

        var failed = new List<string>();
        PassLimit("dist_check", "dist_low", "dist_high", dist, "距离判断", failed);
        PassLimit("angle_check", "angle_low", "angle_high", angle, "角度判断", failed);
        CheckIntersection("inter1x_check", "inter1y_check", "交点1", inters, 0, failed);
        CheckIntersection("inter2x_check", "inter2y_check", "交点2", inters, 1, failed);
        PassLimit("projx_check", "projx_low", "projx_high", foot.X, "垂足X判断", failed);
        PassLimit("projy_check", "projy_low", "projy_high", foot.Y, "垂足Y判断", failed);

        var nr = BuildResult(values, failed, baseImg);

        // 矢量标注：被测线 + 测量圆 + 圆心→垂足距离段 + 垂足/交点
        var okKind = nr.Decision != "NG" ? NodeShapeKind.Ok : NodeShapeKind.Defect;
        nr.Annotations.Add(LineShape(line.Value.X1, line.Value.Y1, line.Value.X2, line.Value.Y2,
            $"距{dist:F2}", okKind));
        nr.Annotations.Add(CircleShape(c.Cx, c.Cy, c.R, null, okKind));
        nr.Annotations.Add(LineShape(foot.X, foot.Y, c.Cx, c.Cy, null, okKind));
        nr.Annotations.Add(PointsShape(okKind, (foot.X, foot.Y)));
        if (inters.Count > 0)
        {
            nr.Annotations.Add(PointsShape(okKind, inters.Select(p => (p.X, p.Y)).ToArray()));
        }

        return nr;
    }
}
