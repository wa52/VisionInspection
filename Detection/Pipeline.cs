using System.IO;
using OpenCvSharp;
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>单图检测结果（整条流水线）：最终判定 + 每节点明细。</summary>
public sealed class PipelineResult
{
    public string Image { get; set; } = "";
    public string Decision { get; set; } = "OK";
    public double? Threshold { get; set; }
    public string ProcessedAt { get; set; } = "";
    public string? Error { get; set; }
    /// <summary>节点名 → 该节点输出值（供 UI 展示每模型 score）。</summary>
    public Dictionary<string, Dictionary<string, string>> NodeValues { get; } = new();
    /// <summary>PatchCore 热图（若有）。</summary>
    public float[,]? HeatMap { get; set; }
    /// <summary>用于 UI 判断展示的阈值取第一个有阈值的节点。</summary>
    public double? NodeThreshold { get; set; }
    /// <summary>最后一个有图像输出节点的输出图（中间图像区域默认显示；由调用方负责释放；可能为 null）。</summary>
    public Mat? DisplayImage { get; set; }
    /// <summary>DisplayImage 来自哪个节点（面板角标注用；可能为 null）。</summary>
    public string? DisplayNodeName { get; set; }
    /// <summary>本轮所有有（非空）图像输出节点的图像，按节点执行顺序（所有权移交调用方负责释放）。</summary>
    public Dictionary<string, Mat> NodeImages { get; } = new();

    /// <summary>节点名 → 矢量标注形状（框/轮廓/文字，源图像素坐标）：UI 屏幕常量渲染。</summary>
    public Dictionary<string, IReadOnlyList<NodeShape>> NodeAnnotations { get; } = new();
}

/// <summary>检测流水线：按 recipe 的节点顺序运行 → 判断模块求值 → 产出最终 OK/NG。</summary>
public sealed class Pipeline : IDisposable
{
    private readonly Recipe _recipe;
    private readonly List<IModelNode> _nodes = new();
    private readonly Action<string> _log;
    private readonly bool _hasVmRouting;
    private bool _nodeFailed;

    public Pipeline(Recipe recipe, Action<string>? log = null)
    {
        _recipe = recipe;
        _log = log ?? (_ => { });
        foreach (var rn in recipe.Nodes)
        {
            IModelNode node;
            if (rn.Type == "Decision")
            {
                node = new DecisionNode(rn.Name, rn.Rules ?? _recipe.Decision.Rules, _log);
            }
            else
            {
                node = NodeFactory.FromRecipeNode(rn);
                // 只加载启用节点：禁用节点不消耗模型加载资源。
                // 加载失败不阻断建线（记校验日志，运行时该节点报 ERROR 并提示检查模型路径）。
                if (node.Enabled)
                {
                    TryLoadModel(node);
                }
            }
            node.Enabled = rn.Enabled;
            // 注入 UI 日志通道：显示/保存/相机IO/图像源/二值化/几何变换节点的失配日志走这里
            switch (node)
            {
                case SaveImageNode s: s.Log = _log; break;
                case CameraIoNode c: c.Log = _log; break;
                case ImageSourceNode isrc: isrc.Log = _log; break;
                case BinarizeNode bz: bz.Log = _log; break;
                case GeometryNode geo: geo.Log = _log; break;
                case ColorTransformNode ct: ct.Log = _log; break;
                case PatchCoreNode pcn: pcn.Log = _log; break;
                case YoloNode yn: yn.Log = _log; break;
                case SegNode sg: sg.Log = _log; break;
                case ContourMatchNode cm: cm.Log = _log; break;
                case PositionCorrectionNode pc: pc.Log = _log; break;
            }
            _nodes.Add(node);
        }
        ValidatePositionCorrectionOrder();
        _hasVmRouting = _nodes.Any(n => n.Enabled && IsModelNode(n) && HasVmRouting(n));
        ValidateNodeGraph();
    }

