using System.IO;
using System.Runtime.InteropServices;
using VisionInspection.Services;
using MvCamCtrl.NET;

namespace VisionInspection.Camera;

/// <summary>
/// 基于 MvCameraControl.Net（海康 MVS 相机控制）的相机控制器。
/// native 运行时 MvCameraControl.dll 位于 MVS Runtime 目录，需先加入 DLL 搜索路径。
/// 所有 SDK 调用在同一相机句柄上串行执行（_sync 保护）。
/// </summary>
public sealed class MvCameraControlCameraController : ICameraController
{
    private const uint TLayerTypeGige = 1;
    private const uint TLayerTypeUsb3 = 4;
    private const uint AccessModeExclusive = 1;
    private const int SoftGrabTimeoutMs = 500;
    private const int SoftPollIntervalMs = 50; // 软触发取帧短轮询间隔：每次持有 _sync ≤50ms，防 UI 读状态被阻塞（卡死）
    private const int GrabPollIntervalMs = 50;
    private const int OpenRetryCount = 3;
    private const int OpenRetryDelayMs = 1000;

    private const uint TriggerModeOff = 0;
    private const uint TriggerModeOn = 1;

    // 触发源/触发沿采用符号名设置（SetEnumValueByString）：不同相机/固件的数值枚举不一致，
    // 实测本机 MV-CS032-60GC 仅接受符号名（数值 Line0=2/Line1=3 报 MV_E_GC_ACCESS）。
    private const string TriggerSourceSoftware = "Software";
    private const string TriggerSourceLine0 = "Line0";
    private const string TriggerSourceLine1 = "Line1";
    private const string TriggerActivationFalling = "FallingEdge";
    private const string TriggerActivationRising = "RisingEdge";
    private const string IoLineModeStrobe = "Strobe";

    // 相机事件名：硬触发模式下 PLC 触发 Line0 → 相机曝光 → 触发 "FrameStart"（帧开始）事件。
    // 该事件用于感知"PLC 已触发"，作为取图超时的计时锚点（Waiting Trigger 无限停留，触发后才计超时）。
    // 注意：SDK 无注销回调 API，回调随 MV_CC_CloseDevice_NET 自动释放；用标志防重复注册。
    private const string FrameStartEventName = "FrameStart";

    // 注意：该 SDK 版本中 MV_E_NODATA = 0x80000007（不是旧文档的 0x8000002B）。
    // 硬触发等待（无 PLC 信号）与预览空闲帧均返回它，必须识别为“无数据”而非错误，
    // 否则硬触发一启动就被当作相机断线（CameraError）→ 状态机 Error。
    private static readonly int NoDataError = MyCamera.MV_E_NODATA;
    private static readonly int AccessDeniedError = MyCamera.MV_E_ACCESS_DENIED;
    private static readonly int BusyError = MyCamera.MV_E_BUSY;

    private readonly string _nativeRuntimeDir;
    private readonly object _sync = new();
    private MyCamera? _camera;
    private CameraConnectionState _state = CameraConnectionState.Disconnected;
    private bool _isPreviewing;
    private bool _isHardTriggering;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private CancellationTokenSource? _hardTriggerCts;
    private Task? _hardTriggerTask;
    private CameraParameters _parameters = new();
    private CameraTriggerMode _triggerMode = CameraTriggerMode.Continuous;
    private TriggerSettings _triggerSettings = new();
    private IoCommunicationSettings _ioCommunicationSettings = new();
    private CameraFloatRange? _gainRange;
    private bool _gammaSupported = true;
    private double? _resultingFrameRate;
    private bool? _lastTriggerLineStatus;

    public MvCameraControlCameraController(string? nativeRuntimeDir)
    {
        // 未配置时回退到应用目录内置 native 运行时（相对路径，免安装包无需本机 MVS 安装）
        _nativeRuntimeDir = string.IsNullOrEmpty(nativeRuntimeDir)
            ? Path.Combine(AppContext.BaseDirectory, "sdk", "native", "Win64_x64")
            : nativeRuntimeDir;
        if (!string.IsNullOrEmpty(_nativeRuntimeDir) && Directory.Exists(_nativeRuntimeDir))
        {
            AddDllDirectory(_nativeRuntimeDir);
        }
    }

    public CameraConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsPreviewing
    {
        get
        {
            lock (_sync)
            {
                return _isPreviewing;
            }
        }
    }

    public bool IsHardTriggering
    {
        get
        {
            lock (_sync)
            {
                return _isHardTriggering;
            }
        }
    }

    public event EventHandler? TriggerWaitTimeout;

    public event EventHandler? TriggerDetected;

    public event EventHandler? CameraError;

    private MyCamera.cbEventdelegateEx? _triggerEventCallback;
    private bool _triggerEventRegistered;

    public CameraParameters Parameters
    {
        get
        {
            lock (_sync)
            {
                return _parameters with { };
            }
        }
    }

    public CameraTriggerMode TriggerMode
    {
        get
        {
            lock (_sync)
            {
                return _triggerMode;
            }
        }
    }

