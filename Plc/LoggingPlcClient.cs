using System.Collections.Concurrent;

namespace SpeakerVisionInspection.Plc;

/// <summary>
/// 日志/模拟 PLC 回传：记录每条消息供测试与联调，实际不连接任何 PLC。
/// </summary>
public sealed class LoggingPlcClient : IPlcClient
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly Action<string> _log;

    public LoggingPlcClient(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    /// <summary>按发送顺序记录的消息快照。</summary>
    public IReadOnlyList<string> Messages => [.. _messages];

    public bool IsDisposed { get; private set; }

    public Task SendIdleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enqueue("IDLE");
        return Task.CompletedTask;
    }

    public Task SendReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enqueue("READY");
        return Task.CompletedTask;
    }

    public Task SendBusyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enqueue("BUSY");
        return Task.CompletedTask;
    }

    public Task SendResultAsync(PlcResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enqueue(result == PlcResult.Ok ? "OK" : "NG");
        return Task.CompletedTask;
    }

    public Task SendErrorAsync(string errorCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enqueue($"ERROR:{errorCode}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IsDisposed = true;
    }

    private void Enqueue(string message)
    {
        _messages.Enqueue(message);
        _log(message);
    }
}
