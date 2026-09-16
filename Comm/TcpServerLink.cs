using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VisionInspection.Comm;

/// <summary>
/// TCP 服务端链路：绑定本地端口等待 PLC 接入（常见：PLC 作客户端主动连视觉机）。
/// 同一时刻服务一个客户端；客户端断开后自动回到监听等待下一次接入。
/// </summary>
public sealed class TcpServerLink : CommLinkBase
{
    private readonly object _sync = new();
    private TcpListener? _listener;
    private TcpClient? _peer;
    private CancellationTokenSource? _cts;
    private int _listenPort;

    public override bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _listener is not null;
            }
        }
    }

    public override bool HasPeer
    {
        get
        {
            lock (_sync)
            {
                return _peer is { Connected: true };
            }
        }
    }

    public override async Task StartAsync(CommDevice device, CancellationToken cancellationToken = default)
    {
        Stop();
        InitAccumulator();
        _listenPort = Math.Max(1, device.Port);
        var listener = new TcpListener(IPAddress.Any, _listenPort);
        listener.Start();
        lock (_sync)
        {
            _listener = listener;
            _cts = new CancellationTokenSource();
        }

        EmitStatus($"监听中 0.0.0.0:{_listenPort}（0.0.0.0=本机全部网卡，对端连本机任意 IP 的 {_listenPort} 端口均可接入），等待客户端接入… 本机 IPv4: {LocalIpv4Summary()}");
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts!.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    /// <summary>本机可用 IPv4（联调提示：告诉对端可以连哪个 IP；拿不到时至少回环可用）。</summary>
    private static string LocalIpv4Summary()
    {
        try
        {
            var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
            return addresses.Count > 0 ? string.Join(" / ", addresses) : "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient peer;
            try
            {
                peer = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 客户端在握手期 reset（如 PLC 上电/反复重启）只影响该次接入，不得杀死监听
                if (!token.IsCancellationRequested && _listener is not null)
                {
                    EmitStatus($"接入异常，继续监听: {ex.Message}");
                    try
                    {
                        await Task.Delay(200, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                return;
            }

            var remote = peer.Client.RemoteEndPoint?.ToString() ?? "未知客户端";
            lock (_sync)
            {
                _peer = peer;
            }

            EmitStatus($"已接受客户端接入 {remote}");
            try
            {
                await ReceiveAsync(peer, token);
            }
            catch (OperationCanceledException)
            {
                peer.Close();
                return;
            }
            catch (Exception)
            {
                // 读取异常按对端断开处理
            }

            lock (_sync)
            {
                if (ReferenceEquals(_peer, peer))
                {
                    _peer = null;
                }
            }

            peer.Close();
            if (!token.IsCancellationRequested && _listener is not null)
            {
                EmitStatus("客户端已断开，继续等待接入…");
            }
        }
    }

    private async Task ReceiveAsync(TcpClient peer, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var stream = peer.GetStream();
        while (!token.IsCancellationRequested && peer.Connected)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0)
            {
                return;
            }

            ReceiveChunk(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    public override void Stop()
    {
        TcpListener? listener;
        TcpClient? peer;
        lock (_sync)
        {
            listener = _listener;
            _listener = null;
            peer = _peer;
            _peer = null;
            _cts?.Cancel();
            _cts = null;
        }

        ResetReceiveState();
        peer?.Close();
        listener?.Stop();
    }

    public override async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        TcpClient? peer;
        lock (_sync)
        {
            peer = _peer;
        }

        if (peer is not { Connected: true })
        {
            throw new InvalidOperationException("TCP服务端尚未接受客户端接入，无法发送");
        }

        var data = Encoding.UTF8.GetBytes(text);
        await peer.GetStream().WriteAsync(data, cancellationToken);
    }
}