    /// <summary>位置修正节点的先后顺序校验：定位来源须在上游、修正目标须在下游，否则日志警告（不阻断）。</summary>
    private void ValidatePositionCorrectionOrder()
    {
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i] is not PositionCorrectionNode pc) continue;
            var sourceIdx = _nodes.FindIndex(n => string.Equals(n.Name, pc.SourceName, StringComparison.OrdinalIgnoreCase));
            if (sourceIdx < 0 || sourceIdx >= i)
            {
                _log?.Invoke($"[位置修正] {pc.Name}: 定位来源「{pc.SourceName}」不在本节点上游，定位结果将不可用（请把定位节点排在本节点之前）");
            }
            foreach (var target in pc.TargetNodes)
            {
                var targetIdx = _nodes.FindIndex(n => string.Equals(n.Name, target, StringComparison.OrdinalIgnoreCase));
                if (targetIdx < 0 || targetIdx <= i)
                {
                    _log?.Invoke($"[位置修正] {pc.Name}: 修正目标「{target}」不在本节点下游，该节点的 ROI 不会跟随修正（请把它排在位置修正之后）");
                }
            }
        }
    }

    public IReadOnlyList<IModelNode> Nodes => _nodes;
    public Recipe Recipe => _recipe;

    /// <summary>运行时修改某节点参数（如阈值）。对模型节点会重载运行时使新值立即生效；返回是否成功。</summary>
    public bool ApplyNodeParam(string nodeName, string key, string value)
    {
        var node = _nodes.FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.OrdinalIgnoreCase));
        if (node == null) return false;

        node.SetParam(key, value);
        if (node.Enabled)
        {
            TryLoadModel(node);
        }

        return true;
    }

    /// <summary>加载模型节点运行时（PatchCore/YOLO）；失败只记校验日志不抛出，运行时该节点报 ERROR。</summary>
    private void TryLoadModel(IModelNode node)
    {
        try
        {
            switch (node)
            {
                case PatchCoreNode pc:
                    pc.EnsureLoaded(_recipe.BaseDir);
                    _log?.Invoke($"[校验] {node.Name}: 模型已加载: {pc.ResolvedModelDir}");
                    break;
                case YoloNode yn:
                    yn.EnsureLoaded(_recipe.BaseDir);
                    _log?.Invoke($"[校验] {node.Name}: 模型已加载: {yn.ResolvedModelDir}");
                    break;
                case SegNode sg:
                    sg.EnsureLoaded(_recipe.BaseDir);
                    _log?.Invoke($"[校验] {node.Name}: 模板已加载: {sg.ResolvedModelDir}");
                    break;
                case ContourMatchNode cm:
                    cm.EnsureLoaded(_recipe.BaseDir);
                    _log?.Invoke($"[校验] {node.Name}: 模板已加载: {cm.ResolvedModelDir}");
                    break;
            }
        }
        catch (Exception ex)
        {
            var hint = node is YoloNode or SegNode or ContourMatchNode ? "「模型目录」" : "「模型路径」";
            _log?.Invoke($"[校验] {node.Name}: 模型加载失败: {ex.Message}（请检查{hint}参数）");
        }
    }

    /// <summary>运行整条流水线。返回最终结果；任一启用节点异常 → 整线判定 ERROR。
    /// sourceOverrides：外部注入指定节点的输出图（通常是图像源节点，用于打分目录走与检测完全相同的链路）；
    /// 命中的图像源节点跳过自身取图，注入的 Mat 所有权归 Run（与 bgr 同实例则由调用方释放）。</summary>
    public PipelineResult Run(
        Mat bgr,
        string imageName,
        string? vmSource = null,
        IoCommunicationSettings? cameraIoSettings = null,
        Action<IoCommunicationSettings>? cameraIoOutput = null,
        IReadOnlyDictionary<string, Mat>? sourceOverrides = null)
    {
        var result = new PipelineResult
        {
            Image = imageName,
            ProcessedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
        };
        _nodeFailed = false;
        var ctx = new PipelineRunContext(bgr, cameraIoSettings, cameraIoOutput);
        if (sourceOverrides is not null)
        {
            foreach (var (name, mat) in sourceOverrides)
            {
                ctx.Images[name] = mat;
            }
        }
        var skippedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranModelNode = false;

        foreach (var node in _nodes)
        {
            if (!node.Enabled) continue;
            if (sourceOverrides is not null && node is ImageSourceNode && sourceOverrides.ContainsKey(node.Name))
            {
                skippedNodes.Add(node.Name);
                _log?.Invoke($"[流水线] {node.Name}: 使用外部注入图像，跳过节点取图");
                continue;
            }
            if (!ShouldRunForVmSource(node, vmSource))
            {
                skippedNodes.Add(node.Name);
                _log?.Invoke($"[流水线] {node.Name}: VM图像标识不匹配，已跳过");
                continue;
            }
            if (node is DecisionNode dn && DecisionReferencesSkippedNode(dn, skippedNodes))
            {
                _log?.Invoke($"[流水线] {node.Name}: 上游节点未参与本次VM图像，已跳过");
                continue;
            }
            if (node is SaveImageNode && vmSource != null && _hasVmRouting && !ranModelNode)
            {
                _log?.Invoke($"[流水线] {node.Name}: 本次VM图像尚无匹配检测节点结果，已跳过");
                continue;
            }
            try
            {
                var nr = node.Run(bgr, ctx);
                if (!string.IsNullOrWhiteSpace(nr.Decision) && !nr.Values.ContainsKey("decision"))
                {
                    nr.Values["decision"] = nr.Decision;
                }
                ctx.Results[node.Name] = nr;
                if (nr.OutputImage != null)
                {
                    ctx.Images[node.Name] = nr.OutputImage;
                }
                result.NodeValues[node.Name] = new Dictionary<string, string>(nr.Values);
                if (nr.Annotations.Count > 0)
                {
                    result.NodeAnnotations[node.Name] = nr.Annotations;
                }
                if (nr.Threshold is { } th && result.NodeThreshold == null)
                {
                    result.NodeThreshold = th;
                }
                if (nr.HeatMap != null)
                {
                    result.HeatMap = nr.HeatMap;
                }
                if (node is DecisionNode)
                {
                    ctx.DecisionResult = nr; // 最近一个条件检测节点的结果
                }
                else if (IsModelNode(node))
                {
                    ranModelNode = true;
                }
                _log?.Invoke($"[流水线] {node.Name}: {string.Join(",", nr.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
            catch (Exception ex)
            {
                result.Error = $"{node.Name}: {ex.Message}";
                result.NodeValues[node.Name] = new Dictionary<string, string> { ["error"] = ex.Message, ["decision"] = "ERROR" };
                _log?.Invoke($"[流水线] {node.Name} 异常: {ex.Message}");
                // 任一启用节点出错 → 决策覆写为 ERROR（不能静默回退 OK 放行）
                _nodeFailed = true;
            }
        }

        if (vmSource != null && _hasVmRouting && !ranModelNode && !_nodeFailed)
        {
            _nodeFailed = true;
            result.Error = "VM图像未匹配任何启用检测节点";
            _log?.Invoke($"[流水线] {result.Error}: {vmSource}");
        }

        // 最终判定优先级：有 Decision 节点用最后一个的决策；否则按模型节点自判汇总。
        // 旧 recipe 里残留的方案级 Decision 可能引用已不存在的节点，不能把模型 NG 放行为 OK。
        result.Decision = _nodeFailed ? "ERROR" : ctx.DecisionResult?.Decision ?? ctx.CurrentDecision;
        result.Threshold = result.NodeThreshold;

        // 自动显示：本轮所有非空节点图像移交调用方（UI 缩略图条逐节点查看）；DisplayImage 指向最后一个
        TransferNodeImages(result, ctx.Images);

        // 释放节点输出图（ctx.Images 中的 Mat 由各节点创建，非输入图）
        foreach (var img in ctx.Images.Values)
        {
            if (!ReferenceEquals(img, bgr)) img.Dispose();
        }
        return result;
    }

    public PipelineResult RunNodeInputs(
        IReadOnlyDictionary<string, Mat> nodeInputs,
        string imageName,
        IReadOnlySet<string>? requiredModelNodes = null,
        IoCommunicationSettings? cameraIoSettings = null,
        Action<IoCommunicationSettings>? cameraIoOutput = null)
    {
        var result = new PipelineResult
        {
            Image = imageName,
            ProcessedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
        };
        _nodeFailed = false;
        var ctx = new PipelineRunContext(nodeInputs.Values.FirstOrDefault() ?? new Mat(), cameraIoSettings, cameraIoOutput);
        var skippedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 阶段1：所有启用的模型节点并发执行（节点间无依赖，各自独立 runtime，线程安全）。
        // 并发结果暂存本地字典，全部完成后统一合并进 ctx，避免共享字典并发写。
        var modelResults = new Dictionary<string, NodeResult>(StringComparer.OrdinalIgnoreCase);
        var modelTasks = new List<(IModelNode Node, string InputKey, Mat Input)>();
        foreach (var node in _nodes)
        {
            if (!node.Enabled) continue;
            if (node is DecisionNode) continue; // 判断节点留到阶段2
            if (!IsModelNode(node)) continue;

            if (!nodeInputs.TryGetValue(node.Name, out var input))
            {
                if (requiredModelNodes?.Contains(node.Name) == true)
                {
                    result.Error = $"{node.Name}: VM图像目录未找到对应图片";
                    result.NodeValues[node.Name] = new Dictionary<string, string> { ["error"] = "VM图像目录未找到对应图片", ["decision"] = "ERROR" };
                    _log?.Invoke($"[流水线] {node.Name}: VM图像目录未找到对应图片");
                    _nodeFailed = true;
                }
                else
                {
                    _log?.Invoke($"[流水线] {node.Name}: 本次VM图像无对应输入，已跳过");
                }

                skippedNodes.Add(node.Name);
                continue;
            }

            modelTasks.Add((node, node.Name, input));
        }

        if (modelTasks.Count > 0)
        {
            var results = modelTasks.AsParallel()
                .WithDegreeOfParallelism(Math.Max(1, Math.Min(modelTasks.Count, Environment.ProcessorCount)))
                .Select<(IModelNode Node, string InputKey, Mat Input), (IModelNode Node, NodeResult? Result, Exception? Error)>(t =>
                {
                    try
                    {
                        var nr = t.Node.Run(t.Input, ctx);
                        if (!string.IsNullOrWhiteSpace(nr.Decision) && !nr.Values.ContainsKey("decision"))
                        {
                            nr.Values["decision"] = nr.Decision;
                        }

                        return (t.Node, nr, (Exception?)null);
                    }
                    catch (Exception ex)
                    {
                        return (t.Node, (NodeResult?)null, ex);
                    }
                })
                .ToArray();

            foreach (var (node, nr, error) in results)
            {
                if (error != null)
                {
                    result.Error = $"{node.Name}: {error.Message}";
                    result.NodeValues[node.Name] = new Dictionary<string, string> { ["error"] = error.Message, ["decision"] = "ERROR" };
                    _log?.Invoke($"[流水线] {node.Name} 异常: {error.Message}");
                    _nodeFailed = true;
                    continue;
                }

                modelResults[node.Name] = nr!;
            }
        }

        // 把模型结果合并进 ctx，供阶段2的判断/保存节点引用。
        foreach (var (name, nr) in modelResults)
        {
            ctx.Results[name] = nr;
            if (nr.OutputImage != null) ctx.Images[name] = nr.OutputImage;
            result.NodeValues[name] = new Dictionary<string, string>(nr.Values);
            if (nr.Annotations.Count > 0) result.NodeAnnotations[name] = nr.Annotations;
            if (nr.Threshold is { } th && result.NodeThreshold == null) result.NodeThreshold = th;
            if (nr.HeatMap != null) result.HeatMap = nr.HeatMap;
            _log?.Invoke($"[流水线] {name}: {string.Join(",", nr.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        // 阶段2：按序串行跑判断/保存节点（依赖阶段1结果）。
        foreach (var node in _nodes)
        {
            if (!node.Enabled) continue;
            if (IsModelNode(node)) continue; // 已在阶段1跑过
            if (node is DecisionNode dn && DecisionReferencesSkippedNode(dn, skippedNodes))
            {
                _log?.Invoke($"[流水线] {node.Name}: 上游节点未参与本次VM图像，已跳过");
                continue;
            }

            try
            {
                var nr = node.Run(ctx.Input, ctx);
                if (!string.IsNullOrWhiteSpace(nr.Decision) && !nr.Values.ContainsKey("decision"))
                {
                    nr.Values["decision"] = nr.Decision;
                }

                ctx.Results[node.Name] = nr;
                if (nr.OutputImage != null)
                {
                    ctx.Images[node.Name] = nr.OutputImage;
                }
                result.NodeValues[node.Name] = new Dictionary<string, string>(nr.Values);
                if (nr.Threshold is { } th && result.NodeThreshold == null) result.NodeThreshold = th;
                if (nr.HeatMap != null) result.HeatMap = nr.HeatMap;
                if (node is DecisionNode) ctx.DecisionResult = nr;
                _log?.Invoke($"[流水线] {node.Name}: {string.Join(",", nr.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
            catch (Exception ex)
            {
                result.Error = $"{node.Name}: {ex.Message}";
                result.NodeValues[node.Name] = new Dictionary<string, string> { ["error"] = ex.Message, ["decision"] = "ERROR" };
                _log?.Invoke($"[流水线] {node.Name} 异常: {ex.Message}");
                _nodeFailed = true;
            }
        }

        result.Decision = _nodeFailed ? "ERROR" : ctx.DecisionResult?.Decision ?? ctx.CurrentDecision;
        result.Threshold = result.NodeThreshold;
        TransferNodeImages(result, ctx.Images);
        foreach (var img in ctx.Images.Values)
        {
            if (!nodeInputs.Values.Any(input => ReferenceEquals(img, input))) img.Dispose();
        }
        if (ctx.Input.Empty()) ctx.Input.Dispose();
        return result;
    }

    /// <summary>
    /// 自动显示：把本轮所有有（非空）图像输出的节点图像按执行顺序移交调用方
    /// （从 ctx.Images 移除，跳过末尾统一释放）；DisplayImage/DisplayNodeName 优先指向最后一个检测节点——
    /// 多图像源流程里排在检测节点之后的图像源不能盖掉检测结果图，无检测节点图像时回退最后一个有图节点。
    /// </summary>
    private void TransferNodeImages(PipelineResult result, Dictionary<string, Mat> images)
    {
        string? lastName = null;
        string? lastModelName = null;
        foreach (var node in _nodes)
        {
            if (!images.TryGetValue(node.Name, out var img) || img.Empty()) continue;
            result.NodeImages[node.Name] = img;
            images.Remove(node.Name);
            lastName = node.Name;
            if (IsModelNode(node)) lastModelName = node.Name;
        }

        var displayName = lastModelName ?? lastName;
        if (displayName != null)
        {
            result.DisplayImage = result.NodeImages[displayName];
            result.DisplayNodeName = displayName;
        }
    }

    private bool ShouldRunForVmSource(IModelNode node, string? vmSource)
    {
        if (vmSource == null || !_hasVmRouting || !IsModelNode(node)) return true;
        if (!node.Params.TryGetValue("vm_label", out var label) || string.IsNullOrWhiteSpace(label)) return false;

        return VmLabelMatches(node, vmSource);
    }

    private static bool HasVmLabel(IModelNode node) =>
        node.Params.TryGetValue("vm_label", out var label) && !string.IsNullOrWhiteSpace(label);

    private static bool HasVmRouting(IModelNode node) => HasVmLabel(node);

    private static bool VmLabelMatches(IModelNode node, string vmSource)
    {
        if (!node.Params.TryGetValue("vm_label", out var label) || string.IsNullOrWhiteSpace(label)) return false;
        var tokens = label.Split([';', ',', '|', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(t => vmSource.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsModelNode(IModelNode node) =>
        node is not DecisionNode and not SaveImageNode and not DisplayNode and not CameraIoNode
        and not ImageSourceNode and not BinarizeNode and not GeometryNode;

    /// <summary>
    /// 构建期校验节点引用（只记日志，不阻断）：source/条件引用的节点不存在、
    /// 或引用了非上游节点（自己或排在自己后面）时，运行时必然取不到输入。
    /// </summary>
    private void ValidateNodeGraph()
    {
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];

            // 图像/结果来源引用（Display/SaveImage/CameraIo 的 source 参数）
            if (node.Params.TryGetValue("source", out var source)
                && !string.IsNullOrWhiteSpace(source)
                && source != "@input")
            {
                ValidateReference(node.Name, source, i);
            }

            // 条件检测节点引用的上游节点
            if (node is DecisionNode dn)
            {
                foreach (var cond in dn.Rules.SelectMany(r => r.Conditions))
                {
                    if (!string.IsNullOrWhiteSpace(cond.Node))
                    {
                        ValidateReference(node.Name, cond.Node, i);
                    }
                }
            }

            // 图像源节点：文件模式检查目录/文件存在性（只记日志；相机模式无磁盘依赖）
            if (node is ImageSourceNode imageSource && IsFileMode(imageSource))
            {
                var dir = node.Params.GetValueOrDefault("dir");
                var path = node.Params.GetValueOrDefault("path");
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    if (!Directory.Exists(dir))
                    {
                        _log?.Invoke($"[校验] {node.Name}: 图片目录不存在: {dir}");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                {
                    _log?.Invoke($"[校验] {node.Name}: 图像文件不存在: {path}");
                }
            }
        }
    }

    private void ValidateReference(string nodeName, string refName, int nodeIndex)
    {
        var refIndex = _nodes.FindIndex(n => string.Equals(n.Name, refName, StringComparison.OrdinalIgnoreCase));
        if (refIndex < 0)
        {
            _log?.Invoke($"[校验] {nodeName}: 来源 '{refName}' 不存在，运行时将取不到该输入");
        }
        else if (refIndex >= nodeIndex)
        {
            _log?.Invoke($"[校验] {nodeName}: 来源 '{refName}' 不是上游节点（顺序错误），运行时将取不到该输入");
        }
    }

    private static bool DecisionReferencesSkippedNode(DecisionNode node, HashSet<string> skippedNodes) =>
        node.Rules.SelectMany(r => r.Conditions).Any(c => skippedNodes.Contains(c.Node));

    /// <summary>图像源是否为文件模式（未配置 source_kind 的旧方案默认文件模式）。</summary>
    private static bool IsFileMode(ImageSourceNode node) =>
        !string.Equals(node.Params.GetValueOrDefault("source_kind"), "相机", StringComparison.Ordinal);

    public void Dispose()
    {
        foreach (var n in _nodes) n.Dispose();
        _nodes.Clear();
    }
}
