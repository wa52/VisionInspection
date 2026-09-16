using OpenCvSharp;
using VisionInspection.Comm;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 接收数据节点（工具节点，VM 式）：从「通信管理」里配置的通信设备取一条收到的文本。
/// 每次执行按 FIFO 消费一条积压数据，无积压时阻塞等待至超时（timeout_ms，0=只取积压不等待）。
/// 超时判定 timeout_action：ERROR停线[默认，VM 式]/按OK继续（received=0）。
/// 判定节点可直接引用本节点 text 值（如 接收数据1.text 等于 OK）。
/// </summary>
public sealed class ReceiveDataNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "device", Label = "通信设备", Kind = "device", Default = "" },
        new ParamDef { Key = "timeout_ms", Label = "接收超时(ms, 0=只取积压)", Kind = "int", Default = "5000" },
        new ParamDef { Key = "timeout_action", Label = "超时判定", Kind = "choice", Default = "ERROR停线", Choices = ["ERROR停线", "按OK继续"] },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["device"] = "",
        ["timeout_ms"] = "5000",
        ["timeout_action"] = "ERROR停线",
    };

    /// <summary>运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public ReceiveDataNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "ReceiveData";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var device = (_params.GetValueOrDefault("device") ?? "").Trim();
        var values = new Dictionary<string, string>
        {
            ["device"] = device,
            ["received"] = "0",
        };

        if (string.IsNullOrWhiteSpace(device))
        {
            return Error(values, "未选择通信设备（请在参数面板选择与「通信管理」一致的设备名）");
        }

        if (ctx.CommRuntime is null)
        {
            return Error(values, "未绑定通信运行时（请重启软件；持续出现请检查主窗口装配）");
        }

        var timeoutMs = ParseTimeoutMs(_params.GetValueOrDefault("timeout_ms"));
        var outcome = ctx.CommRuntime.Receive(device, timeoutMs);
        if (!outcome.Ok)
        {
            return Error(values, outcome.Error ?? "接收失败");
        }

        if (outcome.TimedOut)
        {
            values["timeout_ms"] = timeoutMs.ToString();
            if (!string.Equals(_params.GetValueOrDefault("timeout_action"), "按OK继续", StringComparison.Ordinal))
            {
                var message = $"接收超时({timeoutMs}ms)未收到数据（设备「{device}」），请检查对端是否发送";
                return Error(values, message);
            }

            Log?.Invoke($"[接收数据] {Name}: 接收超时({timeoutMs}ms)未收到数据，按 OK 继续");
            return Ok(values);
        }

        values["received"] = "1";
        values["text"] = outcome.Text;
        return Ok(values);
    }

    public void Dispose() { }

    private static NodeResult Ok(Dictionary<string, string> values)
    {
        var nr = new NodeResult { Decision = "OK" };
        foreach (var kv in values) nr.Values[kv.Key] = kv.Value;
        return nr;
    }

    private static NodeResult Error(Dictionary<string, string> values, string message)
    {
        var nr = new NodeResult { Decision = "ERROR", Error = message };
        foreach (var kv in values) nr.Values[kv.Key] = kv.Value;
        nr.Values["error"] = message;
        return nr;
    }

    /// <summary>超时参数解析：非法/负值回退默认 5000；0 允许（只取积压不等待）。</summary>
    internal static int ParseTimeoutMs(string? raw)
    {
        if (int.TryParse(raw, out var value) && value >= 0)
        {
            return value;
        }

        return 5000;
    }
}
