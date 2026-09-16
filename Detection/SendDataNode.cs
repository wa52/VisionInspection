using System.Text.RegularExpressions;
using OpenCvSharp;
using VisionInspection.Comm;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 发送数据节点（工具节点，VM 式）：把模板文本经「通信管理」里配置的通信设备发出。
/// 模板占位符：{节点名.键名} 引用上游节点输出值（如 {快速匹配1.loc_x}）、{decision} 引用当前整线判定；
/// 未识别的占位符解析为空并记日志警告。设备未配置/设备不存在/链路未就绪/发送失败 → ERROR 停线（不静默漏发）。
/// </summary>
public sealed class SendDataNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "device", Label = "通信设备", Kind = "device", Default = "" },
        new ParamDef { Key = "send_text", Label = "发送内容 ({节点名.键名}/{decision})", Kind = "string", Default = "" },
        new ParamDef { Key = "suffix", Label = "附加结束符", Kind = "choice", Default = "无", Choices = ["无", "\\r\\n", "\\n"] },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["device"] = "",
        ["send_text"] = "",
        ["suffix"] = "无",
    };

    /// <summary>运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public SendDataNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "SendData";
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
            ["sent"] = "0",
        };

        if (string.IsNullOrWhiteSpace(device))
        {
            return Error(values, "未选择通信设备（请在参数面板选择与「通信管理」一致的设备名）");
        }

        var resolved = ResolveTemplate(_params.GetValueOrDefault("send_text") ?? "", ctx, out var missing);
        if (missing.Count > 0)
        {
            Log?.Invoke($"[发送数据] {Name}: 占位符未解析（按空串发送）: {string.Join("、", missing)}");
        }

        var payload = resolved + NormalizeSuffix(_params.GetValueOrDefault("suffix"));

        if (ctx.CommRuntime is null)
        {
            return Error(values, "未绑定通信运行时（请重启软件；持续出现请检查主窗口装配）");
        }

        var outcome = ctx.CommRuntime.Send(device, payload);
        if (!outcome.Ok)
        {
            return Error(values, outcome.Error ?? "发送失败");
        }

        values["sent"] = "1";
        values["resolved_text"] = payload;
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

    /// <summary>附加结束符归一化：无/空 → 空串；转义形式（\r\n 等）→ 真实字符。</summary>
    internal static string NormalizeSuffix(string? value) => (value ?? "").Trim() switch
    {
        "" or "无" => "",
        var v => CommText.Unescape(v),
    };

    /// <summary>
    /// 模板解析（纯函数，可单测）：{节点名.键名} → 上游节点输出值；{decision} → 当前整线判定；
    /// 未识别 → 空串并记入 missing。节点名含「.」时按第一个「.」切分（方案节点名避免用「.」命名）。
    /// </summary>
    internal static string ResolveTemplate(string template, PipelineRunContext ctx, out List<string> missing)
    {
        var unresolved = new List<string>();
        missing = unresolved;
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        return Regex.Replace(template, @"\{([^{}]+)\}", match =>
        {
            var token = match.Groups[1].Value.Trim();
            if (string.Equals(token, "decision", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.CurrentDecision;
            }

            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
            {
                unresolved.Add(token);
                return "";
            }

            var nodeName = token[..dot].Trim();
            var key = token[(dot + 1)..].Trim();
            if (ctx.Results.TryGetValue(nodeName, out var nr) && nr.Values.TryGetValue(key, out var value))
            {
                return value;
            }

            unresolved.Add(token);
            return "";
        });
    }
}
