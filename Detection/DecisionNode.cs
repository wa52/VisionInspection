using OpenCvSharp;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 条件检测节点：用其 Rules 对 ctx 中已跑节点的输出求值（Rule 间 OR / 条件内 AND）。
/// 允许多个：后面的 Decision 节点可通过引用前面 Decision 节点的 decision 字段做链式判断。
/// </summary>
public sealed class DecisionNode : IModelNode
{
    private readonly List<DecisionRule> _rules;
    private readonly Dictionary<string, string> _params = new() { ["_rules"] = "" };
    private readonly Action<string> _log;

    public DecisionNode(string name, List<DecisionRule>? rules = null, Action<string>? log = null)
    {
        Name = name;
        _rules = rules ?? new List<DecisionRule>();
        _log = log ?? (_ => { });
    }

    public string Name { get; set; }
    public string Type => "Decision";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => Array.Empty<ParamDef>();
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public IReadOnlyList<DecisionRule> Rules => _rules;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        // 把已跑节点结果转换为 节点名 → 值字典（供 DecisionEvaluator 引用）
        var values = ctx.Results.ToDictionary(kv => kv.Key, kv => kv.Value.Values);
        var decision = DecisionEvaluator.Evaluate(new RecipeDecision { Rules = _rules }, values, _log);
        return new NodeResult
        {
            Values = { ["decision"] = decision },
            Decision = decision,
        };
    }

    public void Dispose() { }
}
