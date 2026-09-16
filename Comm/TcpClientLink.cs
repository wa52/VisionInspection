using System.Net.Sockets;
using System.Text;

namespace VisionInspection.Comm;

/// <summary>
/// TCP 客户端链路：主动连接 目标IP:端口；掉线后按 AutoReconnect 每 3s 重试，Stop 终止。
/// </summary>
public sealed class TcpClientLink : CommLinkBase
{
    private readonly object _sync = new();
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private CommDevice _device = new();

    public override bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _client is { Connected: true };
            }
        }
    }

    public override async Task StartAsync(CommDevice device, CancellationToken cancellationToken = default)
    {
        Stop();
        _device = device with { };
        InitAccumulator();
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _cts = cts;
        }

        _ = Task.Run(() => RunLoopAsync(cts.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(_device.Host, Math.Max(1, _device.Port), timeoutCts.Token);
                lock (_sync)
                {
                    _client = client;
                }

                EmitStatus($"已连接 {_device.Host}:{_device.Port}");
                await ReceiveAsync(client, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitStatus($"连接失败: {ex.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                    }
                }

                client?.Dispose();
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (!_device.AutoReconnect)
            {
                EmitClosed("连接已断开（未启用自动重连）");
                break;
            }

            EmitClosed("连接已断开，3 秒后重试…");
            try
            {
                await Task.Delay(3000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveAsync(TcpClient client, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var stream = client.GetStream();
        while (!token.IsCancellationRequested && client.Connected)
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
        TcpClient? client;
        lock (_sync)
        {
            client = _client;
            _client = null;
            _cts?.Cancel();
            _cts = null;
        }

        ResetReceiveState();
        client?.Close();
    }

    public override async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        TcpClient? client;
        lock (_sync)
        {
            client = _client;
        }

        if (client is not { Connected: true })
        {
            throw new InvalidOperationException("TCP客户端未连接");
        }

        var data = Encoding.UTF8.GetBytes(text);
        await client.GetStream().WriteAsync(data, cancellationToken);
    }
}
