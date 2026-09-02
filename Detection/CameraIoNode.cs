using OpenCvSharp;
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 相机 IO 通信节点：读取上游检测/判断结果，命中条件时触发相机管理页配置的 Strobe 光耦输出。
/// 设备控制（LINE2、Strobe、LineSource、反相）仍在相机管理/IO 输出页配置。
/// </summary>
public sealed class CameraIoNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "结果来源(空=当前判定)", Kind = "string", Default = "" },
        new ParamDef { Key = "output_when", Label = "输出条件", Kind = "choice", Default = "NG", Choices = ["NG", "OK", "ERROR", "all"] },
        new ParamDef { Key = "duration_ms", Label = "持续时间(ms)", Kind = "int", Default = "500" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "",
        ["output_when"] = "NG",
        ["duration_ms"] = "500",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public CameraIoNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "CameraIo";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var source = _params.TryGetValue("source", out var s) ? s : "";
        var decision = ResolveDecision(source, ctx);
        var outputWhen = _params.TryGetValue("output_when", out var ow) ? ow : "NG";
        var shouldOutput = string.Equals(outputWhen, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, outputWhen, StringComparison.OrdinalIgnoreCase);

        if (!shouldOutput)
        {
            return new NodeResult
            {
                Decision = "OK",
                Values = { ["source_decision"] = decision, ["emitted"] = "0" },
            };
        }

        if (ctx.CameraIoSettings is null)
        {
            return new NodeResult
            {
                Decision = "ERROR",
                Error = "未绑定相机 IO 配置",
                Values = { ["source_decision"] = decision, ["emitted"] = "0", ["error"] = "未绑定相机 IO 配置" },
            };
        }

        var settings = Clone(ctx.CameraIoSettings);
        settings.Enabled = true;
        settings.OutputMode = "NgOnly";
        if (double.TryParse(_params.GetValueOrDefault("duration_ms"), out var durationMs) && durationMs > 0)
        {
            settings.PulseMs = durationMs;
        }

        if (!ctx.EmitCameraIo(settings))
        {
            return new NodeResult
            {
                Decision = "ERROR",
                Error = "未绑定相机 IO 输出执行器",
                Values = { ["source_decision"] = decision, ["emitted"] = "0", ["error"] = "未绑定相机 IO 输出执行器" },
            };
        }

        return new NodeResult
        {
            Decision = "OK",
            Values = { ["source_decision"] = decision, ["emitted"] = "1", ["duration_ms"] = settings.PulseMs.ToString("F0") },
        };
    }

    public void Dispose() { }

    private string ResolveDecision(string source, PipelineRunContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (ctx.Results.TryGetValue(source, out var nr))
            {
                return nr.Values.TryGetValue("decision", out var v) && !string.IsNullOrWhiteSpace(v) ? v : nr.Decision;
            }

            Log?.Invoke($"[CameraIo] {Name}: 结果来源 '{source}' 未找到（来源节点不存在或不在本节点上游），回退当前判定");
        }

        return ctx.CurrentDecision;
    }

    private static IoCommunicationSettings Clone(IoCommunicationSettings settings) => new()
    {
        Enabled = settings.Enabled,
        TriggerInputLine = settings.TriggerInputLine,
        TriggerEdge = settings.TriggerEdge,
        OutputMode = settings.OutputMode,
        NgOutputLine = settings.NgOutputLine,
        StrobeSource = settings.StrobeSource,
        ActiveLevel = settings.ActiveLevel,
        PulseMs = settings.PulseMs,
    };
}
