using VisionInspection.Detection;

namespace VisionInspection;

/// <summary>
/// 执行结果的待渲染队列：后台线程 Publish、UI 定时器 Take，只保留最新一轮（跳帧）。
/// 被跳过结果的节点图由 Publish 返回给调用方释放（逐个容错，防与渲染侧竞态崩掉回调）。
/// </summary>
public sealed class PendingResultQueue
{
    private readonly object _gate = new();
    private PipelineResult? _pending;

    /// <summary>投递新一轮结果；返回被跳过的上一轮（无则 null，调用方负责释放其节点图）。</summary>
    public PipelineResult? Publish(PipelineResult result)
    {
        lock (_gate)
        {
            var skipped = _pending;
            _pending = result;
            return skipped;
        }
    }

    /// <summary>取走待渲染结果（无则 null）。</summary>
    public PipelineResult? Take()
    {
        lock (_gate)
        {
            var r = _pending;
            _pending = null;
            return r;
        }
    }

    /// <summary>
    /// 释放被跳过结果的节点图：Mat 可能与渲染侧共享，逐个容错，
    /// 防止后台线程释放时与 UI 渲染竞态导致 ObjectDisposedException 崩掉回调。
    /// </summary>
    public static void DisposeSkipped(PipelineResult skipped)
    {
        foreach (var m in skipped.NodeImages.Values)
        {
            try
            {
                if (!m.IsDisposed)
                {
                    m.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }
        skipped.NodeImages.Clear();
    }
}
