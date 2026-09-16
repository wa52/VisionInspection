using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionInspection.Camera;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>
/// CameraFrameGrabber.Grab 分支语义（回归）：
/// - 连续模式未预览 → 临时开采集等第一帧（Start/StopPreview），不切软触发、不写触发寄存器；
/// - 软触发模式未预览 → 软触发；
/// - 预览中 → 只等帧；硬触发/未连接 → null；无帧超时 → null 且仍停采集。
/// 背景：曾用「临时切软触发」实现连续模式抓帧——Start/StopGrabbing + 触发寄存器频繁翻面导致取不到帧，
/// 且 SoftTriggerAsync 持锁等帧阻塞 UI 状态读取（重复点击卡死）。
/// </summary>
public class CameraFrameGrabberTests
{
    /// <summary>最小 Fake：状态可配，动作可计数，FrameReceived 首次订阅时可自动发一帧。</summary>
    private sealed class FakeCamera : ICameraController
    {
        public CameraConnectionState State { get; set; } = CameraConnectionState.Connected;
        public bool IsPreviewing { get; set; }
        public bool IsHardTriggering { get; set; }
        public CameraTriggerMode TriggerMode { get; set; } = CameraTriggerMode.Continuous;
        public bool FireOnSubscribe { get; set; } = true;

        public int StartPreviewCalls { get; private set; }
        public int StopPreviewCalls { get; private set; }
        public int SoftTriggerCalls { get; private set; }
        public int SetTriggerModeCalls { get; private set; }

        public CameraParameters Parameters => new();
        public CameraFloatRange? GainRange => null;
        public bool GammaSupported => true;
        public double? ResultingFrameRate => null;

        public event EventHandler? TriggerWaitTimeout;
        public event EventHandler? TriggerDetected;
        public event EventHandler? CameraError;

        private EventHandler<CameraFrameEventArgs>? _received;

        public event EventHandler<CameraFrameEventArgs> FrameReceived
        {
            add { _received += value; if (FireOnSubscribe) RaiseFrame(); }
            remove { _received -= value; }
        }

        /// <summary>构造一帧 Mono8 4×3 灰图并同步投递（handler 拷贝后释放缓冲，与控制器所有权约定一致）。</summary>
        public void RaiseFrame()
        {
            if (_received is null)
            {
                return;
            }

            var data = Marshal.AllocHGlobal(12);
            try
            {
                var pixels = new byte[12];
                for (var i = 0; i < 12; i++)
                {
                    pixels[i] = (byte)(i * 7);
                }

                Marshal.Copy(pixels, 0, data, 12);
                var frame = new CameraFrame { Width = 4, Height = 3, PixelFormat = CameraPixelFormat.Mono8, Data = data, DataLength = 12 };
                _received(this, new CameraFrameEventArgs { Frame = frame });
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        public Task StartPreviewAsync(CancellationToken cancellationToken = default)
        {
            StartPreviewCalls++;
            IsPreviewing = true;
            return Task.CompletedTask;
        }

        public Task StopPreviewAsync(CancellationToken cancellationToken = default)
        {
            StopPreviewCalls++;
            IsPreviewing = false;
            return Task.CompletedTask;
        }

        public Task SoftTriggerAsync(CancellationToken cancellationToken = default)
        {
            SoftTriggerCalls++;
            RaiseFrame();
            return Task.CompletedTask;
        }

        public Task SetTriggerModeAsync(CameraTriggerMode mode, CancellationToken cancellationToken = default)
        {
            SetTriggerModeCalls++;
            TriggerMode = mode;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConnectAsync(CameraInfo camera, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyParametersAsync(CameraParameters parameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyTriggerSettingsAsync(TriggerSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfigureIoCommunicationAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PulseNgOutputAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartHardTriggerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopHardTriggerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public void Grab_ContinuousMode_TemporarilyStartsAcquisition_NoSoftTriggerNoModeSwitch()
    {
        var cam = new FakeCamera { TriggerMode = CameraTriggerMode.Continuous, IsPreviewing = false };
        var grabber = new CameraFrameGrabber(cam);

        using var mat = grabber.Grab(3000);

        Assert.NotNull(mat);
        Assert.Equal(4, mat!.Width);
        Assert.Equal(1, cam.StartPreviewCalls); // 临时开采集
        Assert.Equal(1, cam.StopPreviewCalls);  // 用完即停
        Assert.Equal(0, cam.SoftTriggerCalls);  // 不走软触发
        Assert.Equal(0, cam.SetTriggerModeCalls); // 不切模式（不再翻面触发寄存器）
        Assert.False(cam.IsPreviewing);
    }

    [Fact]
    public void Grab_SoftwareMode_UsesSoftTrigger()
    {
        var cam = new FakeCamera { TriggerMode = CameraTriggerMode.Software, IsPreviewing = false };
        var grabber = new CameraFrameGrabber(cam);

        using var mat = grabber.Grab(3000);

        Assert.NotNull(mat);
        Assert.Equal(1, cam.SoftTriggerCalls);
        Assert.Equal(0, cam.StartPreviewCalls);
        Assert.Equal(0, cam.StopPreviewCalls);
    }

    [Fact]
    public void Grab_Previewing_WaitsFrameWithoutTouchingAcquisition()
    {
        var cam = new FakeCamera { TriggerMode = CameraTriggerMode.Continuous, IsPreviewing = true };
        var grabber = new CameraFrameGrabber(cam);

        using var mat = grabber.Grab(3000);

        Assert.NotNull(mat);
        Assert.Equal(0, cam.StartPreviewCalls);
        Assert.Equal(0, cam.StopPreviewCalls);
        Assert.Equal(0, cam.SoftTriggerCalls);
    }

    [Fact]
    public void Grab_HardwareMode_ReturnsNull()
    {
        var cam = new FakeCamera { TriggerMode = CameraTriggerMode.Hardware, IsHardTriggering = false };
        var grabber = new CameraFrameGrabber(cam);

        using var mat = grabber.Grab(500);

        Assert.Null(mat);
        Assert.Equal(0, cam.StartPreviewCalls);
        Assert.Equal(0, cam.SoftTriggerCalls);
    }

    [Fact]
    public void Grab_Disconnected_ReturnsNull()
    {
        var cam = new FakeCamera { State = CameraConnectionState.Disconnected };
        var grabber = new CameraFrameGrabber(cam);

        using var mat = grabber.Grab(500);

        Assert.Null(mat);
        Assert.Equal(0, cam.StartPreviewCalls);
    }

    [Fact]
    public void Grab_TimeoutWhenNoFrame_StopsAcquisitionAndReturnsNull()
    {
        var cam = new FakeCamera { TriggerMode = CameraTriggerMode.Continuous, IsPreviewing = false, FireOnSubscribe = false };
        var grabber = new CameraFrameGrabber(cam);

        using var mat = grabber.Grab(300);

        Assert.Null(mat);
        Assert.Equal(1, cam.StopPreviewCalls); // 超时也要停采集，不留自由采集在跑
        Assert.False(cam.IsPreviewing);
    }
}
