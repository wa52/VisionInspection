using OpenCvSharp;
using VisionInspection.Camera;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 相机 IO 通信节点（海康式自包含）：输出配置（输出线/Strobe 源/有效电平/持续时间）全部是本节点参数，
/// 随方案保存；设备侧触发输入（Line0/触发沿）属于相机管理（触发参数），与本节点无关。
/// 执行时按命中条件调用相机 Strobe 光耦输出执行器（ctx.CameraIoOutput，由主窗口/生产服务注入）。
/// </summary>
public sealed class CameraIoNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "结果来源(空=当前判定)", Kind = "string", Default = "" },
        new ParamDef { Key = "output_when", Label = "输出类型", Kind = "choice", Default = "NG", Choices = ["NG", "OK", "ERROR", "全部"] },
        new ParamDef { Key = "output_line", Label = "输出线", Kind = "choice", Default = "Line1", Choices = ["Line1", "Line2", "Line3"] },
        new ParamDef { Key = "strobe_source", Label = "脉冲源 (Strobe)", Kind = "choice", Default = "等待触发帧", Choices = ["等待触发帧", "帧结束有效", "曝光结束有效", "定时器有效"] },
        new ParamDef { Key = "active_level", Label = "有效电平", Kind = "choice", Default = "低电平", Choices = ["高电平", "低电平"] },
        new ParamDef { Key = "duration_ms", Label = "持续时间(ms)", Kind = "int", Default = "500" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "",
        ["output_when"] = "NG",
        ["output_line"] = "Line1",
        ["strobe_source"] = "等待触发帧",
        ["active_level"] = "低电平",
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
        var shouldOutput = string.Equals(outputWhen, "全部", StringComparison.Ordinal)
            || string.Equals(outputWhen, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, outputWhen, StringComparison.OrdinalIgnoreCase);

        if (!shouldOutput)
        {
            return new NodeResult
            {
                Decision = "OK",
                Values = { ["source_decision"] = decision, ["emitted"] = "0" },
            };
        }

        if (ctx.CameraIoOutput is null)
        {
            return new NodeResult
            {
                Decision = "ERROR",
                Error = "相机未连接，无法输出 IO 脉冲",
                Values = { ["source_decision"] = decision, ["emitted"] = "0", ["error"] = "相机未连接，无法输出 IO 脉冲" },
            };
        }

        var settings = BuildOutputSettings(_params);
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

    /// <summary>按节点参数构造输出配置（Run 与参数弹窗「测试输出」共用，保证测试与执行一致）。</summary>
    public static IoCommunicationSettings BuildOutputSettings(IReadOnlyDictionary<string, string> parameters)
    {
        double durationMs = 500;
        if (double.TryParse(parameters.GetValueOrDefault("duration_ms"), out var d) && d > 0)
        {
            durationMs = d;
        }

        return new IoCommunicationSettings
        {
            Enabled = true,
            TriggerInputLine = "Line0",
            TriggerEdge = "Rising",
            OutputMode = "NgOnly",
            NgOutputLine = NormalizeOutputLine(parameters.GetValueOrDefault("output_line")),
            StrobeSource = NormalizeStrobeSource(parameters.GetValueOrDefault("strobe_source")),
            ActiveLevel = string.Equals(parameters.GetValueOrDefault("active_level"), "高电平", StringComparison.Ordinal) ? "High" : "Low",
            PulseMs = durationMs,
        };
    }

    private static string NormalizeOutputLine(string? value) => value?.Trim() switch
    {
        "Line2" => "Line2",
        "Line3" => "Line3",
        _ => "Line1",
    };

    /// <summary>归一化 Strobe 源：新配方存中文，旧配方存 SDK 英文名，两者都接受（输出恒为 SDK 名）。</summary>
    private static string NormalizeStrobeSource(string? value) => value?.Trim() switch
    {
        "帧结束有效" or "FrameEndActive" => "FrameEndActive",
        "曝光结束有效" or "ExposureEndActive" => "ExposureEndActive",
        "定时器有效" or "TimerActive" => "TimerActive",
        "等待触发帧" or "FrameTriggerWait" => "FrameTriggerWait",
        _ => "FrameTriggerWait",
    };

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
}
