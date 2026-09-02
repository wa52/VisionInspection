using OpenCvSharp;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 兼容旧方案：图像显示节点已移除——中间面板自动显示各节点执行结果
/// （第一个有图像输出的节点作「原图」，最后一个作「显示图」）。
/// 保留此类型使旧方案（recipe 里的 Type=Display）可正常加载，运行时空操作。
/// </summary>
public sealed class DisplayNode : IModelNode
{
    public DisplayNode(string name, Dictionary<string, string>? init = null, Action<string>? log = null) => Name = name;

    public string Name { get; set; }
    public string Type => "Display";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs { get; } = Array.Empty<ParamDef>();
    public IReadOnlyDictionary<string, string> Params { get; } = new Dictionary<string, string>();
    public void SetParam(string key, string value) { }

    public NodeResult Run(Mat bgr, PipelineRunContext ctx) => new() { Decision = "OK" };

    public void Dispose() { }
}
