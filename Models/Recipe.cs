using System.Text.Json;
using System.Text.Json.Serialization;
using VisionInspection.Detection;

namespace VisionInspection.Models;

/// <summary>模型节点参数定义（驱动通用 Inspector 与序列化）。</summary>
public sealed class ParamDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>string | double | int | bool | path | folder | choice</summary>
    public string Kind { get; set; } = "string";
    public string? Default { get; set; }
    public string[]? Choices { get; set; }
    /// <summary>Kind=noderesult（上游节点输出引用下拉）时的节点类型过滤，如 ["LineFind"]；空 = 不过滤。</summary>
    public string[]? SourceTypes { get; set; }
}

/// <summary>模型节点：统一接口（M2 起由各实现类提供 Run 行为）。</summary>
public sealed class RecipeNode
{
    public string Name { get; set; } = "模型";
    /// <summary>PatchCore | Decision | SaveImage | YOLO | CharRec | ...（NodeFactory 注册的类型名）</summary>
    public string Type { get; set; } = "PatchCore";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Params { get; set; } = new();
    /// <summary>Decision 节点的判断规则（为空则用方案级 Decision）。</summary>
    public List<DecisionRule>? Rules { get; set; }
}

/// <summary>判断条件：引用某个节点输出字段，与一个值比较。</summary>
public sealed class Condition
{
    public string Node { get; set; } = "";
    /// <summary>score | count | text | ...（取 NodeResult.Value 里的键）</summary>
    public string Field { get; set; } = "score";
    /// <summary>&gt; &gt;= &lt; &lt;= = !=</summary>
    public string Op { get; set; } = ">";
    public string Value { get; set; } = "0";
}

/// <summary>判断规则：规则内所有条件 AND，命中则结果为 Result。</summary>
public sealed class DecisionRule
{
    public List<Condition> Conditions { get; set; } = new();
    /// <summary>条件组合方式：all=全部条件符合；any=任意条件符合。</summary>
    public string MatchMode { get; set; } = "all";
    /// <summary>命中本规则时输出的判定，如 NG</summary>
    public string Result { get; set; } = "NG";
    /// <summary>未命中本规则时输出的判定，如 OK。</summary>
    public string ElseResult { get; set; } = "OK";
}

/// <summary>判断模块：任一规则命中 → 该规则的 Result；全部未命中 → 默认 OK。</summary>
public sealed class RecipeDecision
{
    public List<DecisionRule> Rules { get; set; } = new();
}

/// <summary>IO 与外部通信配置（沿用原 config.json 语义）。</summary>
public sealed class RecipeIo
{
    public string InputDir { get; set; } = "vm_input";
    public string? ResultDir { get; set; } = "vm_results";
    public int PollIntervalMs { get; set; } = 500;
    public bool MoveProcessed { get; set; } = false;
    public string ProcessedDir { get; set; } = "";
    public string VmHost { get; set; } = "127.0.0.1";
    public int VmPort { get; set; } = 7930;
    public bool VmEnabled { get; set; } = true;
    public double VmReconnectIntervalS { get; set; } = 3.0;
    public string ResultFormat { get; set; } = "plain"; // plain | csv | json
    public string Separator { get; set; } = ",";
    /// <summary>VM 回包结束符，支持配置为 \r\n / \n；空字符串保持旧行为。</summary>
    public string VmResponseTerminator { get; set; } = "";
}

/// <summary>方案级命名 ROI：归一化几何（中心+宽高+角度），供 PatchCore/YOLO 等节点按名称引用。</summary>
public sealed class RecipeRoi
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Angle { get; set; }

    public RoiRect ToRoiRect() => new(X, Y, W, H, Angle);
    public static RecipeRoi FromRoiRect(string name, RoiRect r) => new()
    {
        Name = name,
        X = r.CenterX,
        Y = r.CenterY,
        W = r.W,
        H = r.H,
        Angle = r.AngleDeg,
    };
}

/// <summary>
/// 方案（Recipe）：多模型检测流程 + 判断规则 + IO 配置 + 方案级 ROI 库。
/// 替代原 config.json 的配置语义；运行状态（Running/Stopped）不持久化。
/// </summary>
public sealed class Recipe
{
    public string Name { get; set; } = "未命名方案";
    public List<RecipeNode> Nodes { get; set; } = new();
    public RecipeDecision Decision { get; set; } = new();
    public RecipeIo Io { get; set; } = new();
    /// <summary>方案级 ROI 库：节点（PatchCore/YOLO）的 rois 参数按名称引用。</summary>
    public List<RecipeRoi> Rois { get; set; } = new();

    /// <summary>上次关闭时的运行状态标记（仅展示，不恢复运行）：Ready | Running</summary>
    public string LastRunState { get; set; } = "Ready";

    /// <summary>配置文件所在目录；相对路径基于它解析。</summary>
    [JsonIgnore]
    public string BaseDir { get; set; } = "";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Recipe CreateDefault()
    {
        return new Recipe { Name = "新建方案" };
    }
}
