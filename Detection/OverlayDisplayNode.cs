using System.Globalization;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 叠加显示节点：显式选择上游节点，把其已有 ROI/检测标注和判定摘要汇总到一张图上。
/// overlay_nodes 为空时不叠加任何节点，避免默认把整条流程的结果混在一起。
/// </summary>
public sealed class OverlayDisplayNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "overlay_nodes", Label = "叠加节点（勾选）", Kind = "nodechecklist", Default = "" },
        new ParamDef { Key = "show_decision", Label = "显示判定结果", Kind = "bool", Default = "true" },
        new ParamDef { Key = "show_values", Label = "显示结果数值", Kind = "bool", Default = "true" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["overlay_nodes"] = "",
        ["show_decision"] = "true",
        ["show_values"] = "true",
    };

    public Action<string>? Log { get; set; }

    public OverlayDisplayNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (key, value) in init) _params[key] = value;
        }
    }

    public string Name { get; set; }
    public string Type => "OverlayDisplay";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var sourceName = _params.GetValueOrDefault("source") ?? "@input";
        var source = sourceName == "@input"
            ? ctx.Input
            : (ctx.Images.TryGetValue(sourceName, out var upstream) ? upstream : null);
        if (source is null || source.Empty())
        {
            var error = $"图像来源未找到或为空: {sourceName}（请改选本节点上游的有图节点）";
            Log?.Invoke($"[OverlayDisplay] {Name}: {error}");
            return new NodeResult { Decision = "ERROR", Error = error };
        }

        var result = new NodeResult
        {
            Decision = "OK",
            OutputImage = source.Clone(),
        };
        var selected = ParseNodeNames(_params.GetValueOrDefault("overlay_nodes"));
        var showDecision = IsTrue(_params.GetValueOrDefault("show_decision"), true);
        var showValues = IsTrue(_params.GetValueOrDefault("show_values"), true);
        var labelIndex = 0;

        foreach (var nodeName in selected)
        {
            if (!ctx.Results.TryGetValue(nodeName, out var nodeResult))
            {
                Log?.Invoke($"[OverlayDisplay] {Name}: 叠加节点不存在或尚未执行: {nodeName}");
                continue;
            }

            result.Annotations.AddRange(nodeResult.Annotations);
            var label = BuildSummary(nodeName, nodeResult, showDecision, showValues);
            if (label.Length > 0)
            {
                result.Annotations.Add(new NodeShape
                {
                    Box = new Rect(6, 6 + labelIndex++ * 22, 1, 1),
                    Label = label,
                    Kind = KindOf(nodeResult.Decision),
                });
            }
        }

        result.Values["overlay_count"] = selected.Count.ToString(CultureInfo.InvariantCulture);
        result.Values["shape_count"] = result.Annotations.Count.ToString(CultureInfo.InvariantCulture);
        result.Values["overlay_nodes"] = string.Join(";", selected);
        result.Values["decision"] = "OK";
        Log?.Invoke($"[OverlayDisplay] {Name}: 显式叠加 {selected.Count} 个节点，标注 {result.Annotations.Count} 个");
        return result;
    }

    internal static IReadOnlyList<string> ParseNodeNames(string? raw) =>
        (raw ?? "").Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string BuildSummary(string name, NodeResult result, bool showDecision, bool showValues)
    {
        var parts = new List<string> { name };
        if (showDecision && !string.IsNullOrWhiteSpace(result.Decision)) parts.Add(result.Decision);
        if (showValues)
        {
            foreach (var key in new[] { "score", "count", "text", "center_x", "center_y", "radius" })
            {
                if (result.Values.TryGetValue(key, out var value)) parts.Add($"{key}={value}");
            }
        }
        return parts.Count > 1 ? string.Join("  ", parts) : "";
    }

    private static NodeShapeKind KindOf(string? decision) => decision switch
    {
        "OK" => NodeShapeKind.Ok,
        "NG" or "ERROR" => NodeShapeKind.Defect,
        _ => NodeShapeKind.Info,
    };

    private static bool IsTrue(string? raw, bool fallback) =>
        raw is null ? fallback : bool.TryParse(raw, out var value) ? value : fallback;

    public void Dispose() { }
}
