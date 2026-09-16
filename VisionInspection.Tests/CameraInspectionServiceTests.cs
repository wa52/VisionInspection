using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionInspection.Camera;
using VisionInspection.Detection;
using VisionInspection.Models;
using VisionInspection.Plc;
using VisionInspection.Production;
using VisionInspection.Trigger;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>
/// 生产触发驱动闭环（假相机全链）：硬触发帧 → 检测 → PLC 状态/结果回传 + CameraIo 发波。
/// 相机为假实现（事件线程同步拷贝语义与真机一致）；PLC 为日志记录版。
/// </summary>
public class CameraInspectionServiceTests
{
    [Fact]
    public async Task Frame_RunsPipeline_SendsPlcMessages_AndIoPulse()
    {
        NodeFactory.Register("FakeNgForProdSvc", (name, _) => new FakeNgNode(name));
        var recipe = new Recipe
        {
            Name = "prod",
            Nodes =
            [
                new RecipeNode { Name = "01 检测", Type = "FakeNgForProdSvc", Enabled = true },
                new RecipeNode
                {
                    Name = "02 条件检测",
                    Type = "Decision",
                    Enabled = true,
                    Rules =
                    [
                        new DecisionRule
                        {
                            MatchMode = "all",
                            Conditions = [new Condition { Node = "01 检测", Field = "decision", Op = "=", Value = "OK" }],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
                new RecipeNode
                {
                    Name = "03 相机IO通信",
                    Type = "CameraIo",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["source"] = "02 条件检测", ["output_when"] = "NG", ["output_line"] = "Line2" },
                },
            ],
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        var plc = new LoggingPlcClient();
        using var camera = new FakeCamera();
        var results = new List<DetectionResult>();
        using var svc = new CameraInspectionService(
            camera,
            plc,
            pipeline,
            () => new TriggerSettings(),
            resultDir: "",
            log: null);
        svc.Result += r => { lock (results) results.Add(r); };
        // 无订阅者时 Preview 的 clone 参数不会被释放：测试订阅并释放，保持 Mat 所有权约定
        svc.Preview += (_, frame, _, images) =>
        {
            frame?.Dispose();
            UiExtensions.ConvertNodeImages(images);
        };

        svc.Arm();
        Assert.Equal(TriggerState.Armed, svc.State);
        Assert.Equal(["READY"], plc.Messages);

        camera.EmitFrame(4, 4); // PLC 触发一次
        await WaitUntilAsync(() => plc.Messages.Count >= 3);

        Assert.Equal(["READY", "BUSY", "NG"], plc.Messages);
        Assert.Equal(1, camera.PulseCalls); // CameraIo 节点按 NG 条件发波一次
        lock (results)
        {
            var result = Assert.Single(results);
            Assert.Equal("NG", result.Decision);
            Assert.Equal(0.42, result.Score);
            Assert.Equal("Line2", camera.PulseLines[0]);
        }
        Assert.Equal(TriggerState.Armed, svc.State); // 处理完回到等待触发

        svc.Disarm();
        Assert.Equal(TriggerState.Idle, svc.State);
        Assert.Equal(["READY", "BUSY", "NG", "IDLE"], plc.Messages);
    }

    [Fact]
    public async Task RapidSecondTrigger_IsNotRejectedByArtificialInterval()
    {
        NodeFactory.Register("FakeNgForRapidTrigger", (name, _) => new FakeNgNode(name));
        var recipe = new Recipe
        {
            Name = "rapid-trigger",
            Nodes = [new RecipeNode { Name = "检测", Type = "FakeNgForRapidTrigger", Enabled = true }],
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        var plc = new LoggingPlcClient();
        using var camera = new FakeCamera();
        using var svc = new CameraInspectionService(
            camera,
            plc,
            pipeline,
            () => new TriggerSettings(),
            resultDir: "",
            log: null);
        svc.Preview += (_, frame, _, images) =>
        {
            frame?.Dispose();
            UiExtensions.ConvertNodeImages(images);
        };

        svc.Arm();
        camera.EmitFrame(4, 4);
        await WaitUntilAsync(() => plc.Messages.Count >= 3);

        // 第二次触发立即到达时，不能被本项目自定义的时间门槛拒绝。
        camera.EmitFrame(4, 4);
        await WaitUntilAsync(() => plc.Messages.Count >= 5);

        Assert.DoesNotContain("TRIGGER_TOO_FAST", plc.Messages);
        Assert.Equal(2, plc.Messages.Count(message => message == "BUSY"));
    }

    [Fact]
    public async Task ConsecutiveFrames_ArrivingWhileBusy_AreProcessedInOrder()
    {
        NodeFactory.Register("FakeSlowForQueuedTriggers", (name, _) => new FakeSlowNode(name));
        FakeSlowNode.Reset();
        var recipe = new Recipe
        {
            Name = "queued-triggers",
            Nodes = [new RecipeNode { Name = "慢检测", Type = "FakeSlowForQueuedTriggers", Enabled = true }],
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        var plc = new LoggingPlcClient();
        using var camera = new FakeCamera();
        var results = new List<DetectionResult>();
        using var svc = new CameraInspectionService(
            camera,
            plc,
            pipeline,
            () => new TriggerSettings(),
            resultDir: "",
            log: null);
        svc.Result += result => { lock (results) results.Add(result); };
        svc.Preview += (_, frame, _, images) =>
        {
            frame?.Dispose();
            UiExtensions.ConvertNodeImages(images);
        };

        svc.Arm();
        camera.EmitFrame(4, 4);
        await WaitUntilAsync(() => FakeSlowNode.HasStarted);

        // 后续帧在第一帧检测尚未完成时到达，应进入 FIFO 而不是被丢弃。
        camera.EmitFrame(4, 4);
        camera.EmitFrame(4, 4);

        await WaitUntilAsync(() => plc.Messages.Count(message => message == "BUSY") == 3);
        await WaitUntilAsync(() =>
        {
            lock (results) return results.Count == 3;
        });

        Assert.Equal(0, svc.OverrunCount);
        Assert.Equal(0, camera.PulseCalls);
        lock (results)
        {
            var sequence = results
                .Select(result => result.NodeDetails["慢检测"]["sequence"])
                .ToArray();
            Assert.Equal(["1", "2", "3"], sequence);
        }
        Assert.Equal(TriggerState.Armed, svc.State);
    }

    [Fact]
    public async Task FrameReceived_ClearsTriggerTimeoutAnchor()
    {
        var recipe = new Recipe
        {
            Name = "trigger-timeout-anchor",
            Nodes = [new RecipeNode { Name = "检测", Type = "Binarize", Enabled = true }],
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        var plc = new LoggingPlcClient();
        using var camera = new FakeCamera();
        using var svc = new CameraInspectionService(
            camera,
            plc,
            pipeline,
            () => new TriggerSettings { GrabTimeoutMs = 1 },
            resultDir: "",
            log: null);
        svc.Preview += (_, frame, _, images) =>
        {
            frame?.Dispose();
            UiExtensions.ConvertNodeImages(images);
        };

        svc.Arm();
        camera.EmitTrigger();
        camera.EmitFrame(4, 4);
        await WaitUntilAsync(() => plc.Messages.Contains("OK"));

        // 后续无触发的轮询不能使用上一帧的 FrameStart 时间误报 GRAB_TIMEOUT。
        camera.EmitTriggerWaitTimeout();

        Assert.Equal(TriggerState.Armed, svc.State);
        Assert.DoesNotContain("GRAB_TIMEOUT", plc.Messages);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件未在超时时间内满足");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>恒 NG 假节点（驱动 Decision→NG→CameraIo 发波验证）。</summary>
    private sealed class FakeNgNode : IModelNode
    {
        public FakeNgNode(string name) => Name = name;
        public string Name { get; set; } = "";
        public string Type => "FakeNgForProdSvc";
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ParamDef> ParamDefs => Array.Empty<ParamDef>();
        public IReadOnlyDictionary<string, string> Params { get; } = new Dictionary<string, string>();
        public void SetParam(string key, string value) { }
        public NodeResult Run(Mat bgr, PipelineRunContext ctx) => new() { Decision = "NG", Values = { ["decision"] = "NG", ["score"] = "0.42" } };
        public void Dispose() { }
    }

    private sealed class FakeSlowNode : IModelNode
    {
        private static int _runCount;

        public FakeSlowNode(string name) => Name = name;
        public static bool HasStarted => Volatile.Read(ref _runCount) > 0;
        public string Name { get; set; } = "";
        public string Type => "FakeSlowForQueuedTriggers";
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ParamDef> ParamDefs => Array.Empty<ParamDef>();
        public IReadOnlyDictionary<string, string> Params { get; } = new Dictionary<string, string>();
        public void SetParam(string key, string value) { }

        public NodeResult Run(Mat bgr, PipelineRunContext ctx)
        {
            if (Interlocked.Increment(ref _runCount) == 1)
            {
                Thread.Sleep(150);
            }

            var sequence = Volatile.Read(ref _runCount).ToString();
            return new NodeResult
            {
                Decision = "OK",
                Values = { ["decision"] = "OK", ["sequence"] = sequence },
            };
        }

        public void Dispose() { }

        public static void Reset() => Interlocked.Exchange(ref _runCount, 0);
    }

    /// <summary>假相机：状态/触发模式可控，EmitFrame 在事件线程内同步投帧（缓冲事件返回后释放，与真机一致）。</summary>
    private sealed class FakeCamera : ICameraController
    {
        public CameraConnectionState State { get; set; } = CameraConnectionState.Connected;
        public bool IsPreviewing { get; set; }
        public CameraParameters Parameters { get; } = new();
        public CameraTriggerMode TriggerMode { get; set; } = CameraTriggerMode.Hardware;
        public CameraFloatRange? GainRange => null;
        public bool GammaSupported => true;
        public double? ResultingFrameRate => null;
        public bool IsHardTriggering { get; private set; }

        private readonly List<string> _pulseLines = [];
        public IReadOnlyList<string> PulseLines => _pulseLines;
        public int PulseCalls => _pulseLines.Count;

        public event EventHandler? TriggerWaitTimeout;
        public event EventHandler? TriggerDetected;
        public event EventHandler? CameraError;
        public event EventHandler<CameraFrameEventArgs>? FrameReceived;

        public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraInfo>>([]);

        public Task ConnectAsync(CameraInfo camera, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartPreviewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopPreviewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyParametersAsync(CameraParameters parameters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetTriggerModeAsync(CameraTriggerMode mode, CancellationToken cancellationToken = default)
        {
            TriggerMode = mode;
            return Task.CompletedTask;
        }
        public Task ApplyTriggerSettingsAsync(TriggerSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfigureIoCommunicationAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PulseNgOutputAsync(IoCommunicationSettings settings, CancellationToken cancellationToken = default)
        {
            _pulseLines.Add(settings.NgOutputLine);
            return Task.CompletedTask;
        }
        public Task SoftTriggerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartHardTriggerAsync(CancellationToken cancellationToken = default)
        {
            IsHardTriggering = true;
            return Task.CompletedTask;
        }
        public Task StopHardTriggerAsync(CancellationToken cancellationToken = default)
        {
            IsHardTriggering = false;
            return Task.CompletedTask;
        }
        public void Dispose() { }

        /// <summary>投一帧 Mono8 图像（缓冲仅事件期间有效，事件返回后立即释放——与控制器真实行为一致）。</summary>
        public void EmitFrame(int width, int height)
        {
            var length = width * height;
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                FrameReceived?.Invoke(this, new CameraFrameEventArgs
                {
                    Frame = new CameraFrame
                    {
                        Width = width,
                        Height = height,
                        PixelFormat = CameraPixelFormat.Mono8,
                        Data = ptr,
                        DataLength = length,
                    },
                });
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void EmitTrigger() => TriggerDetected?.Invoke(this, EventArgs.Empty);

        public void EmitTriggerWaitTimeout() => TriggerWaitTimeout?.Invoke(this, EventArgs.Empty);
    }
}
