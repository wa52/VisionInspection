using System.IO.Ports;
using System.Text;

namespace SpeakerVisionInspection.Comm;

/// <summary>
/// 串口链路（RS232/485）：按 串口名/波特率/数据位/校验位/停止位 打开，后台读循环收包。
/// </summary>
public sealed class SerialLink : CommLinkBase
{
    private readonly object _sync = new();
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private LineAccumulator? _accumulator;

    public override bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _port is { IsOpen: true };
            }
        }
    }

    public override async Task StartAsync(CommDevice device, CancellationToken cancellationToken = default)
    {
        Stop();
        _accumulator = CreateAccumulator();
        var port = new SerialPort(device.SerialPortName, device.BaudRate, ParseParity(device.Parity), Math.Clamp(device.DataBits, 5, 8), ParseStopBits(device.StopBits))
        {
            ReadTimeout = 200,
            WriteTimeout = 1000,
        };
        port.Open();
        lock (_sync)
        {
            _port = port;
            _cts = new CancellationTokenSource();
        }

        var parityText = device.Parity?.Trim() switch
        {
            "Even" => "偶校验",
            "Odd" => "奇校验",
            _ => "无校验",
        };
        var stopText = string.Equals(device.StopBits, "Two", StringComparison.OrdinalIgnoreCase) ? "2 位" : "1 位";
        EmitStatus($"串口 {port.PortName} 已打开（{port.BaudRate} 波特, {port.DataBits} 数据位, {parityText}, {stopText}）");
        _ = Task.Run(() => ReceiveLoopAsync(port, _cts!.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(SerialPort port, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested && port.IsOpen)
            {
                var read = 0;
                try
                {
                    read = await port.BaseStream.ReadAsync(buffer, token);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (read == 0)
                {
                    continue;
                }

                foreach (var line in _accumulator!.Feed(Encoding.UTF8.GetString(buffer, 0, read)))
                {
                    Emit(line);
                }
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
                EmitClosed($"串口读取异常: {ex.Message}");
            }
        }
    }

    public override void Stop()
    {
        SerialPort? port;
        lock (_sync)
        {
            port = _port;
            _port = null;
            _cts?.Cancel();
            _cts = null;
        }

        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch
        {
            // 关闭失败无需处理：串口即将废弃
        }

        port.Dispose();
    }

    public override async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        SerialPort? port;
        lock (_sync)
        {
            port = _port;
        }

        if (port is not { IsOpen: true })
        {
            throw new InvalidOperationException("串口未打开");
        }

        await port.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await port.BaseStream.FlushAsync(cancellationToken);
    }

    internal static Parity ParseParity(string value) => value?.ToLowerInvariant() switch
    {
        "even" => Parity.Even,
        "odd" => Parity.Odd,
        _ => Parity.None,
    };

    internal static StopBits ParseStopBits(string value) => value?.ToLowerInvariant() switch
    {
        "two" => StopBits.Two,
        _ => StopBits.One,
    };
}
