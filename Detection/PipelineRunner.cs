using OpenCvSharp;
using VisionInspection.Camera;
using VisionInspection.Comm;

namespace VisionInspection.Detection;

/// <summary>
/// 流水线执行器：单次执行 / 连续执行（软件驱动的运行方式，脱离 PLC 硬触发链路）。
/// 图像由「图像源」节点自行取得（文件目录游标或相机抓帧）；执行器只负责驱动与取消。
/// - RunOnceAsync：跑一遍流水线，返回结果。
/// - StartContinuous：后台循环逐轮执行（同一 Pipeline 实例，图像源游标连续推进），StopAsync 终止；
///   取消在两轮之间生效（单轮 Pipeline.Run 内部不可中断）。
/// Completed/StateChanged/Failed 均在后台线程回调，UI 侧自行调度。
/// </summary>
public sealed class PipelineRunner : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _continuousTask;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public bool IsContinuous { get; private set; }

    /// <summary>执行状态变化（true=开始，false=结束）。</summary>
    public event Action<bool>? StateChanged;
    /// <summary>每轮执行完成（单次一次、连续每轮一次），携带该轮结果。</summary>
    public event Action<PipelineResult>? Completed;
    /// <summary>执行异常（流水线整体抛出等，节点级错误已在 result.Error 里）。</summary>
    public event Action<string>? Failed;

    /// <summary>单次执行一遍流水线（空输入，图像由图像源节点取得）。</summary>
    public async Task<PipelineResult> RunOnceAsync(
        Pipeline pipeline,
        string imageName,
        Action<IoCommunicationSettings>? cameraIoOutput = null,
        ICommRuntime? commRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        BeginRun(continuous: false);
        try
        {
            using var input = new Mat();
            var result = await Task.Run(
                () => pipeline.Run(input, imageName, null, cameraIoOutput, null, commRuntime));
            Completed?.Invoke(result);
            return result;
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>启动连续执行；已有执行进行中时抛异常。用 StopAsync 终止。</summary>
    public void StartContinuous(
        Pipeline pipeline,
        Func<string> imageNameFactory,
        Action<IoCommunicationSettings>? cameraIoOutput = null,
        ICommRuntime? commRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(imageNameFactory);

        Task loop;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                throw new InvalidOperationException("已有执行在进行中");
            }

            IsRunning = true;
            IsContinuous = true;
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            _continuousTask = loop = Task.Run(async () =>
            {
                try
                {
                    var iteration = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        iteration++;
                        using var input = new Mat();
                        PipelineResult result;
                        try
                        {
                            result = pipeline.Run(input, imageNameFactory(), null, cameraIoOutput, null, commRuntime);
                        }
                        catch (Exception ex)
                        {
                            Failed?.Invoke($"连续执行第 {iteration} 轮异常: {ex.Message}");
                            break;
                        }

                        Completed?.Invoke(result);
                        try
                        {
                            await Task.Delay(1, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    EndRun();
                }
            });
        }
        StateChanged?.Invoke(true);
    }

    /// <summary>请求停止连续执行并等待循环退出（取消在当前轮结束后生效）。重复调用无副作用。</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _continuousTask;
            _cts?.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // 循环内部已兜底，不会抛；保险起见不向 UI 传播
            }
        }
    }

    private void BeginRun(bool continuous)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                throw new InvalidOperationException("已有执行在进行中");
            }

            IsRunning = true;
            IsContinuous = continuous;
        }
        StateChanged?.Invoke(true);
    }

    private void EndRun()
    {
        lock (_gate)
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _continuousTask = null;
        }
        StateChanged?.Invoke(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cts?.Cancel();
        }

        try
        {
            _continuousTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _continuousTask = null;
        }
    }
}
