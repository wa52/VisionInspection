using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>占位节点：用于尚未实现的模型类型（扩展点）。可保存/添加，运行时报 ERROR 不崩溃。</summary>
public sealed class UnsupportedNode : IModelNode
{
    private readonly string _message;

    public UnsupportedNode(string name, string type)
    {
        Name = name;
        Type = type;
        _message = $"{type} 节点尚未实现（仅占位扩展点）。请实现 IModelNode 并更新 NodeFactory 注册。";
        _params["_type"] = type;
    }

    private readonly Dictionary<string, string> _params = new();
    public string Name { get; set; }
    public string Type { get; }
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => Array.Empty<ParamDef>();
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx) => throw new NotSupportedException(_message);

    public void Dispose() { }
}
