namespace SpeakerVisionInspection.Camera;

public enum CameraConnectionState
{
    Disconnected,
    Connected,
}

/// <summary>数值节点的可设范围（相机回读）。</summary>
public readonly record struct CameraFloatRange(double Min, double Max)
{
    public double Clamp(double value) => Math.Clamp(value, Min, Max);

    public bool Contains(double value) => value >= Min && value <= Max;
}

public sealed class CameraFrameEventArgs : EventArgs
{
    public required CameraFrame Frame { get; init; }
}

/// <summary>相机控制接缝。UI 与采集逻辑只依赖此接口；真实实现包装 MvCameraControl SDK，测试用 Fake。</summary>
public interface ICameraController : IDisposable
{
    CameraConnectionState State { get; }

    /// <summary>是否正在连续采集（预览）。</summary>
    bool IsPreviewing { get; }

    /// <summary>当前采集参数（快照）。</summary>
    CameraParameters Parameters { get; }

    /// <summary>当前触发模式。</summary>
    CameraTriggerMode TriggerMode { get; }

    /// <summary>相机支持的增益范围（连接后回读；未连接返回 null）。</summary>
    CameraFloatRange? GainRange { get; }

    /// <summary>Gamma 节点是否可写（连接后探测；未连接默认 true）。</summary>
    bool GammaSupported { get; }

    /// <summary>是否正在硬触发采集（等待 PLC 触发）。</summary>
    bool IsHardTriggering { get; }

    /// <summary>硬触发等待触发帧超时（GrabTimeout 到达）。</summary>
    event EventHandler? TriggerWaitTimeout;

    /// <summary>硬触发模式下相机检测到外部触发信号（FrameStart 事件）。</summary>
    event EventHandler? TriggerDetected;

    /// <summary>采集过程中相机断线/取帧错误（非超时）。</summary>
    event EventHandler? CameraError;

    /// <summary>预览期间每取到一帧触发（后台线程）；帧在事件返回后被控制器释放，handler 必须同步使用。</summary>
    event EventHandler<CameraFrameEventArgs>? FrameReceived;

    /// <summary>异步枚举在线相机（GigE/USB3）。</summary>
    Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>连接指定相机；连接成功前 State 保持 Disconnected。</summary>
    Task ConnectAsync(CameraInfo camera, CancellationToken cancellationToken = default);

    /// <summary>断开当前相机并释放资源；重复调用无副作用。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>后台循环连续采集并触发 FrameReceived；未连接则抛异常。</summary>
    Task StartPreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>停止采集并释放 SDK 缓冲；未采集时无副作用。</summary>
    Task StopPreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>应用采集参数（实时生效）。</summary>
    Task ApplyParametersAsync(CameraParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>设置触发模式；软/硬触发与连续预览互斥（切换时先停止预览）。</summary>
    Task SetTriggerModeAsync(CameraTriggerMode mode, CancellationToken cancellationToken = default);

    /// <summary>应用触发配置（源/沿/延迟/滤波/超时/间隔），硬触发模式下实时生效。</summary>
    Task ApplyTriggerSettingsAsync(TriggerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>配置相机 IO 输出（用于 NG 结果回传 PLC）。</summary>
    Task ConfigureIoCommunicationAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default);

    /// <summary>按 IO 通信配置输出一次 NG 脉冲。</summary>
    Task PulseNgOutputAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default);

    /// <summary>软触发取单帧并触发 FrameReceived；需已连接且 TriggerMode=Software。</summary>
    Task SoftTriggerAsync(CancellationToken cancellationToken = default);

    /// <summary>开始硬触发采集（等待 PLC 触发帧）；需 TriggerMode=Hardware，与连续预览互斥。</summary>
    Task StartHardTriggerAsync(CancellationToken cancellationToken = default);

    /// <summary>停止硬触发采集并释放缓冲。</summary>
    Task StopHardTriggerAsync(CancellationToken cancellationToken = default);
}
