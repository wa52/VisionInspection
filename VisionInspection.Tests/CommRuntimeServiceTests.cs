using VisionInspection.Comm;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>生产通信运行时测试：假链路注入，覆盖惰性建链/收发/队列/断链重建/互斥守卫（全离线，无真实 socket/串口）。</summary>
public class CommRuntimeServiceTests
{
    private sealed class FakeCommLink : ICommLink
    {
        private readonly object _gate = new();
        private readonly List<string> _sent = [];

        public bool Ready { get; set; } = true;
        public bool PeerReady { get; set; } = true;
        public bool ThrowOnStart { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public string? LastTerminator { get; private set; }
        public Action<string>? TextReceived { get; set; }
        public Action<string>? StatusChanged { get; set; }
        public Action<string>? LinkClosed { get; set; }

        public bool IsReady => Ready;
        public bool HasPeer => PeerReady;

        public Task StartAsync(CommDevice device, CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("端口被占用");
            }

            return Task.CompletedTask;
        }

        public void Stop() => StopCount++;

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _sent.Add(text);
            }

            return Task.CompletedTask;
        }

        public void SetTerminator(string escapedTerminator) => LastTerminator = escapedTerminator;

        public void Dispose() => Stop();

        public List<string> SnapshotSent() { lock (_gate) { return [.. _sent]; } }
        public void Emit(string text) => TextReceived?.Invoke(text);
        public void SimulateClosed() => LinkClosed?.Invoke("closed");
    }

    /// <summary>临时 comm.json 存储（测试结束删除临时文件）。</summary>
    private sealed class TempStore : IDisposable
    {
        public CommDeviceStore Store { get; }
        public string FilePath { get; }

        public TempStore(params CommDevice[] devices)
        {
            FilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"commrt_{Guid.NewGuid():N}.json");
            Store = new CommDeviceStore(FilePath);
            Store.Save(devices);
        }

        public void Dispose()
        {
            try { File.Delete(FilePath); } catch { /* 临时文件清理，允许已不存在 */ }
        }
    }

    [Fact]
    public void Send_CreatesLinkLazily_ForwardsText_ReportsHeld()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC", Protocol = "TCP客户端" });
        var links = new List<FakeCommLink>();
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => { var link = new FakeCommLink(); links.Add(link); return link; });

        Assert.False(svc.IsHeld("PLC"));
        var outcome = svc.Send("PLC", "OK\r\n");

        Assert.True(outcome.Ok);
        var link = Assert.Single(links);
        Assert.Equal(["OK\r\n"], link.SnapshotSent());
        Assert.True(svc.IsHeld("PLC"));
        // 第二次发送复用同一链路（不重复建链）
        svc.Send("PLC", "NG");
        Assert.Equal(["OK\r\n", "NG"], link.SnapshotSent());
        Assert.Equal(1, link.StartCount);
    }

    [Fact]
    public void Send_MissingOrEmptyDevice_FailsWithActionableHint()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => new FakeCommLink());

        var empty = svc.Send("", "x");
        Assert.False(empty.Ok);
        Assert.Contains("未选择通信设备", empty.Error);

        var unknown = svc.Send("不存在", "x");
        Assert.False(unknown.Ok);
        Assert.Contains("不存在设备", unknown.Error);
        Assert.Contains("通信管理", unknown.Error);
    }

    [Fact]
    public void Send_LinkNotReady_Fails()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        var links = new List<FakeCommLink>();
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => { var link = new FakeCommLink(); links.Add(link); return link; });

        Assert.True(svc.Send("PLC", "a").Ok);
        links[0].PeerReady = false;
        var outcome = svc.Send("PLC", "b");

        Assert.False(outcome.Ok);
        Assert.Contains("未就绪", outcome.Error);
        // 失败的发送不会多写一条
        Assert.Equal(["a"], links[0].SnapshotSent());
    }

    [Fact]
    public void Receive_TakesFifoThenTimesOut()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        var links = new List<FakeCommLink>();
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => { var link = new FakeCommLink(); links.Add(link); return link; });

        // 无积压：快速超时
        var timeout = svc.Receive("PLC", 30);
        Assert.True(timeout.Ok);
        Assert.True(timeout.TimedOut);

        links[0].Emit("A");
        links[0].Emit("B");
        var first = svc.Receive("PLC", 0);
        var second = svc.Receive("PLC", 0);
        Assert.False(first.TimedOut);
        Assert.Equal("A", first.Text);
        Assert.Equal("B", second.Text);
    }

    [Fact]
    public void Receive_WaitsUntilDataArrives()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        var links = new List<FakeCommLink>();
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => { var link = new FakeCommLink(); links.Add(link); return link; });
        svc.Receive("PLC", 0); // 先建链
        var link = links[0];

        var delayed = Task.Run(() =>
        {
            Thread.Sleep(80);
            link.Emit("X");
        });
        var outcome = svc.Receive("PLC", 2000);
        delayed.Wait();

        Assert.False(outcome.TimedOut);
        Assert.Equal("X", outcome.Text);
    }

    [Fact]
    public void LinkClosed_RemovesLink_NextCallRecreates()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        var links = new List<FakeCommLink>();
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => { var link = new FakeCommLink(); links.Add(link); return link; });

        svc.Send("PLC", "1");
        links[0].SimulateClosed();
        Assert.False(svc.IsHeld("PLC"));

        svc.Send("PLC", "2");
        Assert.Equal(2, links.Count);
        Assert.Equal(1, links[1].StartCount);
        Assert.Equal(["2"], links[1].SnapshotSent());
    }

    [Fact]
    public void StartFailure_ReturnsError_NotHeld_AndRetriesNextCall()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        var link = new FakeCommLink { ThrowOnStart = true };
        using var svc = new CommRuntimeService(store.Store, linkFactory: _ => link);

        var outcome = svc.Send("PLC", "x");
        Assert.False(outcome.Ok);
        Assert.Contains("无法连接设备", outcome.Error);
        Assert.False(svc.IsHeld("PLC"));

        link.ThrowOnStart = false;
        Assert.True(svc.Send("PLC", "x").Ok);
        Assert.Equal(["x"], link.SnapshotSent());
    }

    [Fact]
    public void Dispose_StopsAllLinks()
    {
        using var store = new TempStore(new CommDevice { Name = "PLC" });
        var link = new FakeCommLink();
        var svc = new CommRuntimeService(store.Store, linkFactory: _ => link);
        svc.Send("PLC", "x");

        svc.Dispose();
        Assert.Equal(1, link.StopCount);
        Assert.False(svc.IsHeld("PLC"));
    }

    [Fact]
    public void ReceiveQueue_Bounded_DropsOldest()
    {
        var queue = new CommReceiveQueue(8);
        for (var i = 0; i < 10; i++)
        {
            queue.Enqueue($"n{i}");
        }

        Assert.True(queue.TryTake(0, out var text));
        Assert.Equal("n2", text); // 前 2 条被丢最旧
        Assert.Equal(2, queue.DroppedCount);
        for (var i = 3; i <= 9; i++)
        {
            Assert.True(queue.TryTake(0, out _));
        }
        Assert.False(queue.TryTake(0, out _));
        queue.Enqueue("late");
        Assert.True(queue.TryTake(0, out var after));
        Assert.Equal("late", after);
    }
}
