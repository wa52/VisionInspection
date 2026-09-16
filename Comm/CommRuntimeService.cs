namespace VisionInspection.Comm;

/// <summary>发送结果：Ok=已发出；失败时 Error 给可操作提示。</summary>
public readonly record struct CommSendOutcome(bool Ok, string? Error)
{
    public static CommSendOutcome Fail(string error) => new(false, error);
    public static readonly CommSendOutcome Success = new(true, null);
}

/// <summary>接收结果：Ok=true 且 TimedOut=false → Text 有效；Ok=true 且 TimedOut=true → 超时无数据；Ok=false → Error 给可操作提示。</summary>
public readonly record struct CommReceiveOutcome(bool Ok, string Text, bool TimedOut, string? Error)
{
    public static CommReceiveOutcome Got(string text) => new(true, text, false, null);
    public static readonly CommReceiveOutcome Timeout = new(true, "", true, null);
    public static CommReceiveOutcome Fail(string error) => new(false, "", false, error);
}

/// <summary>
/// 方案级通信运行时接缝：发送数据/接收数据节点经 PipelineRunContext 拿到它收发文本。
/// 与通信管理弹窗（纯联调工具，链路归弹窗所有）分离；IsHeld 供弹窗做互斥守卫。
/// </summary>
public interface ICommRuntime
{
    /// <summary>按设备名发送文本（设备配置取自 comm.json；链路未就绪返回失败）。</summary>
    CommSendOutcome Send(string deviceName, string text);

    /// <summary>按设备名取一条收到的文本；无积压时阻塞等待至 timeoutMs（毫秒，0=只取积压）。</summary>
    CommReceiveOutcome Receive(string deviceName, int timeoutMs);

    /// <summary>该设备是否已被运行时持有（打开过链路）——通信管理弹窗连接前用它拦截重复占用。</summary>
    bool IsHeld(string deviceName);
}

/// <summary>
/// 生产通信运行时：按设备名惰性建链（设备配置每次建链时从 comm.json 现读，弹窗改参数后即时生效），
/// 收到的文本进每设备有界 FIFO 队列（容量 64，满丢最旧）；链路断开后丢弃该链路，下次收发自动重建。
/// 链路工厂可注入（单测用假链路，离线验证）。
/// </summary>
public sealed class CommRuntimeService : ICommRuntime, IDisposable
{
    /// <summary>每设备收包队列容量：满后丢最旧（防 PLC 刷屏把内存吃爆），溢出记日志。</summary>
    public const int QueueCapacity = 64;

    private readonly CommDeviceStore _store;
    private readonly Func<CommDevice, ICommLink> _linkFactory;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, RuntimeLink> _links = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private sealed class RuntimeLink
    {
        public required ICommLink Link { get; init; }
        public required CommReceiveQueue Queue { get; init; }
    }

    public CommRuntimeService(
        CommDeviceStore store,
        Action<string>? log = null,
        Func<CommDevice, ICommLink>? linkFactory = null)
    {
        _store = store;
        _log = log ?? (_ => { });
        _linkFactory = linkFactory ?? CommLinkFactory.Create;
    }

    public CommSendOutcome Send(string deviceName, string text)
    {
        try
        {
            var link = EnsureLink(deviceName, out var deviceError);
            if (link == null)
            {
                return CommSendOutcome.Fail(deviceError!);
            }

            if (!link.Link.HasPeer)
            {
                return CommSendOutcome.Fail(NotReadyMessage(deviceName));
            }

            link.Link.SendTextAsync(text).GetAwaiter().GetResult();
            return CommSendOutcome.Success;
        }
        catch (Exception ex)
        {
            return CommSendOutcome.Fail($"发送失败: {ex.Message}（请检查设备「{deviceName}」的连接状态与参数）");
        }
    }

