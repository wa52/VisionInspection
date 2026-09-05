using System.Text;

namespace SpeakerVisionInspection.Comm;

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

/// <summary>收包累积器：结束符非空按行切分，空结束符按收包块透传。</summary>
internal sealed class LineAccumulator
{
    private readonly StringBuilder _pending = new();

    public LineAccumulator(string terminator) => Terminator = terminator;

    private string Terminator { get; }

    public List<string> Feed(string chunk)
    {
        if (Terminator.Length == 0)
        {
            return [chunk];
        }

        _pending.Append(chunk);
        var lines = new List<string>();
        while (true)
        {
            var joined = _pending.ToString();
            var index = joined.IndexOf(Terminator, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            lines.Add(joined[..index]);
            _pending.Clear();
            _pending.Append(joined[(index + Terminator.Length)..]);
        }

        return lines;
    }
}

/// <summary>链路基类：结束符与事件派发共用逻辑。</summary>
public abstract class CommLinkBase : ICommLink
{
    private string _terminator = "";

    public abstract bool IsReady { get; }

    public virtual bool HasPeer => IsReady;

    public Action<string>? TextReceived { get; set; }

    public Action<string>? StatusChanged { get; set; }

    public Action<string>? LinkClosed { get; set; }

    public abstract Task StartAsync(CommDevice device, CancellationToken cancellationToken = default);

    public abstract void Stop();

    public abstract Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    public void SetTerminator(string escapedTerminator) => _terminator = CommText.Unescape(escapedTerminator);

    public virtual void Dispose() => Stop();

    internal LineAccumulator CreateAccumulator() => new(_terminator);

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
