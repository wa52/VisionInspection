using OpenCvSharp;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 位置修正节点（VisionMaster 位置修正同类，工具节点）：
/// 读取定位节点（须输出 loc_x/loc_y/loc_angle/loc_valid 标准契约键，当前=轮廓匹配）的位姿，
/// 与基准位姿（base_x/base_y/base_angle，参数面板「创建基准」从定位来源最近一次执行一键采集）求变换
/// （Δθ=当前角−基准角；p' = R(Δθ)·S·(p−P0)+P1，S=diag(scale_x,scale_y) 方向尺度），
/// 写入运行上下文 —— target_nodes 列出的下游节点解析 ROI 后应用该变换，检测区域随工件平移+旋转。
/// 选择方式（按点/按坐标）仅决定面板引用行的展示形式，数据同源（定位来源的匹配点X/Y）。
/// 定位失败（loc_valid≠1）或定位源缺失 → 本节点 ERROR（停线防误检）；未设基准 → 透传不修正。
/// 图像显示：show_base/show_run 控制结果图上是否画基准点/运行点十字标注。
/// </summary>
public sealed class PositionCorrectionNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "定位来源(引用上游定位节点)", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "select_mode", Label = "选择方式", Kind = "choice", Default = "按点", Choices = ["按点", "按坐标"] },
        new ParamDef { Key = "target_nodes", Label = "要修正ROI的节点(引用勾选)", Kind = "targets_ref", Default = "" },
        new ParamDef { Key = "scale_x", Label = "X方向尺度", Kind = "double", Default = "1" },
        new ParamDef { Key = "scale_y", Label = "Y方向尺度", Kind = "double", Default = "1" },
        new ParamDef { Key = "base_x", Label = "基准位姿X(创建基准自动写入)", Kind = "hidden", Default = "" },
        new ParamDef { Key = "base_y", Label = "基准位姿Y(创建基准自动写入)", Kind = "hidden", Default = "" },
        new ParamDef { Key = "base_angle", Label = "基准角度(创建基准自动写入)", Kind = "hidden", Default = "" },
        new ParamDef { Key = "show_base", Label = "显示基准点", Kind = "bool", Default = "1" },
        new ParamDef { Key = "show_run", Label = "显示运行点", Kind = "bool", Default = "1" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["select_mode"] = "按点",
        ["target_nodes"] = "",
        ["scale_x"] = "1",
        ["scale_y"] = "1",
        ["base_x"] = "",
        ["base_y"] = "",
        ["base_angle"] = "",
        ["show_base"] = "1",
        ["show_run"] = "1",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public PositionCorrectionNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "PositionCorrection";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    /// <summary>定位来源节点名（Pipeline 顺序校验用）。</summary>
    public string SourceName => (_params.GetValueOrDefault("source") ?? "").Trim();

    /// <summary>要修正 ROI 的下游节点名列表（Pipeline 顺序校验用）。</summary>
    public IReadOnlyList<string> TargetNodes => (_params.GetValueOrDefault("target_nodes") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Length > 0)
        .ToList();

    /// <summary>基准位姿是否已设置（三项都有有效数值）。</summary>
    public bool HasBasePose =>
        double.TryParse(_params.GetValueOrDefault("base_x"), out _) &&
        double.TryParse(_params.GetValueOrDefault("base_y"), out _) &&
        double.TryParse(_params.GetValueOrDefault("base_angle"), out _);

    /// <summary>选择方式（按点/按坐标）——仅影响面板引用行展示形式，运行数据同源。</summary>
    public string SelectMode => (_params.GetValueOrDefault("select_mode") ?? "按点").Trim() is "按坐标" ? "按坐标" : "按点";

    /// <summary>方向尺度（非法值回退 1）。</summary>
    public (double X, double Y) Scales =>
        (TryScale("scale_x"), TryScale("scale_y"));

    private double TryScale(string key) =>
        double.TryParse(_params.GetValueOrDefault(key), out var v) && v > 0 ? v : 1.0;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var source = SourceName;
        var passthrough = ResolvePassthroughImage(bgr, ctx, source);

        // 定位来源结果
        if (!ctx.Results.TryGetValue(source, out var sourceResult))
        {
            Log?.Invoke($"[位置修正] {Name}: 定位来源节点「{source}」不存在或不在本节点上游（调整流程顺序或「定位来源」参数）");
            return Fail(passthrough, $"定位来源节点「{source}」不存在或不在本节点上游");
        }
        var values = sourceResult.Values;
        if (!values.TryGetValue("loc_valid", out var valid))
        {
            return Fail(passthrough, $"定位来源「{source}」未输出 loc_* 标准键（不是定位节点；当前仅轮廓匹配支持）");
        }
        if (valid != "1")
        {
            Log?.Invoke($"[位置修正] {Name}: 定位失败（{source} loc_valid={valid}）→ 节点 ERROR，停止修正");
            return Fail(passthrough, $"定位失败：{source} 未找到有效位姿（loc_valid={valid}）");
        }
        if (!TryReadPose(values, out var curX, out var curY, out var curAngle))
        {
            return Fail(passthrough, $"定位来源「{source}」的 loc_x/loc_y/loc_angle 不是有效数值");
        }

        // 基准位姿
        if (!HasBasePose)
        {
            var pass = new NodeResult
            {
                Decision = "OK",
                OutputImage = passthrough,
                Values = { ["dx"] = "0", ["dy"] = "0", ["dangle"] = "0", ["base_set"] = "0" },
            };
            if (ParseFlag(_params.GetValueOrDefault("show_run")))
            {
                pass.Annotations.Add(Cross(curX, curY, NodeShapeKind.Ok, "运行位姿"));
            }
            Log?.Invoke($"[位置修正] {Name}: 未设置基准位姿（参数面板点「创建基准」或手填 base_x/base_y/base_angle），本轮不修正");
            return pass;
        }
        var baseX = double.Parse(_params.GetValueOrDefault("base_x"));
        var baseY = double.Parse(_params.GetValueOrDefault("base_y"));
        var baseAngle = double.Parse(_params.GetValueOrDefault("base_angle"));
        var (scaleX, scaleY) = Scales;

        var targets = TargetNodes;
        if (targets.Count == 0)
        {
            Log?.Invoke($"[位置修正] {Name}: 「要修正ROI的节点」未勾选，无节点跟随修正");
        }

        ctx.PoseCorrection = new PoseCorrection(Name, targets, baseX, baseY, baseAngle, curX, curY, curAngle, scaleX, scaleY);
        Log?.Invoke($"[位置修正] {Name}: 基准({baseX:F1},{baseY:F1},{baseAngle:F1}°) → 当前({curX:F1},{curY:F1},{curAngle:F1}°)，尺度({scaleX:F2},{scaleY:F2})，修正目标: {string.Join("、", targets)}");
        var result = new NodeResult
        {
            Decision = "OK",
            OutputImage = passthrough,
            Values =
            {
                ["dx"] = (curX - baseX).ToString("F2"),
                ["dy"] = (curY - baseY).ToString("F2"),
                ["dangle"] = (curAngle - baseAngle).ToString("F2"),
                ["base_set"] = "1",
                ["scale_x"] = scaleX.ToString("F3"),
                ["scale_y"] = scaleY.ToString("F3"),
                ["loc_x"] = curX.ToString("F2"),
                ["loc_y"] = curY.ToString("F2"),
                ["loc_angle"] = curAngle.ToString("F2"),
            },
        };
        if (ParseFlag(_params.GetValueOrDefault("show_base")))
        {
            result.Annotations.Add(Cross(baseX, baseY, NodeShapeKind.Info, "基准位姿"));
        }
        if (ParseFlag(_params.GetValueOrDefault("show_run")))
        {
            result.Annotations.Add(Cross(curX, curY, NodeShapeKind.Ok, "运行位姿"));
        }
        return result;
    }

    /// <summary>十字标注（屏幕常量渲染的两段线，臂长取图像宽高较小边的 1.5%）。</summary>
    private static NodeShape Cross(double x, double y, NodeShapeKind kind, string label)
    {
        var r = 18.0;
        return new NodeShape
        {
            Polys = new Point[][]
            {
                new[] { new Point(x - r, y), new Point(x + r, y) },
                new[] { new Point(x, y - r), new Point(x, y + r) },
            },
            Kind = kind,
            Label = label,
        };
    }

    private static bool ParseFlag(string? value) =>
        value is null || (double.TryParse(value, out var v) ? v != 0 : value is "是" or "true" or "True");

    private static bool TryReadPose(IReadOnlyDictionary<string, string> values, out double x, out double y, out double angle)
    {
        x = y = angle = 0;
        return double.TryParse(values.GetValueOrDefault("loc_x"), out x) &&
               double.TryParse(values.GetValueOrDefault("loc_y"), out y) &&
               double.TryParse(values.GetValueOrDefault("loc_angle"), out angle);
    }

    /// <summary>
    /// 透传图：定位节点的输出图（下游「图像来源」可指向本节点）。必须克隆——节点结果图所有权移交 UI，
    /// 直接共享上游/原图 Mat 实例会因 UI 释放上游图而悬空（Cannot access a disposed object）。
    /// </summary>
    private static Mat ResolvePassthroughImage(Mat bgr, PipelineRunContext ctx, string source)
    {
        var src = source != "@input" && ctx.Images.TryGetValue(source, out var im) && !im.Empty() ? im : bgr;
        return src.Clone();
    }

    private static NodeResult Fail(Mat image, string error) => new()
    {
        Decision = "ERROR",
        Error = error,
        OutputImage = image,
    };

    public void Dispose() { }
}