    public CommReceiveOutcome Receive(string deviceName, int timeoutMs)
    {
        try
        {
            var link = EnsureLink(deviceName, out var deviceError);
            if (link == null)
            {
                return CommReceiveOutcome.Fail(deviceError!);
            }

            if (!link.Link.IsReady)
            {
                return CommReceiveOutcome.Fail(NotReadyMessage(deviceName));
            }

            return link.Queue.TryTake(Math.Max(0, timeoutMs), out var text)
                ? CommReceiveOutcome.Got(text)
                : CommReceiveOutcome.Timeout;
        }
        catch (Exception ex)
        {
            return CommReceiveOutcome.Fail($"接收失败: {ex.Message}（请检查设备「{deviceName}」的连接状态与参数）");
        }
    }

    public bool IsHeld(string deviceName)
    {
        lock (_sync)
        {
            return _links.ContainsKey(deviceName);
        }
    }

    /// <summary>按设备名取链路（无则建）；设备不存在/启动失败返回 null + 错误信息。</summary>
    private RuntimeLink? EnsureLink(string deviceName, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            error = "未选择通信设备（请在节点参数里选择与「通信管理」一致的设备名）";
            return null;
        }

        lock (_sync)
        {
            if (_links.TryGetValue(deviceName, out var existing))
            {
                error = null;
                return existing;
            }

            var device = _store.Load().FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            if (device == null)
            {
                error = $"通信管理中不存在设备「{deviceName}」（请先在工具栏「通信管理」添加并保存该设备）";
                return null;
            }

            var queue = new CommReceiveQueue(QueueCapacity);
            ICommLink? link = null;
            try
            {
                link = _linkFactory(device);
                link.TextReceived = queue.Enqueue;
                link.StatusChanged = msg => _log($"[通信运行时] {deviceName}: {msg}");
                link.LinkClosed = _ =>
                {
                    // 断链丢弃（含积压数据），下次收发自动重建链路；只移除自己这个实例（防误删新建链路）
                    lock (_sync)
                    {
                        if (_links.TryGetValue(deviceName, out var held) && ReferenceEquals(held.Link, link))
                        {
                            _links.Remove(deviceName);
                        }
                    }
                    _log($"[通信运行时] {deviceName}: 链路已断开，下次执行时自动重建");
                };
                link.SetTerminator(device.Terminator);
                link.StartAsync(device).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                try { link?.Dispose(); } catch { /* 建链失败清理，忽略二次异常 */ }
                error = $"无法连接设备「{deviceName}」: {ex.Message}（设备可能已被「通信管理」弹窗占用，或 IP/端口/串口参数有误）";
                return null;
            }

            var runtimeLink = new RuntimeLink { Link = link, Queue = queue };
            _links[deviceName] = runtimeLink;
            _log($"[通信运行时] {deviceName}: 链路已建立（{device.Protocol}）");
            error = null;
            return runtimeLink;
        }
    }

    private static string NotReadyMessage(string deviceName) =>
        $"设备「{deviceName}」链路未就绪（TCP客户端未连上/服务端无客户端接入/串口已断开），请检查对端设备";

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var link in _links.Values)
            {
                try { link.Link.Dispose(); } catch { /* 退出清理，忽略单个链路释放异常 */ }
            }
            _links.Clear();
        }
    }
}

/// <summary>
/// 有界 FIFO 收包队列：满后丢最旧（溢出计数记日志用）。
/// Monitor 等待实现阻塞取（流水线节点在后台线程同步调用，50ms 内无需自旋）。
/// </summary>
public sealed class CommReceiveQueue
{
    private readonly object _sync = new();
    private readonly Queue<string> _items = new();
    private readonly int _capacity;

    /// <summary>累计丢最旧条数（诊断用）。</summary>
    public int DroppedCount { get; private set; }

    public CommReceiveQueue(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Enqueue(string text)
    {
        lock (_sync)
        {
            if (_items.Count >= _capacity)
            {
                _items.Dequeue();
                DroppedCount++;
            }
            _items.Enqueue(text);
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>阻塞取一条；timeoutMs 毫秒内无数据返回 false（0=只取积压不等待）。</summary>
    public bool TryTake(int timeoutMs, out string text)
    {
        lock (_sync)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (_items.Count == 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    text = "";
                    return false;
                }
                Monitor.Wait(_sync, (int)Math.Min(remaining, int.MaxValue));
            }
            text = _items.Dequeue();
            return true;
        }
    }
}
