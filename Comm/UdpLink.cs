using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SpeakerVisionInspection.Comm;

/// <summary>
/// UDP 链路：绑定本地端口收包；发送到 目标IP:端口（目标IP 为空时回发最近来包的对端）。
/// </summary>
public sealed class UdpLink : CommLinkBase
{
    private readonly object _sync = new();
    private UdpClient? _udp;
    private IPEndPoint? _remote;
    private CancellationTokenSource? _cts;
    private CommDevice _device = new();
    private LineAccumulator? _accumulator;

    public override bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _udp is not null;
            }
        }
    }

    public override async Task StartAsync(CommDevice device, CancellationToken cancellationToken = default)
    {
        Stop();
        _device = device with { };
        _accumulator = CreateAccumulator();
        var udp = new UdpClient(Math.Max(1, device.Port));
        _remote = string.IsNullOrWhiteSpace(_device.Host)
            ? null
            : new IPEndPoint(IPAddress.Parse(_device.Host.Trim()), Math.Max(1, _device.Port));
        lock (_sync)
        {
            _udp = udp;
            _cts = new CancellationTokenSource();
        }

        EmitStatus($"UDP 已绑定本地端口 {device.Port}，目标 {(_remote?.ToString() ?? "最近来包对端")}");
        _ = Task.Run(() => ReceiveLoopAsync(udp, _cts!.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(token);
                lock (_sync)
                {
                    _remote ??= result.RemoteEndPoint;
                }

                foreach (var line in _accumulator!.Feed(Encoding.UTF8.GetString(result.Buffer)))
                {
                    Emit(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    EmitClosed($"UDP 收包异常: {ex.Message}");
                }

                return;
            }
        }
    }

    public override void Stop()
    {
        UdpClient? udp;
        lock (_sync)
        {
            udp = _udp;
            _udp = null;
            _cts?.Cancel();
            _cts = null;
        }

        udp?.Dispose();
    }

    public override async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        UdpClient? udp;
        IPEndPoint? remote;
        lock (_sync)
        {
            udp = _udp;
            remote = _remote;
        }

        if (udp is null)
        {
            throw new InvalidOperationException("UDP 未启动");
        }

        if (remote is null)
        {
            throw new InvalidOperationException("UDP 尚无目标地址（请填写目标IP 或先接收一包）");
        }

        var data = Encoding.UTF8.GetBytes(text);
        await udp.SendAsync(data, remote);
    }
}
