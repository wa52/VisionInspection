using System.Text;

namespace VisionInspection.Comm;

/// <summary>
/// 通信链路统一接缝：TCP 客户端 / TCP 服务端 / UDP / 串口。
/// StartAsync 语义随协议不同（拨号连接 / 本地监听 / 打开串口）；生产闭环后续接入仍走 IPlcClient。
/// </summary>
public interface ICommLink : IDisposable
{
    /// <summary>已就绪：TCP客户端已连接 / TCP服务端已监听 / UDP已绑定 / 串口已打开。</summary>
    bool IsReady { get; }

    /// <summary>可发送：TCP服务端=已接受客户端接入，其余协议同 IsReady。</summary>
    bool HasPeer { get; }

    /// <summary>收到一条文本（已去结束符；空结束符按收包块）。链路归创建方独占，直接赋值回调。</summary>
    Action<string>? TextReceived { get; set; }

    /// <summary>链路状态变化（已监听/客户端接入/客户端断开/串口已打开等）。</summary>
    Action<string>? StatusChanged { get; set; }

    /// <summary>链路关闭/中断（主动 Stop 不回调）。</summary>
    Action<string>? LinkClosed { get; set; }

    Task StartAsync(CommDevice device, CancellationToken cancellationToken = default);

    void Stop();

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>设置接收结束符（Start 前调用；转义形式如 \r\n）。</summary>
    void SetTerminator(string escapedTerminator);
}

/// <summary>按设备协议类型创建链路。</summary>
public static class CommLinkFactory
{
    public static ICommLink Create(CommDevice device) => device.Protocol switch
    {
        "TCP服务端" => new TcpServerLink(),
        "UDP" => new UdpLink(),
        "串口" => new SerialLink(),
        _ => new TcpClientLink(),
    };
}

/// <summary>结束符转义（\r\n / \n / \t / \\）与按行累积，四种链路共用。</summary>
public static class CommText
{
    public static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == 'n') { builder.Append('\n'); i++; continue; }
                if (next == 'r') { builder.Append('\r'); i++; continue; }
                if (next == 't') { builder.Append('\t'); i++; continue; }
                if (next == '\\') { builder.Append('\\'); i++; continue; }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}

/// <summary>收包累积器：结束符非空按行切分（同时匹配真实控制符与其字面转义文本），空结束符按收包块透传。</summary>
internal sealed class LineAccumulator
{
    private readonly StringBuilder _pending = new();

    /// <summary>
    /// terminator=真实结束符（如 \r\n 控制字符）；alternate=用户在输入框填的原始文本（如字面 4 字符 "\r\n"），
    /// 对端（如海康 VM 手打 \r\n 发送）常把转义当文本发，二者都按行切开。
    /// </summary>
    public LineAccumulator(string terminator, string alternate)
    {
        Terminator = terminator;
        Alternate = string.Equals(alternate, terminator, StringComparison.Ordinal) ? "" : alternate;
    }

    private string Terminator { get; }

    private string Alternate { get; }

    /// <summary>有未成行的缓冲内容（结束符迟迟不来时供空闲刷新取走）。</summary>
    public bool HasPending => _pending.Length > 0;

    public List<string> Feed(string chunk)
    {
        if (Terminator.Length == 0 && Alternate.Length == 0)
        {
            return [chunk];
        }

        _pending.Append(chunk);
        var lines = new List<string>();
        while (true)
        {
            var joined = _pending.ToString();
            var main = Terminator.Length > 0 ? joined.IndexOf(Terminator, StringComparison.Ordinal) : -1;
            var alt = Alternate.Length > 0 ? joined.IndexOf(Alternate, StringComparison.Ordinal) : -1;
            int index, length;
            if (main >= 0 && (alt < 0 || main <= alt))
            {
                (index, length) = (main, Terminator.Length);
            }
            else if (alt >= 0)
            {
                (index, length) = (alt, Alternate.Length);
            }
            else
            {
                break;
            }

            lines.Add(joined[..index]);
            _pending.Clear();
            _pending.Append(joined[(index + length)..]);
        }

        return lines;
    }

    /// <summary>取走缓冲的未成行内容并清空（空闲刷新用）。</summary>
    public string TakePending()
    {
        var text = _pending.ToString();
        _pending.Clear();
        return text;
    }
}

/// <summary>链路基类：结束符与事件派发共用逻辑。</summary>
public abstract class CommLinkBase : ICommLink
{
    /// <summary>空闲刷新间隔：设置了接收结束符但对端迟迟不发结束符时，把缓冲内容按收包块回调（否则永远收不到）。</summary>
    private const int IdleFlushMs = 500;

    private string _terminator = "";
    private string _terminatorRaw = "";
    private LineAccumulator? _accumulator;
    private System.Threading.Timer? _idleFlushTimer;
    private readonly object _flushSync = new();

    public abstract bool IsReady { get; }

    public virtual bool HasPeer => IsReady;

    public Action<string>? TextReceived { get; set; }

    public Action<string>? StatusChanged { get; set; }

    public Action<string>? LinkClosed { get; set; }

    public abstract Task StartAsync(CommDevice device, CancellationToken cancellationToken = default);

    public abstract void Stop();

    public abstract Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    public void SetTerminator(string escapedTerminator)
    {
        _terminatorRaw = escapedTerminator;
        _terminator = CommText.Unescape(escapedTerminator);
    }

    public virtual void Dispose() => Stop();

    /// <summary>Start 时初始化收包累积器（在 Stop/重连后重置）。</summary>
    internal void InitAccumulator() => _accumulator = new LineAccumulator(_terminator, _terminatorRaw);

    /// <summary>Stop 时清空收包状态（丢弃未成行缓冲，停掉空闲刷新定时器）。</summary>
    protected void ResetReceiveState()
    {
        lock (_flushSync)
        {
            _accumulator = null;
            _idleFlushTimer?.Dispose();
            _idleFlushTimer = null;
        }
    }

    /// <summary>
    /// 收包统一入口：按结束符切行回调；结束符 500ms 内不来时把缓冲内容整块回调。
    /// 链路线程与刷新定时器都经 _flushSync 串行，回调顺序与收包顺序一致。
    /// </summary>
    protected void ReceiveChunk(string chunk)
    {
        lock (_flushSync)
        {
            var accumulator = _accumulator;
            if (accumulator is null)
            {
                return; // Stop 之后迟到的收包，丢弃
            }

            foreach (var line in accumulator.Feed(chunk))
            {
                Emit(line);
            }

            if (accumulator.HasPending)
            {
                if (_idleFlushTimer is null)
                {
                    _idleFlushTimer = new System.Threading.Timer(FlushPending, null, Timeout.Infinite, Timeout.Infinite);
                }

                _idleFlushTimer.Change(IdleFlushMs, Timeout.Infinite);
            }
            else
            {
                _idleFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void FlushPending(object? state)
    {
        lock (_flushSync)
        {
            if (_accumulator is not { HasPending: true })
            {
                return;
            }

            Emit(_accumulator.TakePending());
        }
    }

    protected void Emit(string text)
    {
        if (text.Length > 0)
        {
            TextReceived?.Invoke(text);
        }
    }

    protected void EmitStatus(string text) => StatusChanged?.Invoke(text);

    protected void EmitClosed(string text) => LinkClosed?.Invoke(text);
}
