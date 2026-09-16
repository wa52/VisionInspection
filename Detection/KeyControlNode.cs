using System.Windows.Input;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 按键控制节点（工具节点，恒 OK）：为检测流程绑定一个常用键盘键位（空格/F5/F8/F12/Enter）。
/// 程序空闲时按下键位 = 执行一次完整检测流程（图像源为相机+软触发模式时即软触发抓帧）；
/// 连续执行中按键忽略（不打断）。键位绑定由 MainWindow 在配方加载/参数变更后注册；
/// 输入框聚焦时不触发（防打字误触发）。本节点在流程内仅透传配置（不处理图像、不参与判定）。
/// </summary>
public sealed class KeyControlNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "trigger_key", Label = "触发键位", Kind = "choice", Default = "空格", Choices = ["空格", "Enter", "F5", "F8", "F12"] },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["trigger_key"] = "空格",
    };

    /// <summary>运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public KeyControlNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "KeyControl";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    /// <summary>键位名 → WPF Key（未知/空 → null）。</summary>
    internal static Key? ParseKey(string? name) => (name ?? "").Trim() switch
    {
        "空格" => Key.Space,
        "Enter" => Key.Enter,
        "F5" => Key.F5,
        "F8" => Key.F8,
        "F12" => Key.F12,
        _ => null,
    };

    /// <summary>WPF Key → 键位显示名（日志用）。</summary>
    internal static string KeyName(Key key) => key switch
    {
        Key.Space => "空格",
        Key.Enter => "Enter",
        Key.F5 => "F5",
        Key.F8 => "F8",
        Key.F12 => "F12",
        _ => key.ToString(),
    };

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var keyName = _params.GetValueOrDefault("trigger_key") ?? "空格";
        return new NodeResult
        {
            Decision = "OK",
            Values = { ["trigger_key"] = keyName },
        };
    }

    public void Dispose() { }
}
