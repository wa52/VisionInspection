using System.IO;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 输出图像节点：把原图或某个上游节点的输出图，按当前判定保存到指定目录。
/// source = @input（原图）或上游节点名；save_mode = all/ok/ng；dir = 保存目录（下分 OK/NG 子目录）。
/// </summary>
public sealed class SaveImageNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "save_mode", Label = "检测结果保存类型", Kind = "choice", Default = "全部", Choices = ["全部", "仅OK", "仅NG", "不保存"] },
        new ParamDef { Key = "dir", Label = "保存目录", Kind = "folder", Default = "output" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["save_mode"] = "全部",
        ["dir"] = "output",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public SaveImageNode(string name, Dictionary<string, string>? init = null, Action<string>? log = null)
    {
        Name = name;
        Log = log;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "SaveImage";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    /// <summary>保存门槛归一化：新配方存中文（全部/仅OK/仅NG/不保存），旧配方存 all/ok/ng，两者都接受。</summary>
    internal static string NormalizeMode(string? mode) => (mode ?? "").Trim() switch
    {
        "仅OK" or "ok" => "ok",
        "仅NG" or "ng" => "ng",
        "不保存" or "none" or "off" => "none",
        _ => "all",
    };

    /// <summary>保存类型门槛：仅OK=判定 OK 时保存，仅NG=判定 NG 时保存，其余全部保存。YOLO/Seg 整图留存共用。</summary>
    internal static bool ShouldSave(string? mode, string decision) => NormalizeMode(mode) switch
    {
        "ok" => decision == "OK",
        "ng" => decision == "NG",
        "none" => false,
        _ => true,
    };

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var source = _params.TryGetValue("source", out var s) ? s : "@input";
        var mode = _params.TryGetValue("save_mode", out var m) ? m : "all";
        var dir = _params.TryGetValue("dir", out var d) ? d : "";

        // 取图像源：@input = 原图；否则取上游节点输出图
        Mat? img = source == "@input" ? ctx.Input : (ctx.Images.TryGetValue(source, out var im) ? im : null);
        if (img == null)
        {
            Log?.Invoke($"[SaveImage] {Name}: 图像来源未找到: {source}（来源节点不存在或不在本节点上游），本次不保存");
            return new NodeResult { Decision = "OK", Values = { ["saved"] = "0" } };
        }

        var decision = ctx.CurrentDecision;
        var shouldSave = ShouldSave(mode, decision);

        if (!shouldSave)
        {
            return new NodeResult { Decision = "OK", Values = { ["saved"] = "0" } };
        }
        if (string.IsNullOrWhiteSpace(dir))
        {
            Log?.Invoke($"[SaveImage] {Name}: 未配置「保存目录」(dir)，本次不保存");
            return new NodeResult { Decision = "OK", Values = { ["saved"] = "0" } };
        }

        try
        {
            // 保存到 dir\<判定>\<时间戳>_<源名>
            var sub = decision == "NG" ? "NG" : "OK";
            var full = Path.IsPathRooted(dir)
                ? Path.GetFullPath(Path.Combine(dir, sub))
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir, sub));
            Directory.CreateDirectory(full);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var tag = source == "@input" ? "input" : source;
            var outPath = Path.Combine(full, $"{ts}_{tag}.jpg");
            Cv2.ImWrite(outPath, img);
            Log?.Invoke($"[SaveImage] {Name}: 已保存 {decision} 图 -> {outPath}");
            return new NodeResult { Decision = "OK", Values = { ["saved"] = "1" } };
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[SaveImage] {Name} 保存失败: {ex.Message}");
            return new NodeResult { Decision = "ERROR", Error = ex.Message, Values = { ["saved"] = "0" } };
        }
    }

    public void Dispose() { }
}