    public CameraFloatRange? GainRange
    {
        get
        {
            lock (_sync)
            {
                return _gainRange;
            }
        }
    }

    public bool GammaSupported
    {
        get
        {
            lock (_sync)
            {
                return _gammaSupported;
            }
        }
    }

    /// <summary>相机回读的实际帧率（fps，连接后回读；未连接或相机不支持返回 null）。</summary>
    public double? ResultingFrameRate
    {
        get
        {
            lock (_sync)
            {
                return _resultingFrameRate;
            }
        }
    }

    public event EventHandler<CameraFrameEventArgs>? FrameReceived;

    public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CameraInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Enumerate();
        }, cancellationToken);
    }

    public Task ConnectAsync(CameraInfo camera, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect(camera);
        }, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 设备层与 UI 无亲和：ConfigureAwait(false) 防止「UI 线程 GetResult 同步等待 + 内部 await 回投 UI 上下文」死锁
        // （曾导致预览/硬触发激活时关闭软件：Window_Closed 挂死 → 相机不释放 + 弹窗残留桌面）。
        await StopPreviewAsync(cancellationToken).ConfigureAwait(false);
        await StopHardTriggerAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _camera?.MV_CC_CloseDevice_NET();
            _camera?.MV_CC_DestroyDevice_NET();
            _camera = null;
            _state = CameraConnectionState.Disconnected;
            _gainRange = null;
            _gammaSupported = true;
        }
    }

    public Task StartPreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_isPreviewing)
                {
                    return;
                }

                if (_camera is null || _state != CameraConnectionState.Connected)
                {
                    throw new InvalidOperationException("未连接相机，无法开始预览");
                }

                if (_triggerMode != CameraTriggerMode.Continuous)
                {
                    throw new InvalidOperationException("触发模式非连续，无法开始预览（请先切换为连续）");
                }

                var ret = _camera.MV_CC_StartGrabbing_NET();
                if (ret != 0)
                {
                    throw new InvalidOperationException($"开始采集失败（错误码 {ret}）");
                }

                _isPreviewing = true;
                _previewCts = new CancellationTokenSource();
                var token = _previewCts.Token;
                _previewTask = Task.Run(() => PreviewLoop(token), token);
            }
        }, cancellationToken);
    }

    public async Task StopPreviewAsync(CancellationToken cancellationToken = default)
    {
        Task? loopTask;
        lock (_sync)
        {
            if (!_isPreviewing)
            {
                return;
            }

            _isPreviewing = false;
            _previewCts?.Cancel();
            loopTask = _previewTask;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("预览循环退出异常", ex);
            }
        }

        lock (_sync)
        {
            _previewCts?.Dispose();
            _previewCts = null;
            _previewTask = null;
            _camera?.MV_CC_StopGrabbing_NET();
        }
    }

    public Task ApplyParametersAsync(CameraParameters parameters, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _parameters = parameters with { };
                ApplyParametersLocked();
            }
        }, cancellationToken);
    }

    public async Task SetTriggerModeAsync(CameraTriggerMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 软/硬触发与连续预览互斥：切换为非连续时先停止预览
        if (mode != CameraTriggerMode.Continuous)
        {
            await StopPreviewAsync(cancellationToken).ConfigureAwait(false);
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _triggerMode = mode;
                if (_camera is null)
                {
                    return;
                }

                switch (mode)
                {
                    case CameraTriggerMode.Software:
                        // TriggerMode=On，Source=Software，等待软触发单帧
                        SetEnum("TriggerMode", TriggerModeOn, throwOnError: true);
                        SetEnumByString("TriggerSource", TriggerSourceSoftware, throwOnError: true);
                        break;
                    case CameraTriggerMode.Hardware:
                        // TriggerMode=On，Source=Line0/Line1，触发沿/延迟/滤波来自触发配置
                        SetEnum("TriggerMode", TriggerModeOn, throwOnError: true);
                        ApplyTriggerSettingsLocked(_triggerSettings);
                        break;
                    default:
                        SetEnum("TriggerMode", TriggerModeOff, throwOnError: true);
                        break;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task ApplyTriggerSettingsAsync(TriggerSettings settings, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _triggerSettings = settings with { };
                if (_triggerMode == CameraTriggerMode.Hardware)
                {
                    ApplyTriggerSettingsLocked(_triggerSettings);
                }
            }
        }, cancellationToken);
    }

    public Task ConfigureIoCommunicationAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _ioCommunicationSettings = CloneIoSettings(settings);
                if (!_ioCommunicationSettings.Enabled)
                {
                    if (_camera is not null && _state == CameraConnectionState.Connected)
                    {
                        SetBool("StrobeEnable", false, throwOnError: false);
                    }
                    return;
                }

                EnsureConnectedLocked("配置 IO 通信");
                ConfigureIoOutputLocked(_ioCommunicationSettings);
            }
        }, cancellationToken);
    }

    public async Task PulseNgOutputAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || !string.Equals(settings.OutputMode, "NgOnly", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var configured = CloneIoSettings(settings);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                EnsureConnectedLocked("输出 NG 脉冲");
                ConfigureIoOutputLocked(configured);
                SetBool("StrobeEnable", true, throwOnError: true);
                LogIoOutputLineStatusLocked(configured, "NG 输出开始");
            }
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var pulseMs = Math.Max(1, (int)Math.Round(configured.PulseMs));
            await Task.Delay(pulseMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_camera is null || _state != CameraConnectionState.Connected)
                    {
                        return;
                    }

                    SetEnumByString("LineSelector", GetIoOutputLine(configured), throwOnError: false);
                    LogIoOutputLineStatusLocked(configured, "NG 输出结束前");
                    SetBool("StrobeEnable", false, throwOnError: false);
                    LogIoOutputLineStatusLocked(configured, "NG 输出结束后");
                }
            }).ConfigureAwait(false);
        }
    }

    private void ApplyTriggerSettingsLocked(TriggerSettings settings)
    {
        if (_camera is null)
        {
            return;
        }

        var source = settings.TriggerSource == "Line1" ? TriggerSourceLine1 : TriggerSourceLine0;
        var activation = settings.TriggerActivation == "Falling" ? TriggerActivationFalling : TriggerActivationRising;

        // 触发源/触发沿是硬触发链路的根基，失败必须抛出（不得静默吞掉后误报“已应用”）
        SetEnumByString("TriggerSource", source, throwOnError: true);
        SetEnumByString("TriggerActivation", activation, throwOnError: true);
        if (settings.TriggerDelayUs > 0)
        {
            var ret = _camera.MV_CC_SetFloatValue_NET("TriggerDelay", (float)settings.TriggerDelayUs);
            if (ret != 0)
            {
                AppLog.Warn($"设置触发延迟失败（错误码 {ret}）");
            }
        }

        if (settings.TriggerFilterUs > 0)
        {
            var ret = _camera.MV_CC_SetFloatValue_NET("LineFilterWidth", (float)settings.TriggerFilterUs);
            if (ret != 0)
            {
                AppLog.Warn($"设置输入滤波失败（错误码 {ret}）");
            }
        }

        if (settings.BurstFrameCount >= 1)
        {
            var ret = _camera.MV_CC_SetIntValue_NET("AcquisitionBurstFrameCount", unchecked((uint)settings.BurstFrameCount));
            if (ret != 0)
            {
                AppLog.Warn($"设置条件触发数失败（错误码 {ret}）");
            }
        }
    }

    private void ConfigureIoOutputLocked(IoCommunicationSettings settings)
    {
        var outputLine = GetIoOutputLine(settings);
        var strobeSource = string.IsNullOrWhiteSpace(settings.StrobeSource) ? "FrameTriggerWait" : settings.StrobeSource.Trim();
        SetEnumByString("LineSelector", outputLine, throwOnError: true);
        SetEnumByString("LineMode", IoLineModeStrobe, throwOnError: true);
        SetEnumByString("LineSource", strobeSource, throwOnError: true);
        SetBool("LineInverter", IsInvertedLevel(settings), throwOnError: true);
        SetBool("StrobeEnable", false, throwOnError: true);
    }

    public Task StartHardTriggerAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_isHardTriggering)
                {
                    return;
                }

                if (_camera is null || _state != CameraConnectionState.Connected)
                {
                    throw new InvalidOperationException("未连接相机，无法开始硬触发");
                }

                if (_triggerMode != CameraTriggerMode.Hardware)
                {
                    throw new InvalidOperationException("未处于硬触发模式");
                }

                if (_isPreviewing)
                {
                    throw new InvalidOperationException("连续预览中，请先停止预览再硬触发");
                }

                // 注册 FrameStart 事件回调：PLC 触发 → 相机曝光 → 触发 TriggerDetected（超时计时锚点）。
                // 必须先注册再开始采集，避免 StartGrabbing 后到注册完成前漏掉首个触发事件。
                // SDK 无注销 API，回调随 CloseDevice 释放；标志防重复注册。
                if (!_triggerEventRegistered)
                {
                    _triggerEventCallback = OnTriggerEvent;
                    var registerRet = _camera.MV_CC_RegisterEventCallBackEx_NET(FrameStartEventName, _triggerEventCallback, IntPtr.Zero);
                    if (registerRet == 0)
                    {
                        _triggerEventRegistered = true;
                    }
                    else
                    {
                        AppLog.Warn($"注册 {FrameStartEventName} 事件失败（错误码 {registerRet}），取图超时将回退为出帧间隔判定");
                    }
                }

                var ret = _camera.MV_CC_StartGrabbing_NET();
                if (ret != 0)
                {
                    throw new InvalidOperationException($"开始硬触发采集失败（错误码 {ret}）");
                }

                _isHardTriggering = true;
                _lastTriggerLineStatus = null;
                LogTriggerLineStatusLocked(force: true);
                _hardTriggerCts = new CancellationTokenSource();
                var token = _hardTriggerCts.Token;
                _hardTriggerTask = Task.Run(() => HardTriggerLoop(token), token);
            }
        }, cancellationToken);
    }

    public async Task StopHardTriggerAsync(CancellationToken cancellationToken = default)
    {
        Task? loopTask;
        lock (_sync)
        {
            if (!_isHardTriggering)
            {
                return;
            }

            _isHardTriggering = false;
            _hardTriggerCts?.Cancel();
            loopTask = _hardTriggerTask;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("硬触发循环退出异常", ex);
            }
        }

        lock (_sync)
        {
            _hardTriggerCts?.Dispose();
            _hardTriggerCts = null;
            _hardTriggerTask = null;
            _camera?.MV_CC_StopGrabbing_NET();
        }
    }

    private void LogIoOutputLineStatusLocked(IoCommunicationSettings settings, string phase)
    {
        if (_camera is null)
        {
            return;
        }

        var outputLine = GetIoOutputLine(settings);
        SetEnumByString("LineSelector", outputLine, throwOnError: false);
        var status = false;
        var ret = _camera.MV_CC_GetBoolValue_NET("LineStatus", ref status);
        if (ret == 0)
        {
            AppLog.Info($"[相机IO诊断] {phase}: {outputLine}.LineStatus={(status ? "High" : "Low")}，StrobeSource={settings.StrobeSource}，有效电平={settings.ActiveLevel}");
        }
        else
        {
            AppLog.Warn($"[相机IO诊断] {phase}: 读取 {outputLine}.LineStatus 失败（错误码 {ret}）");
        }
    }

    private void HardTriggerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = new MyCamera.MV_FRAME_OUT();
            CameraFrame? converted = null;
            try
            {
                lock (_sync)
                {
                    if (!_isHardTriggering || _camera is null)
                    {
                        break;
                    }

                    // 短轮询取帧（50ms）：避免长时间持有 _sync 导致 UI 线程读状态被阻塞数秒（卡死）。
                    // 实际取图超时由 MainWindow 累加 TriggerWaitTimeout 事件实现（GrabTimeoutMs）。
                    var ret = _camera.MV_CC_GetImageBuffer_NET(ref frame, GrabPollIntervalMs);
                    if (ret != 0)
                    {
                        if (ret == NoDataError)
                        {
                            LogTriggerLineStatusLocked(force: false);
                            // 无触发帧（超时）：可能是常态等待；若超过配置取图超时仍无帧则上报一次
                            TriggerWaitTimeout?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            // 非超时错误（断线等）：上报并退出循环
                            AppLog.Warn($"硬触发取帧失败（错误码 {ret}）");
                            CameraError?.Invoke(this, EventArgs.Empty);
                            break;
                        }

                        continue;
                    }

                    converted = ConvertFrame(_camera, ref frame);
                }

                if (converted is not null)
                {
                    FrameReceived?.Invoke(this, new CameraFrameEventArgs { Frame = converted });
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (converted is not null)
                    {
                        Marshal.FreeHGlobal(converted.Data);
                    }

                    if (frame.pBufAddr != IntPtr.Zero)
                    {
                        _camera?.MV_CC_FreeImageBuffer_NET(ref frame);
                    }
                }
            }
        }
    }

    private void OnTriggerEvent(ref MvCamCtrl.NET.MyCamera.MV_EVENT_OUT_INFO pEventInfo, IntPtr pUser)
    {
        // 相机收到 PLC 触发信号开始曝光时触发（FrameStart）。SDK 后台线程调用，事件 handler 自行调度。
        AppLog.Info("[硬触发诊断] 收到相机 FrameStart 事件");
        TriggerDetected?.Invoke(this, EventArgs.Empty);
    }

    private void LogTriggerLineStatusLocked(bool force)
    {
        if (_camera is null)
        {
            return;
        }

        var source = string.IsNullOrWhiteSpace(_triggerSettings.TriggerSource) ? TriggerSourceLine0 : _triggerSettings.TriggerSource;
        var selectRet = _camera.MV_CC_SetEnumValueByString_NET("LineSelector", source);
        if (selectRet != 0)
        {
            if (force)
            {
                AppLog.Warn($"[硬触发诊断] 选择触发输入线 {source} 失败（错误码 {selectRet}）");
            }
            return;
        }

        var status = false;
        var ret = _camera.MV_CC_GetBoolValue_NET("LineStatus", ref status);
        if (ret != 0)
        {
            if (force)
            {
                AppLog.Warn($"[硬触发诊断] 读取 {source}.LineStatus 失败（错误码 {ret}）");
            }
            return;
        }

        if (force || _lastTriggerLineStatus != status)
        {
            _lastTriggerLineStatus = status;
            AppLog.Info($"[硬触发诊断] {source}.LineStatus={(status ? "High" : "Low")}，触发沿={_triggerSettings.TriggerActivation}");
        }
    }

    public Task SoftTriggerAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            CameraFrame? converted = null;

            // 快速段（持锁）：校验 + 开始采集。
            // 不能在持有 _sync 时等帧——GetImageBuffer 阻塞期间 UI 线程读状态
            // （State/TriggerMode/IsPreviewing）会被一起卡住数秒（重复点击卡死的根源）。
            lock (_sync)
            {
                if (_camera is null || _state != CameraConnectionState.Connected)
                {
                    throw new InvalidOperationException("未连接相机，无法软触发");
                }

                if (_triggerMode != CameraTriggerMode.Software)
                {
                    throw new InvalidOperationException("未处于软触发模式");
                }

                if (_isPreviewing)
                {
                    throw new InvalidOperationException("连续预览中，请先停止预览再软触发");
                }

                var startRet = _camera.MV_CC_StartGrabbing_NET();
                if (startRet != 0)
                {
                    throw new InvalidOperationException($"开始采集失败（错误码 {startRet}）");
                }

                var trigRet = _camera.MV_CC_TriggerSoftwareExecute_NET();
                if (trigRet != 0)
                {
                    throw new InvalidOperationException($"软触发失败（错误码 {trigRet}）");
                }
            }

            try
            {
                // 取帧段（短轮询，对齐 HardTriggerLoop 的防卡死模式）：每次持有 _sync ≤ SoftPollIntervalMs
                var deadline = Environment.TickCount64 + SoftGrabTimeoutMs;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        if (_camera is null || _state != CameraConnectionState.Connected)
                        {
                            throw new InvalidOperationException("取帧中相机已断开");
                        }

                        var frame = new MyCamera.MV_FRAME_OUT();
                        var ret = _camera.MV_CC_GetImageBuffer_NET(ref frame, SoftPollIntervalMs);
                        if (ret == 0)
                        {
                            try
                            {
                                converted = ConvertFrame(_camera, ref frame);
                            }
                            finally
                            {
                                _camera.MV_CC_FreeImageBuffer_NET(ref frame);
                            }
                        }
                    }

                    if (converted is not null)
                    {
                        break;
                    }

                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new InvalidOperationException($"触发后取帧失败（超时 {SoftGrabTimeoutMs}ms）");
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _camera?.MV_CC_StopGrabbing_NET();
                }
            }

            if (converted is not null)
            {
                try
                {
                    FrameReceived?.Invoke(this, new CameraFrameEventArgs { Frame = converted });
                }
                finally
                {
                    Marshal.FreeHGlobal(converted.Data);
                }
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        // 同步阻塞断开：受 GetImageBuffer 超时（≤500ms）约束，退出时短暂等待可接受；
        // 异常交由调用方（Window_Closed 的 try/catch）处理。
        DisconnectAsync().GetAwaiter().GetResult();
    }

    private IReadOnlyList<CameraInfo> Enumerate()
    {
        var result = new EnumerateResult();
        TryEnumerateLayer(TLayerTypeGige, "GigE", result);
        TryEnumerateLayer(TLayerTypeUsb3, "USB3", result);

        if (result.SucceededLayers == 0)
        {
            throw new InvalidOperationException($"枚举相机失败: {string.Join("; ", result.Failures)}");
        }

        if (result.Failures.Count > 0)
        {
            AppLog.Warn($"部分传输层枚举失败: {string.Join("; ", result.Failures)}");
        }

        return result.Devices;
    }

    private static void TryEnumerateLayer(uint tLayerType, string layerName, EnumerateResult result)
    {
        try
        {
            var stDevList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            var ret = MyCamera.MV_CC_EnumDevices_NET(tLayerType, ref stDevList);
            if (ret != 0)
            {
                result.Failures.Add($"{layerName} (错误码 {ret})");
                return;
            }

            result.SucceededLayers++;
            foreach (var info in ReadDeviceInfos(stDevList))
            {
                var cam = ToCameraInfo(info, layerName);
                // 同一相机可能因持久 IP / 当前 IP 配置被枚举多次，按序列号去重
                if (!string.IsNullOrEmpty(cam.SerialNumber) &&
                    result.Devices.Any(d => d.SerialNumber == cam.SerialNumber))
                {
                    continue;
                }

                result.Devices.Add(cam);
            }
        }
        catch (Exception ex)
        {
            result.Failures.Add($"{layerName} ({ex.Message})");
        }
    }

    private void Connect(CameraInfo camera)
    {
        lock (_sync)
        {
            if (_state == CameraConnectionState.Connected)
            {
                return;
            }

            // 断开/进程退出后 GigE 会话可能残留：OpenDevice 报 ACCESS_DENIED/BUSY 时按固定间隔重试
            for (var attempt = 0; ; attempt++)
            {
                var stDevInfo = FindDeviceBySerial(camera.SerialNumber);
                var cam = new MyCamera();
                var retCreate = cam.MV_CC_CreateDevice_NET(ref stDevInfo);
                if (retCreate != 0)
                {
                    cam.MV_CC_DestroyDevice_NET();
                    throw new InvalidOperationException($"创建相机句柄失败（错误码 {retCreate}）");
                }

                var retOpen = cam.MV_CC_OpenDevice_NET(AccessModeExclusive, 0);
                if (retOpen == 0)
                {
                    _camera = cam;
                    _state = CameraConnectionState.Connected;
                    ReadParameterCapabilitiesLocked();
                    return;
                }

                cam.MV_CC_DestroyDevice_NET();
                if (attempt < OpenRetryCount && (retOpen == AccessDeniedError || retOpen == BusyError))
                {
                    AppLog.Warn($"设备访问被拒（错误码 {retOpen}），{OpenRetryDelayMs}ms 后重试（第 {attempt + 1} 次）");
                    Thread.Sleep(OpenRetryDelayMs);
                    continue;
                }

                throw new InvalidOperationException($"连接相机失败（错误码 {retOpen}）");
            }
        }
    }

    private MyCamera.MV_CC_DEVICE_INFO FindDeviceBySerial(string serialNumber)
    {
        var all = new List<MyCamera.MV_CC_DEVICE_INFO>();
        CollectRawInfos(TLayerTypeGige, all);
        CollectRawInfos(TLayerTypeUsb3, all);

        foreach (var info in all)
        {
            if (string.Equals(DecodeDeviceInfo(info).Serial, serialNumber, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        throw new InvalidOperationException($"未找到序列号为 {serialNumber} 的相机（可能已离线）");
    }

    private static void CollectRawInfos(uint tLayerType, List<MyCamera.MV_CC_DEVICE_INFO> target)
    {
        try
        {
            var stDevList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            var ret = MyCamera.MV_CC_EnumDevices_NET(tLayerType, ref stDevList);
            if (ret != 0)
            {
                return;
            }

            target.AddRange(ReadDeviceInfos(stDevList));
        }
        catch (Exception ex)
        {
            AppLog.Warn("枚举传输层失败", ex);
        }
    }

    private static IEnumerable<MyCamera.MV_CC_DEVICE_INFO> ReadDeviceInfos(MyCamera.MV_CC_DEVICE_INFO_LIST devList)
    {
        for (var i = 0; i < devList.nDeviceNum && i < devList.pDeviceInfo.Length; i++)
        {
            var ptr = devList.pDeviceInfo[i];
            if (ptr != IntPtr.Zero)
            {
                yield return Marshal.PtrToStructure<MyCamera.MV_CC_DEVICE_INFO>(ptr);
            }
        }
    }

    private static CameraInfo ToCameraInfo(MyCamera.MV_CC_DEVICE_INFO info, string interfaceType)
    {
        var decoded = DecodeDeviceInfo(info);
        var displayName = FirstNonEmpty(decoded.UserDefinedName, decoded.ModelName, decoded.Serial);
        return new CameraInfo(
            string.IsNullOrEmpty(displayName) ? "未知相机" : displayName,
            decoded.Serial,
            decoded.ModelName,
            decoded.IpAddress,
            interfaceType);
    }

    private static DeviceData DecodeDeviceInfo(MyCamera.MV_CC_DEVICE_INFO info)
    {
        // SpecialInfo 为 byte[] 联合体：GigE -> MV_GIGE_DEVICE_INFO，USB3 -> MV_USB3_DEVICE_INFO
        var gige = info.SpecialInfo.stGigEInfo;
        if (gige.Length >= Marshal.SizeOf<MyCamera.MV_GIGE_DEVICE_INFO>())
        {
            var g = BytesToStruct<MyCamera.MV_GIGE_DEVICE_INFO>(gige);
            return new DeviceData(g.chUserDefinedName, g.chSerialNumber, g.chModelName, UInt32ToIp(g.nCurrentIp));
        }

        var usb = info.SpecialInfo.stUsb3VInfo;
        if (usb.Length >= Marshal.SizeOf<MyCamera.MV_USB3_DEVICE_INFO>())
        {
            var u = BytesToStruct<MyCamera.MV_USB3_DEVICE_INFO>(usb);
            return new DeviceData(u.chUserDefinedName, u.chSerialNumber, u.chModelName, null);
        }

        return new DeviceData(null, "", null, null);
    }

    internal static string? UInt32ToIp(uint value)
    {
        if (value == 0)
        {
            return null;
        }

        // SDK 的 nCurrentIp 为网络字节序（大端），例如 0xA9FE4CFD = 169.254.76.253
        var bytes = BitConverter.GetBytes(value);
        return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}";
    }

    private void PreviewLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = new MyCamera.MV_FRAME_OUT();
            CameraFrame? converted = null;
            try
            {
                lock (_sync)
                {
                    if (!_isPreviewing || _camera is null)
                    {
                        break;
                    }

                    // 短轮询取帧（50ms）：避免每帧长时间持有 _sync 阻塞 UI 读状态
                    var ret = _camera.MV_CC_GetImageBuffer_NET(ref frame, GrabPollIntervalMs);
                    if (ret != 0)
                    {
                        if (ret != NoDataError)
                        {
                            AppLog.Warn($"取帧失败（错误码 {ret}）");
                        }

                        continue;
                    }

                    converted = ConvertFrame(_camera, ref frame);
                }

                if (converted is not null)
                {
                    FrameReceived?.Invoke(this, new CameraFrameEventArgs { Frame = converted });
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (converted is not null)
                    {
                        Marshal.FreeHGlobal(converted.Data);
                    }

                    if (frame.pBufAddr != IntPtr.Zero)
                    {
                        _camera?.MV_CC_FreeImageBuffer_NET(ref frame);
                    }
                }
            }
        }
    }

    private CameraFrame? ConvertFrame(MyCamera cam, ref MyCamera.MV_FRAME_OUT frame)
    {
        var info = frame.stFrameInfo;
        if (info.nWidth == 0 || info.nHeight == 0)
        {
            return null;
        }

        var targetFormat = _parameters.PixelFormat == "RGB8" ? CameraPixelFormat.Rgb8 : CameraPixelFormat.Mono8;
        var channels = CameraPixelFormat.ChannelsOf(targetFormat);
        var dataLength = (uint)info.nWidth * (uint)info.nHeight * (uint)channels;
        var dst = Marshal.AllocHGlobal((int)dataLength);
        try
        {
            var cvt = new MyCamera.MV_PIXEL_CONVERT_PARAM
            {
                nWidth = info.nWidth,
                nHeight = info.nHeight,
                enSrcPixelType = info.enPixelType,
                pSrcData = frame.pBufAddr,
                nSrcDataLen = info.nFrameLen,
                enDstPixelType = (MyCamera.MvGvspPixelType)targetFormat,
                pDstBuffer = dst,
                nDstLen = dataLength,
                nDstBufferSize = dataLength,
            };

            var ret = cam.MV_CC_ConvertPixelType_NET(ref cvt);
            if (ret != 0)
            {
                Marshal.FreeHGlobal(dst);
                AppLog.Warn($"像素转换失败（错误码 {ret}）");
                return null;
            }

            return new CameraFrame
            {
                Width = info.nWidth,
                Height = info.nHeight,
                PixelFormat = targetFormat,
                Data = dst,
                DataLength = (int)dataLength,
            };
        }
        catch
        {
            Marshal.FreeHGlobal(dst);
            throw;
        }
    }

    private void ApplyParametersLocked()
    {
        if (_camera is null)
        {
            return;
        }

        // 自动模式先于数值设置：自动曝光/增益生效时数值节点只读，越序写入只会刷错误码
        var exposureAuto = NormalizeAutoMode(_parameters.ExposureAuto);
        SetEnumByString("ExposureAuto", exposureAuto);
        if (exposureAuto == "Off")
        {
            SetFloat("ExposureTime", (float)_parameters.ExposureTimeUs);
        }

        var gainAuto = NormalizeAutoMode(_parameters.GainAuto);

        // 增益按相机回读范围钳制（越界 SDK 返回 MV_E_GC_RANGE，静默失败导致"增益无效"）
        var gain = _parameters.Gain;
        if (_gainRange is { } range)
        {
            var clamped = range.Clamp(gain);
            if (Math.Abs(clamped - gain) > 0.0001)
            {
                AppLog.Warn($"增益 {gain:F1} 超出相机范围 [{range.Min:F1}, {range.Max:F1}]，已钳制为 {clamped:F1}");
                _parameters = _parameters with { Gain = clamped };
                gain = clamped;
            }
        }

        SetEnumByString("GainAuto", gainAuto);
        if (gainAuto == "Off")
        {
            SetFloat("Gain", (float)gain);
        }

        if (_gammaSupported)
        {
            SetFloat("Gamma", (float)_parameters.Gamma);
        }

        if (_parameters.ImageWidth > 0)
        {
            SetInt("Width", _parameters.ImageWidth);
        }

        if (_parameters.ImageHeight > 0)
        {
            SetInt("Height", _parameters.ImageHeight);
        }

        if (_parameters.FrameRate > 0)
        {
            SetBool("AcquisitionFrameRateEnable", true);
            SetFloat("AcquisitionFrameRate", (float)_parameters.FrameRate);
        }

        var pixelFormat = _parameters.PixelFormat == "RGB8" ? CameraPixelFormat.Rgb8 : CameraPixelFormat.Mono8;
        var ret = _camera.MV_CC_SetPixelFormat_NET(pixelFormat);
        if (ret != 0)
        {
            AppLog.Warn($"设置像素格式失败（错误码 {ret}）");
        }
    }

    private static string NormalizeAutoMode(string? mode) =>
        string.Equals(mode, "Once", StringComparison.OrdinalIgnoreCase) ? "Once"
        : string.Equals(mode, "Continuous", StringComparison.OrdinalIgnoreCase) ? "Continuous"
        : "Off";

    /// <summary>连接后回读相机参数能力：增益范围、增益模式、Gamma 可写性。</summary>
    private void ReadParameterCapabilitiesLocked()
    {
        if (_camera is null)
        {
            return;
        }

        _gainRange = null;
        _gammaSupported = true;
        _resultingFrameRate = null;

        var fv = new MyCamera.MVCC_FLOATVALUE();
        if (_camera.MV_CC_GetFloatValue_NET("Gain", ref fv) == 0 && fv.fMax > fv.fMin)
        {
            _gainRange = new CameraFloatRange(fv.fMin, fv.fMax);
            AppLog.Info($"相机增益范围: {fv.fMin:F1} ~ {fv.fMax:F1} dB（当前 {fv.fCurValue:F1}）");
        }
        else
        {
            AppLog.Warn("无法读取相机增益范围，增益将不做钳制");
        }

        // 实际帧率（ResultingFrameRate，只读回显；部分相机在触发模式下才有效）
        var rfv = new MyCamera.MVCC_FLOATVALUE();
        if (_camera.MV_CC_GetFloatValue_NET("ResultingFrameRate", ref rfv) == 0 && rfv.fCurValue > 0)
        {
            _resultingFrameRate = rfv.fCurValue;
        }

        // 自动增益（GainMode=Continuous/Once）会覆盖手动增益设置，应用手动增益前需置 Off
        var gm = new MyCamera.MVCC_ENUMVALUE();
        if (_camera.MV_CC_GetGainMode_NET(ref gm) == 0 && gm.nCurValue != 0)
        {
            var ret = _camera.MV_CC_SetGainMode_NET(0);
            AppLog.Warn($"相机处于自动增益模式（{gm.nCurValue}），已切换为手动增益（Off，错误码 {ret}）");
        }

        // Gamma：用节点访问模式判定是否可写（AM_RW=4 才写，RO=3 只读）
        var accessMode = default(MyCamera.MV_XML_AccessMode);
        if (_camera.MV_XML_GetNodeAccessMode_NET("Gamma", ref accessMode) == 0 && accessMode != MyCamera.MV_XML_AccessMode.AM_RW)
        {
            _gammaSupported = false;
            AppLog.Warn($"相机 Gamma 节点不可写（访问模式 {accessMode}），将跳过 Gamma 设置");
        }
    }

    private void SetFloat(string key, float value)
    {
        var ret = _camera!.MV_CC_SetFloatValue_NET(key, value);
        if (ret != 0)
        {
            AppLog.Warn($"设置 {key} 失败（错误码 {ret}）");
        }
    }

    private void SetInt(string key, int value)
    {
        var ret = _camera!.MV_CC_SetIntValue_NET(key, unchecked((uint)value));
        if (ret != 0)
        {
            AppLog.Warn($"设置 {key} 失败（错误码 {ret}）");
        }
    }

    private void SetBool(string key, bool value, bool throwOnError = false)
    {
        var ret = _camera!.MV_CC_SetBoolValue_NET(key, value);
        if (ret != 0)
        {
            AppLog.Warn($"设置 {key}={value} 失败（错误码 {ret}）");
            if (throwOnError)
            {
                throw new InvalidOperationException($"设置 {key}={value} 失败（错误码 0x{unchecked((uint)ret):X8}）");
            }
        }
    }

    private void SetEnum(string key, uint value, bool throwOnError = false)
    {
        var ret = _camera!.MV_CC_SetEnumValue_NET(key, value);
        if (ret != 0)
        {
            AppLog.Warn($"设置 {key} 失败（错误码 {ret}）");
            if (throwOnError)
            {
                throw new InvalidOperationException($"设置 {key} 失败（错误码 0x{unchecked((uint)ret):X8}）");
            }
        }
    }

    private void SetEnumByString(string key, string value, bool throwOnError = false)
    {
        var ret = _camera!.MV_CC_SetEnumValueByString_NET(key, value);
        if (ret != 0)
        {
            AppLog.Warn($"设置 {key}={value} 失败（错误码 {ret}）");
            if (throwOnError)
            {
                throw new InvalidOperationException($"设置 {key}={value} 失败（错误码 0x{unchecked((uint)ret):X8}）");
            }
        }
    }

    private void EnsureConnectedLocked(string action)
    {
        if (_camera is null || _state != CameraConnectionState.Connected)
        {
            throw new InvalidOperationException($"未连接相机，无法{action}");
        }
    }

    private static bool IsInvertedLevel(IoCommunicationSettings settings) =>
        string.Equals(settings.ActiveLevel, "Low", StringComparison.OrdinalIgnoreCase);

    private static string GetIoOutputLine(IoCommunicationSettings settings) =>
        string.IsNullOrWhiteSpace(settings.NgOutputLine) ? "Line1" : settings.NgOutputLine.Trim();

    private static IoCommunicationSettings CloneIoSettings(IoCommunicationSettings settings) => new()
    {
        Enabled = settings.Enabled,
        TriggerInputLine = settings.TriggerInputLine,
        TriggerEdge = settings.TriggerEdge,
        OutputMode = settings.OutputMode,
        NgOutputLine = settings.NgOutputLine,
        StrobeSource = settings.StrobeSource,
        ActiveLevel = settings.ActiveLevel,
        PulseMs = settings.PulseMs,
    };

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int AddDllDirectory(string lpPathName);

    private sealed record DeviceData(string? UserDefinedName, string Serial, string? ModelName, string? IpAddress);

    private sealed class EnumerateResult
    {
        public List<CameraInfo> Devices { get; } = [];
        public List<string> Failures { get; } = [];
        public int SucceededLayers { get; set; }
    }
}
