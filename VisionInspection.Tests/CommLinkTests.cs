using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionInspection.Comm;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>通信链路测试（TCP/UDP 用真实 loopback socket；串口仅覆盖纯逻辑解析，真实口待现场/虚拟口验证）。</summary>
public class CommLinkTests
{
    [Fact]
    public void SerialLink_ParsesParityAndStopBits()
    {
        Assert.Equal(Parity.None, SerialLink.ParseParity("None"));
        Assert.Equal(Parity.Even, SerialLink.ParseParity("Even"));
        Assert.Equal(Parity.Odd, SerialLink.ParseParity("odd"));
        Assert.Equal(Parity.None, SerialLink.ParseParity("垃圾"));
        Assert.Equal(Parity.None, SerialLink.ParseParity(null));
        Assert.Equal(StopBits.One, SerialLink.ParseStopBits("One"));
        Assert.Equal(StopBits.Two, SerialLink.ParseStopBits("TWO"));
        Assert.Equal(StopBits.One, SerialLink.ParseStopBits(""));
        Assert.Equal(StopBits.One, SerialLink.ParseStopBits(null));
    }
    private static int AllocateTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int AllocateUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时");
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task TcpServer_AcceptsClient_ReceivesTerminatedLines_SendsBack()
    {
        var port = AllocateTcpPort();
        using var server = new TcpServerLink();
        var received = new List<string>();
        server.TextReceived += text => { lock (received) received.Add(text); };
        server.SetTerminator("\\r\\n");
        await server.StartAsync(new CommDevice { Name = "srv", Protocol = "TCP服务端", Port = port });

        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        var stream = peer.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("PING1\r\nPING2\r\n"));

        await WaitUntilAsync(() => { lock (received) return received.Count >= 2; });
        lock (received)
        {
            Assert.Equal(["PING1", "PING2"], received);
        }

        Assert.True(server.HasPeer);
        await server.SendTextAsync("ACK");
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);
        Assert.Equal("ACK", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task TcpClient_Connects_Sends_ReceivesEchoLine()
    {
        var port = AllocateTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var echoTask = EchoOnceAsync(listener);

        using var link = new TcpClientLink();
        var received = new List<string>();
        link.TextReceived += text => { lock (received) received.Add(text); };
        link.SetTerminator("\\r\\n");
        await link.StartAsync(new CommDevice { Name = "cli", Protocol = "TCP客户端", Host = "127.0.0.1", Port = port });

        await WaitUntilAsync(() => link.IsReady);
        await link.SendTextAsync("HELLO");
        await echoTask;

        await WaitUntilAsync(() => { lock (received) return received.Count >= 1; });
        lock (received)
        {
            Assert.Equal(["ECHO:HELLO"], received);
        }
    }

    [Fact]
    public async Task Udp_ReceivesChunk_Then_SendsBackToPeer()
    {
        var port = AllocateUdpPort();
        using var link = new UdpLink();
        var received = new List<string>();
        link.TextReceived += text => { lock (received) received.Add(text); };
        await link.StartAsync(new CommDevice { Name = "udp", Protocol = "UDP", Host = "", Port = port });

        using var peer = new UdpClient();
        await peer.SendAsync(Encoding.UTF8.GetBytes("DATUM"), new IPEndPoint(IPAddress.Loopback, port));
        await WaitUntilAsync(() => { lock (received) return received.Count >= 1; });
        lock (received)
        {
            Assert.Equal(["DATUM"], received);
        }

        await link.SendTextAsync("ACK");
        var result = await peer.ReceiveAsync();
        Assert.Equal("ACK", Encoding.UTF8.GetString(result.Buffer));
    }

    [Fact]
    public void CommDevice_NameOrProtocolChange_RaisesPropertyChanged()
    {
        var device = new CommDevice { Name = "设备1" };
        var raised = new List<string>();
        device.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        device.Name = "PLC_A";
        Assert.Equal(["Name", "Display"], raised);
        raised.Clear();
        device.Protocol = "TCP服务端";
        Assert.Equal(["Protocol", "Display"], raised);
        Assert.Equal("PLC_A（TCP服务端）", device.Display);
    }

    [Fact]
    public async Task TcpClient_ReceivesDataWithoutTerminator_AfterIdleFlush()
    {
        // 用户场景：对端（如海康 VM 发送数据）不按约定带接收结束符 → 数据按收包块照常显示，不再永远缓冲
        var port = AllocateTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("NO-TERM-DATA"));
        });

        using var link = new TcpClientLink();
        var received = new List<string>();
        link.TextReceived += text => { lock (received) received.Add(text); };
        link.SetTerminator("\\r\\n");
        await link.StartAsync(new CommDevice { Name = "cli", Protocol = "TCP客户端", Host = "127.0.0.1", Port = port });

        await WaitUntilAsync(() => { lock (received) return received.Count >= 1; }, 5000);
        lock (received)
        {
            Assert.Equal(["NO-TERM-DATA"], received);
        }

        listener.Stop();
    }

    [Fact]
    public async Task TcpClient_IdleFlushKeepsReceiveOrder()
    {
        // 先到的不带结束符内容空闲刷新显示，后到的成行内容仍按序回调
        var port = AllocateTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("PART"));
            await Task.Delay(700);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("REST\r\n"));
        });

        using var link = new TcpClientLink();
        var received = new List<string>();
        link.TextReceived += text => { lock (received) received.Add(text); };
        link.SetTerminator("\\r\\n");
        await link.StartAsync(new CommDevice { Name = "cli", Protocol = "TCP客户端", Host = "127.0.0.1", Port = port });

        await WaitUntilAsync(() => { lock (received) return received.Count >= 2; }, 5000);
        lock (received)
        {
            Assert.Equal(["PART", "REST"], received);
        }

        listener.Stop();
    }

    [Fact]
    public void LineAccumulator_SplitsRealAndLiteralTerminator()
    {
        // 真实回车换行与字面转义文本（对端手打 \r\n 当文本发送）都要切开
        var accumulator = new LineAccumulator("\r\n", "\\r\\n");
        Assert.Equal(["A", "B"], accumulator.Feed("A\r\nB\\r\\n"));
        Assert.False(accumulator.HasPending);
        // 无结束符的内容留在缓冲（由空闲刷新按收包块回调）
        Assert.Empty(accumulator.Feed("C"));
        Assert.True(accumulator.HasPending);
        Assert.Equal("C", accumulator.TakePending());
    }

    [Fact]
    public async Task TcpClient_SplitsLinesWhenPeerSendsLiteralEscapedTerminator()
    {
        // 用户场景：海康 VM 侧手打 \r\n 随文本发出（4 字符字面量），本侧仍按行切分
        var port = AllocateTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("111TestData\\r\\n111TestData\\r\\n"));
        });

        using var link = new TcpClientLink();
        var received = new List<string>();
        link.TextReceived += text => { lock (received) received.Add(text); };
        link.SetTerminator("\\r\\n");
        await link.StartAsync(new CommDevice { Name = "cli", Protocol = "TCP客户端", Host = "127.0.0.1", Port = port });

        await WaitUntilAsync(() => { lock (received) return received.Count >= 2; }, 5000);
        lock (received)
        {
            Assert.Equal(["111TestData", "111TestData"], received);
        }

        listener.Stop();
    }

    private static async Task EchoOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var buffer = new byte[1024];
        var stream = client.GetStream();
        var read = await stream.ReadAsync(buffer);
        var text = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\r', '\n');
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"ECHO:{text}\r\n"));
        listener.Stop();
    }
}
