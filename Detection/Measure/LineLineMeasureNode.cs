using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 线线测量节点（VM IMVSL2LMeasureModu 式，订阅模式）：引用上游两条直线（直线查找输出 x1/y1/x2/y2），
/// 输出 绝对距离（4端点平均垂距）/ 夹角 / 交点。判定限（距离/角度/交点X/交点Y，独立开关）全过 → OK，任一超限 → NG；
/// 上游未找到直线 → ERROR 停线。VM「绘制」模式（模块内卡尺自找线）不支持——找线用直线查找节点。
/// </summary>
public sealed class LineLineMeasureNode : MeasureNodeBase
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "line1", Label = "直线1来源", Kind = "noderesult", Default = "", SourceTypes = ["LineFind"] },
        new ParamDef { Key = "line2", Label = "直线2来源", Kind = "noderesult", Default = "", SourceTypes = ["LineFind"] },
        new ParamDef { Key = "angle_range", Label = "输出角度范围", Kind = "choice", Default = MeasureAngleRange.Linear, Choices = [MeasureAngleRange.Linear, MeasureAngleRange.Segment] },
        new ParamDef { Key = "dist_check", Label = "距离判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "dist_low", Label = "距离下限(px)", Kind = "double", Default = "0" },
        new ParamDef { Key = "dist_high", Label = "距离上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "angle_check", Label = "角度判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "angle_low", Label = "角度下限(°)", Kind = "double", Default = "-180" },
        new ParamDef { Key = "angle_high", Label = "角度上限(°)", Kind = "double", Default = "180" },
        new ParamDef { Key = "interx_check", Label = "交点X判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "interx_low", Label = "交点X下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "interx_high", Label = "交点X上限(px)", Kind = "double", Default = "99999" },
        new ParamDef { Key = "intery_check", Label = "交点Y判断", Kind = "choice", Default = "不判断", Choices = ["不判断", "判断"] },
        new ParamDef { Key = "intery_low", Label = "交点Y下限(px)", Kind = "double", Default = "-99999" },
        new ParamDef { Key = "intery_high", Label = "交点Y上限(px)", Kind = "double", Default = "99999" },
    ];

    private static Dictionary<string, string> Defaults() => new()
    {
        ["line1"] = "",
        ["line2"] = "",
        ["angle_range"] = MeasureAngleRange.Linear,
        ["dist_check"] = "不判断",
        ["dist_low"] = "0",
        ["dist_high"] = "99999",
        ["angle_check"] = "不判断",
        ["angle_low"] = "-180",
        ["angle_high"] = "180",
        ["interx_check"] = "不判断",
        ["interx_low"] = "-99999",
        ["interx_high"] = "99999",
        ["intery_check"] = "不判断",
        ["intery_low"] = "-99999",
        ["intery_high"] = "99999",
    };

    public LineLineMeasureNode(string name, Dictionary<string, string>? init = null)
        : base(name, init, Defaults())
    {
    }

    public override string Type => "LineLineMeasure";
    public override IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;

    public override NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var values = new Dictionary<string, string>();
        var baseImg = BuildBaseImage(bgr); // 透传底图作标注画布（无图则 null）

        var v1 = ResolveRef("line1", "直线1", ctx, out var name1, out var e1);
        if (v1 == null) return ErrorResult(values, e1!, baseImg);
        var line1 = ExtractLine(v1, name1!, out var le1);
        if (line1 == null) return ErrorResult(values, le1!, baseImg);

        var v2 = ResolveRef("line2", "直线2", ctx, out var name2, out var e2);
        if (v2 == null) return ErrorResult(values, e2!, baseImg);
        var line2 = ExtractLine(v2, name2!, out var le2);
        if (line2 == null) return ErrorResult(values, le2!, baseImg);

        var dist = MeasureCore.L2LAbsDistance(line1.Value, line2.Value);
        var angle = MeasureCore.L2LAngle(line1.Value, line2.Value, AngleRange);
        var inter = MeasureCore.LineLineIntersection(line1.Value, line2.Value);

        values["abs_dist"] = F(dist);
        values["angle"] = F(angle);
        values["inter_x"] = inter == null ? "" : F(inter.Value.X);
        values["inter_y"] = inter == null ? "" : F(inter.Value.Y);
        values["line1_x1"] = F(line1.Value.X1);
        values["line1_y1"] = F(line1.Value.Y1);
        values["line1_x2"] = F(line1.Value.X2);
        values["line1_y2"] = F(line1.Value.Y2);
        values["line1_angle"] = F(MeasureCore.NormalizeAngle(MeasureCore.DirectionAngleDeg(line1.Value), MeasureAngleRange.Segment));
        values["line2_x1"] = F(line2.Value.X1);
        values["line2_y1"] = F(line2.Value.Y1);
        values["line2_x2"] = F(line2.Value.X2);
        values["line2_y2"] = F(line2.Value.Y2);
        values["line2_angle"] = F(MeasureCore.NormalizeAngle(MeasureCore.DirectionAngleDeg(line2.Value), MeasureAngleRange.Segment));

        var failed = new List<string>();
        PassLimit("dist_check", "dist_low", "dist_high", dist, "距离判断", failed);
        PassLimit("angle_check", "angle_low", "angle_high", angle, "角度判断", failed);
        if (inter != null)
        {
            PassLimit("interx_check", "interx_low", "interx_high", inter.Value.X, "交点X判断", failed);
            PassLimit("intery_check", "intery_low", "intery_high", inter.Value.Y, "交点Y判断", failed);
        }
        else
        {
            // 平行/共线无交点：交点判定启用即不过（值不存在）
            if (CheckOn("interx_check")) failed.Add("交点X判断");
            if (CheckOn("intery_check")) failed.Add("交点Y判断");
        }

        var nr = BuildResult(values, failed, baseImg);

        // 矢量标注：两条被测线 + 交点 + 距离/角度标签（绿/红按判定）
        var okKind = nr.Decision != "NG" ? NodeShapeKind.Ok : NodeShapeKind.Defect;
        nr.Annotations.Add(LineShape(line1.Value.X1, line1.Value.Y1, line1.Value.X2, line1.Value.Y2,
            $"距{dist:F2} 角{angle:F2}°", okKind));
        nr.Annotations.Add(LineShape(line2.Value.X1, line2.Value.Y1, line2.Value.X2, line2.Value.Y2, null, okKind));
        if (inter != null)
        {
            nr.Annotations.Add(PointsShape(okKind, (inter.Value.X, inter.Value.Y)));
        }

        return nr;
    }
}
