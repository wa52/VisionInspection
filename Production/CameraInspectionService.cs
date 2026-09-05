using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenCvSharp;
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using SpeakerVisionInspection.Plc;
using SpeakerVisionInspection.Services;
using SpeakerVisionInspection.Trigger;

namespace SpeakerVisionInspection.Production;

/// <summary>
/// 生产编排服务：把「相机硬触发帧 → 多模型流水线检测 → 最终 OK/NG → PLC/IO 输出」串成闭环。
/// - 帧缓冲在事件线程同步拷贝（相机缓冲事件后即释放），工作线程异步转换 + 推理，不阻塞采集。
/// - 有界队列（容量 1）：Busy 期间到达的触发帧记为溢出（TriggerStateMachine.OverrunCount），不无限排队。
/// - PLC 语义：Ready（等待触发）→ Busy（处理中）→ OK/NG → Ready；错误 → Error。
/// - NG 时输出相机 IO 脉冲（Line 输出给 PLC）。
/// </summary>
public sealed class CameraInspectionService : IDisposable
{
    private readonly ICameraController _camera;
    private readonly IPlcClient _plc;
    private readonly Pipeline _pipeline;
    private readonly Func<TriggerSettings> _getTriggerSettings;
    private readonly Action<string> _log;
    private readonly string _resultDir;

    private readonly TriggerStateMachine _state = new();
    private readonly Channel<FrameSnapshot> _queue = Channel.CreateBounded<FrameSnapshot>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly object _jsonLock = new();
    private Task? _worker;
    private DateTime? _triggerDetectedAt;
    private DateTime _lastTriggerAt = DateTime.MinValue;
    private bool _disposed;

    public CameraInspectionService(
        ICameraController camera,
        IPlcClient plc,
        Pipeline pipeline,
        Func<TriggerSettings> getTriggerSettings,
        string resultDir,
        Action<string>? log = null)
    {
        _camera = camera;
        _plc = plc;
        _pipeline = pipeline;
        _getTriggerSettings = getTriggerSettings;
        _resultDir = resultDir;
        _log = log ?? (_ => { });

        _camera.FrameReceived += Camera_FrameReceived;
        _camera.TriggerDetected += Camera_TriggerDetected;
        _camera.TriggerWaitTimeout += Camera_TriggerWaitTimeout;
        _camera.CameraError += Camera_CameraError;
        _state.StateChanged += (_, _) => RaiseStateChanged();

        _worker = Task.Run(WorkerLoop);
    }

    /// <summary>当前触发状态机状态。</summary>
    public TriggerState State => _state.State;

    /// <summary>最近一次错误原因（状态机 LastError 转发）。</summary>
    public string? LastError => _state.LastError;

    public event Action<DetectionResult, Mat?, float[,]?, Mat?>? Preview;
    public event Action<DetectionResult>? Result;
    public event Action<TriggerState>? StateChanged;

    /// <summary>进入生产：状态机 Armed + 回 PLC Ready。相机已由 UI 切到硬触发并 StartHardTrigger。</summary>
    public void Arm()
    {
        if (_state.Arm())
        {
            _log("[生产] 已进入生产，等待 PLC 触发");
            _ = _plc.SendReadyAsync();
            RaiseStateChanged();
        }
    }

    /// <summary>退出生产：回 PLC Idle + 状态机 Disarm。</summary>
    public void Disarm()
    {
        if (_state.Disarm())
        {
            _log("[生产] 已退出生产");
            _ = _plc.SendIdleAsync();
            RaiseStateChanged();
        }
    }

    private void Camera_FrameReceived(object? sender, CameraFrameEventArgs e)
    {
        // 仅在生产状态（Armed/Busy）处理触发帧；预览/软触发帧不进检测链路。
        if (_state.State is not (TriggerState.Armed or TriggerState.Busy))
        {
            return;
        }

        // 事件线程同步拷贝相机缓冲（事件返回后缓冲即被控制器释放）。
        var frame = e.Frame;
        var data = new byte[frame.DataLength];
        System.Runtime.InteropServices.Marshal.Copy(frame.Data, data, 0, frame.DataLength);
        var snapshot = new FrameSnapshot(data, frame.Width, frame.Height, frame.PixelFormat);

        // 最小触发间隔校验（过快触发记溢出）。
        var now = DateTime.UtcNow;
        var minInterval = _getTriggerSettings().MinTriggerIntervalMs;
        var tooFast = minInterval > 0 && (now - _lastTriggerAt).TotalMilliseconds < minInterval;
        _lastTriggerAt = now;
        if (tooFast)
        {
            _log($"[生产] 触发间隔过短（{minInterval:F0}ms 内重复触发）");
            _ = _plc.SendErrorAsync("TRIGGER_TOO_FAST");
            return;
        }

        // 溢出自愈：Error 态收到有效触发帧即恢复等待。
        if (_state.State == TriggerState.Error)
        {
            _log($"[生产] 从错误恢复（{_state.LastError ?? "未知"}），重新等待触发");
            _state.Reset();
            RaiseStateChanged();
        }

        if (!_state.TriggerReceived())
        {
            _log($"[生产] 触发溢出: {_state.LastError}");
            if (_state.State != TriggerState.Error)
            {
                _ = _plc.SendErrorAsync("OVERFLOW");
                RaiseStateChanged();
            }
            return;
        }

        RaiseStateChanged();
        if (!_queue.Writer.TryWrite(snapshot))
        {
            _log("[生产] 处理队列已满，丢弃本帧");
        }
    }

