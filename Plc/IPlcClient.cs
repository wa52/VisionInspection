namespace VisionInspection.Plc;

/// <summary>PLC 回传结果。</summary>
public enum PlcResult
{
    Ok,
    Ng,
}

/// <summary>
/// PLC 回传接缝。生产模式下程序将 Ready/Busy/OK/NG/Error 回传 PLC（如 IO 输出或 Modbus TCP）。
/// 首版提供 LoggingPlcClient（仅日志/模拟），真实协议待 PLC 品牌确认后实现。
/// </summary>
public interface IPlcClient : IDisposable
{
    /// <summary>未生产/断开。</summary>
    Task SendIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>可接收触发（硬触发等待中）。</summary>
    Task SendReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>正在处理上一帧。</summary>
    Task SendBusyAsync(CancellationToken cancellationToken = default);

    /// <summary>处理结果 OK/NG。</summary>
    Task SendResultAsync(PlcResult result, CancellationToken cancellationToken = default);

    /// <summary>错误（超时/断线/溢出）。</summary>
    Task SendErrorAsync(string errorCode, CancellationToken cancellationToken = default);
}
