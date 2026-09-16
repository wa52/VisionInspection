using OpenCvSharp;
using VisionInspection.Camera;
using VisionInspection.Comm;
using VisionInspection.Detection;
using VisionInspection.Models;

namespace VisionInspection;

/// <summary>
/// 方案执行编排（应用层）：拥有 Pipeline 生命周期与运行方式（单次/连续），UI 只调用启动/停止与订阅事件。
/// 设备能力经委托注入（帧抓取工厂 + 相机 IO 输出），服务不认识具体设备实现。
/// 方案结构/模型路径变化后由 UI 调 Invalidate() 标脏，下次 PrepareAsync 重建流水线。
/// Completed/StateChanged/Failed 均在后台线程回调，UI 侧自行调度。
/// </summary>
public sealed class RecipeExecutionService : IDisposable
{
    private readonly Func<Recipe?> _getRecipe;
    private readonly Action<string> _log;
    private readonly Func<Func<int, Mat?>> _frameProviderFactory;
    private readonly Func<Action<IoCommunicationSettings>?> _cameraIoFactory;
    private readonly Func<ICommRuntime?> _commRuntimeFactory;
    private readonly PipelineRunner _runner = new();

    private Pipeline? _pipeline;
    private bool _pipelineDirty = true;
    private bool _disposed;

    public bool IsRunning => _runner.IsRunning;
    public bool IsContinuous => _runner.IsContinuous;

    /// <summary>当前流水线（打分目录等直接消费方；PrepareAsync 成功后可用）。</summary>
    public Pipeline? CurrentPipeline => _pipeline;

    /// <summary>每轮执行完成（单次一次、连续每轮一次），携带该轮结果。</summary>
    public event Action<PipelineResult>? Completed;
    /// <summary>执行状态变化（true=开始，false=结束）。</summary>
    public event Action<bool>? StateChanged;
    /// <summary>执行异常（流水线整体抛出等，节点级错误已在 result.Error 里）。</summary>
    public event Action<string>? Failed;

    public RecipeExecutionService(
        Func<Recipe?> getRecipe,
        Action<string> log,
        Func<Func<int, Mat?>> frameProviderFactory,
        Func<Action<IoCommunicationSettings>?> cameraIoFactory,
        Func<ICommRuntime?>? commRuntimeFactory = null)
    {
        _getRecipe = getRecipe;
        _log = log;
        _frameProviderFactory = frameProviderFactory;
        _cameraIoFactory = cameraIoFactory;
        _commRuntimeFactory = commRuntimeFactory ?? (() => null);
        _runner.Completed += r => Completed?.Invoke(r);
        _runner.StateChanged += s => StateChanged?.Invoke(s);
        _runner.Failed += m => Failed?.Invoke(m);
    }

    /// <summary>方案结构/参数变化后标脏：下次 PrepareAsync 重建流水线。</summary>
    public void Invalidate() => _pipelineDirty = true;

    /// <summary>
    /// 确保流水线就绪：脏则后台重建、注入帧提供器、校验 @input 引用。失败记日志返回 false。
    /// </summary>
    public async Task<bool> PrepareAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runner.IsRunning)
        {
            _log("[执行] 已有执行在进行中，请先停止");
            return false;
        }
        var recipe = _getRecipe();
        if (recipe == null) return false;
        var hasImageSource = recipe.Nodes.Any(n => n.Enabled && (n.Type == "ImageSource" || n.Type == "ImageLoad"));
        if (!hasImageSource)
        {
            _log("[执行] 方案中没有启用的图像源节点，请先添加「图像源」作为流程起点");
            return false;
        }
        if (_pipeline == null || _pipelineDirty)
        {
            _pipeline?.Dispose();
            _pipeline = null;
            Pipeline built;
            try
            {
                built = await Task.Run(() => new Pipeline(recipe, _log));
            }
            catch (Exception ex)
            {
                _pipelineDirty = true;
                _log("[执行] 流水线构建失败: " + ex.Message + "（请检查各节点参数，如 PatchCore 的「模型路径」）");
                return false;
            }
            _pipeline = built;
            _pipelineDirty = false;
            _log($"[执行] 流水线已就绪: {_pipeline.Nodes.Count(n => n.Enabled)} 个启用节点");
        }
        foreach (var n in _pipeline.Nodes)
        {
            if (n is ImageSourceNode src)
            {
                src.FrameProvider = _frameProviderFactory();
            }
        }
        foreach (var n in _pipeline.Nodes.Where(n => n.Enabled))
        {
            if (n.ParamDefs.Any(d => d.Key == "source") && n.Params.TryGetValue("source", out var srcVal) && srcVal == "@input")
            {
                _log("[校验] " + n.Name + ": 图像来源为 @input（执行时为空图），请在参数里选择上游节点");
            }
        }
        return true;
    }

    /// <summary>单次执行一遍流水线（图像由图像源节点取得）。准备失败/异常记日志不抛出。</summary>
    public async Task RunOnceAsync()
    {
        if (!await PrepareAsync()) return;
        try
        {
            await _runner.RunOnceAsync(_pipeline!, $"EXEC_{DateTime.Now:yyyyMMdd_HHmmss_fff}", _cameraIoFactory(), _commRuntimeFactory());
        }
        catch (Exception ex)
        {
            _log("单次执行失败: " + ex.Message);
        }
    }

    /// <summary>连续执行开关：连续运行中则停止；否则准备并启动连续循环（用 StopAsync 终止）。</summary>
    public async Task ToggleContinuousAsync()
    {
        if (_runner.IsRunning && _runner.IsContinuous)
        {
            await _runner.StopAsync();
            return;
        }
        if (!await PrepareAsync()) return;
        try
        {
            _runner.StartContinuous(_pipeline!, () => $"EXEC_{DateTime.Now:yyyyMMdd_HHmmss_fff}", _cameraIoFactory(), _commRuntimeFactory());
            _log("连续执行已开始，点「停止执行」结束");
        }
        catch (Exception ex)
        {
            _log("连续执行启动失败: " + ex.Message);
        }
    }

    /// <summary>请求停止连续执行并等待循环退出（取消在当前轮结束后生效）。重复调用无副作用。</summary>
    public Task StopAsync() => _runner.StopAsync();

    /// <summary>运行时修改某节点参数（对模型节点重载运行时使新值立即生效）。返回是否成功。</summary>
    public bool ApplyNodeParam(string nodeName, string key, string value) =>
        _pipeline?.ApplyNodeParam(nodeName, key, value) ?? false;

    /// <summary>参数热应用：模型节点的模型目录变化标脏（重建才生效），其余直接热更。</summary>
    public void HotApplyParam(string nodeName, string key, string value)
    {
        if (_pipeline == null) return;
        var node = _pipeline.Nodes.FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.OrdinalIgnoreCase));
        if (node == null) return;
        if (node is ContourMatchNode && key == "model_dir")
        {
            _pipeline.ApplyNodeParam(nodeName, key, value);
        }
        else if ((node is PatchCoreNode || node is YoloNode || node is SegNode || node is SemanticSegNode || node is CharRecNode) && key == "model_dir")
        {
            _pipelineDirty = true;
        }
        else
        {
            _pipeline.ApplyNodeParam(nodeName, key, value);
        }
    }

    /// <summary>节点启停热切换（不重建流水线）。</summary>
    public void SetNodeEnabled(string nodeName, bool enabled)
    {
        var node = _pipeline?.Nodes.FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.OrdinalIgnoreCase));
        if (node != null) node.Enabled = enabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner.Dispose();
        _pipeline?.Dispose();
        _pipeline = null;
    }
}