    private async Task WorkerLoop()
    {
        try
        {
            await foreach (var snapshot in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                await ProcessFrameAsync(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"[生产] 工作线程异常退出: {ex}");
        }
    }

    private async Task ProcessFrameAsync(FrameSnapshot snapshot)
    {
        try
        {
            _ = _plc.SendBusyAsync();
            using var bgr = CameraFrameMatConverter.ToBgrMat(snapshot.Data, snapshot.Width, snapshot.Height, snapshot.PixelFormat);
            var imageName = $"CAM_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            var pr = _pipeline.Run(
                bgr,
                imageName,
                cameraIoOutput: settings => _camera.PulseNgOutputAsync(settings).GetAwaiter().GetResult());
            var result = ToDetectionResult(pr);

            _log($"[检测] {imageName} -> {result.Decision} (节点={result.NodeDetails.Count})");
            Result?.Invoke(result);
            Preview?.Invoke(result, bgr.Clone(), pr.HeatMap, pr.DisplayImage);
            // 未交给 Preview 的节点图像由本服务释放（DisplayImage 由订阅方负责）
            foreach (var kv in pr.NodeImages)
            {
                if (!ReferenceEquals(kv.Value, pr.DisplayImage)) kv.Value.Dispose();
            }
            WriteLocalJson(result);

            await _plc.SendResultAsync(result.Decision == "NG" ? PlcResult.Ng : PlcResult.Ok);

            _state.FrameProcessed();
        }
        catch (Exception ex)
        {
            _log($"[生产] 检测失败: {ex.Message}");
            _state.ProcessingError($"检测异常: {ex.Message}");
            _ = _plc.SendErrorAsync("DETECT_ERROR");
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    private void Camera_TriggerDetected(object? sender, EventArgs e)
    {
        _triggerDetectedAt = DateTime.UtcNow;
        if (_state.State == TriggerState.Error)
        {
            _log($"[生产] 从错误恢复（{_state.LastError ?? "未知"}），重新等待触发");
            _state.Reset();
            RaiseStateChanged();
        }
    }

    private void Camera_TriggerWaitTimeout(object? sender, EventArgs e)
    {
        // 仅 Armed 且已检测到触发（FrameStart）后超时仍无帧才报取图超时；Waiting Trigger 无限停留。
        if (_state.State != TriggerState.Armed || _triggerDetectedAt is null)
        {
            return;
        }

        var grabTimeout = _getTriggerSettings().GrabTimeoutMs;
        if ((DateTime.UtcNow - _triggerDetectedAt.Value).TotalMilliseconds < grabTimeout)
        {
            return;
        }

        if (_state.GrabTimeout())
        {
            _log($"[生产] 取图超时: 检测到触发后 {grabTimeout:F0}ms 内未收到相机帧");
            _ = _plc.SendErrorAsync("GRAB_TIMEOUT");
            RaiseStateChanged();
        }
    }

    private void Camera_CameraError(object? sender, EventArgs e)
    {
        if (_state.CameraDisconnected())
        {
            _log($"[生产] 相机断线: {_state.LastError ?? "采集错误"}");
            _ = _plc.SendErrorAsync("CAMERA_DISCONNECTED");
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(_state.State);

    private static DetectionResult ToDetectionResult(PipelineResult pr)
    {
        var d = new DetectionResult(
            pr.Image, double.NaN, pr.Threshold, pr.Decision, pr.ProcessedAt, pr.Error)
        {
            NodeDetails = pr.NodeValues,
        };
        if (pr.NodeValues.TryGetValue(pr.NodeValues.Keys.FirstOrDefault() ?? "", out var first)
            && first.TryGetValue("score", out var sc) && double.TryParse(sc, out var score))
        {
            return d with { Score = score };
        }
        return d;
    }

    private void WriteLocalJson(DetectionResult result)
    {
        if (string.IsNullOrWhiteSpace(_resultDir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_resultDir);
            var outPath = Path.Combine(_resultDir, Path.GetFileNameWithoutExtension(result.Image) + ".json");
            var obj = new
            {
                image = result.Image,
                score = double.IsNaN(result.Score) ? (double?)null : result.Score,
                threshold = result.Threshold,
                decision = result.Decision,
                processed_at = result.ProcessedAt,
                error = result.Error,
                nodes = result.NodeDetails,
            };
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
            });
            lock (_jsonLock)
            {
                File.WriteAllText(outPath, json, System.Text.Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _log($"[落盘] 写入结果失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _camera.FrameReceived -= Camera_FrameReceived;
        _camera.TriggerDetected -= Camera_TriggerDetected;
        _camera.TriggerWaitTimeout -= Camera_TriggerWaitTimeout;
        _camera.CameraError -= Camera_CameraError;

        try
        {
            _queue.Writer.TryComplete();
            _cts.Cancel();
            _worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
