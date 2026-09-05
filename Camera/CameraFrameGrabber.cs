using System.Runtime.InteropServices;
using OpenCvSharp;
using SpeakerVisionInspection.Production;

namespace SpeakerVisionInspection.Camera;

/// <summary>
/// 同步取一帧相机图像（供图像源节点在单次/连续执行时抓帧）。
/// - 预览中：等待下一帧 FrameReceived（不动相机状态）；
/// - 未预览 + 连续模式：临时开始采集（StartPreview=自由采集）等第一帧后停止——
///   不切软触发、不写触发寄存器（频繁 Start/StopGrabbing + TriggerMode/Source 翻面会导致取帧失败/卡顿）；
/// - 未预览 + 软触发模式：直接软触发取一帧；
/// - 硬触发模式 / 硬触发采集中：返回 null（不干扰生产采集链路）。
/// 帧缓冲在事件返回后由控制器释放 → handler 内同步 Marshal.Copy 成字节数组再跨线程转换。
/// </summary>
public sealed class CameraFrameGrabber
{
    private readonly ICameraController _camera;

    public CameraFrameGrabber(ICameraController camera) => _camera = camera;

    /// <summary>阻塞取一帧 BGR Mat；超时/未连接/硬触发中返回 null。调用方负责 Dispose 返回的 Mat。</summary>
    public Mat? Grab(int timeoutMs, Action<string>? log = null)
    {
        if (_camera.State != CameraConnectionState.Connected)
        {
            log?.Invoke("[取帧] 相机未连接");
            return null;
        }

        if (_camera.TriggerMode == CameraTriggerMode.Hardware || _camera.IsHardTriggering)
        {
            log?.Invoke("[取帧] 硬触发模式下不抓帧（会干扰生产采集），请改用连续/软触发或预览");
            return null;
        }

        var tcs = new TaskCompletionSource<FrameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CameraFrameEventArgs e)
        {
            var f = e.Frame;
            var data = new byte[f.DataLength];
            Marshal.Copy(f.Data, data, 0, f.DataLength);
            tcs.TrySetResult(new FrameSnapshot(data, f.Width, f.Height, f.PixelFormat));
        }

        var stopPreview = false;
        _camera.FrameReceived += Handler;
        try
        {
            var wasPreviewing = _camera.IsPreviewing;
            if (!wasPreviewing && _camera.TriggerMode == CameraTriggerMode.Continuous)
            {
                // 连续模式 = 自由采集：开始采集后帧持续到达，等第一帧即可（用完即停）
                _camera.StartPreviewAsync().GetAwaiter().GetResult();
                stopPreview = true;
            }
            else if (!wasPreviewing)
            {
                // 软触发模式：主动触发一帧
                _camera.SoftTriggerAsync().GetAwaiter().GetResult();
            }

            var completed = Task.WaitAny(tcs.Task, Task.Delay(Math.Max(100, timeoutMs)));
            if (completed != 0 || !tcs.Task.IsCompletedSuccessfully)
            {
                log?.Invoke($"[取帧] 等待相机帧超时（{timeoutMs}ms）");
                return null;
            }

            var snapshot = tcs.Task.Result;
            return CameraFrameMatConverter.ToBgrMat(snapshot.Data, snapshot.Width, snapshot.Height, snapshot.PixelFormat);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[取帧] 取帧失败: {ex.Message}");
            return null;
        }
        finally
        {
            _camera.FrameReceived -= Handler;
            if (stopPreview)
            {
                try
                {
                    _camera.StopPreviewAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[取帧] 停止采集失败: {ex.Message}");
                }
            }
        }
    }
}
