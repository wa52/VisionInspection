namespace SpeakerVisionInspection.Trigger;

/// <summary>硬触发状态机状态。</summary>
public enum TriggerState
{
    /// <summary>未生产（未启用硬触发）。</summary>
    Idle,

    /// <summary>已启用硬触发，等待 PLC 触发信号（Ready）。</summary>
    Armed,

    /// <summary>收到触发帧，处理中（Busy）。</summary>
    Busy,

    /// <summary>错误（超时/断线/溢出等）。</summary>
    Error,
}

/// <summary>
/// 硬触发状态机（纯逻辑，无 SDK/UI 依赖）。
/// 转移方法均返回 bool：合法转移返回 true 并更新状态，非法转移返回 false 并记录 LastError。
/// </summary>
public sealed class TriggerStateMachine
{
    public TriggerState State { get; private set; } = TriggerState.Idle;

    /// <summary>最近一次非法转移或错误的原因。</summary>
    public string? LastError { get; private set; }

    /// <summary>Busy 期间溢出触发计数（累计）。</summary>
    public int OverrunCount { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>进入生产（硬触发启用）：Idle/Error → Armed。</summary>
    public bool Arm()
    {
        if (State is TriggerState.Armed or TriggerState.Busy)
        {
            return Fail("已在生产状态（Armed/Busy），无需重复 Arm");
        }

        SetState(TriggerState.Armed);
        return true;
    }

    /// <summary>退出生产：任意状态 → Idle。</summary>
    public bool Disarm()
    {
        if (State == TriggerState.Idle)
        {
            return true;
        }

        SetState(TriggerState.Idle);
        return true;
    }

    /// <summary>收到触发帧：Armed → Busy；Busy 期间再次触发记溢出。</summary>
    public bool TriggerReceived()
    {
        if (State == TriggerState.Busy)
        {
            OverrunCount++;
            return Fail("Busy 期间再次触发（溢出）");
        }

        if (State != TriggerState.Armed)
        {
            return Fail($"未处于等待触发状态（当前 {State}），忽略触发");
        }

        SetState(TriggerState.Busy);
        return true;
    }

    /// <summary>处理完成：Busy → Armed（回到等待下一次触发）。</summary>
    public bool FrameProcessed()
    {
        if (State != TriggerState.Busy)
        {
            return Fail($"无处理中的帧（当前 {State}），忽略处理完成");
        }

        SetState(TriggerState.Armed);
        return true;
    }

    /// <summary>取图超时（GrabTimeout）：已检测到触发（Armed）后相机迟迟未出帧 → Error。Waiting Trigger 无限停留，无触发不超时。</summary>
    public bool GrabTimeout()
    {
        if (State != TriggerState.Armed)
        {
            return Fail($"非等待触发状态（当前 {State}），忽略超时");
        }

        LastError = "检测到触发后相机未出帧（取图超时）";
        SetState(TriggerState.Error);
        return true;
    }

    /// <summary>相机断线：Armed/Busy → Error。</summary>
    public bool CameraDisconnected()
    {
        if (State is not (TriggerState.Armed or TriggerState.Busy))
        {
            return Fail($"非生产状态（当前 {State}），忽略断线");
        }

        LastError = "相机断开连接";
        SetState(TriggerState.Error);
        return true;
    }

    /// <summary>处理帧异常：Busy → Error（如检测抛异常）。</summary>
    public bool ProcessingError(string reason)
    {
        if (State != TriggerState.Busy)
        {
            return Fail($"非处理中状态（当前 {State}），忽略处理错误");
        }

        LastError = reason;
        SetState(TriggerState.Error);
        return true;
    }

    /// <summary>从错误恢复：Error → Armed（重新进入生产）。</summary>
    public bool Reset()
    {
        if (State != TriggerState.Error)
        {
            return Fail($"非错误状态（当前 {State}），无需 Reset");
        }

        LastError = null;
        SetState(TriggerState.Armed);
        return true;
    }

    private bool Fail(string reason)
    {
        LastError = reason;
        return false;
    }

    private void SetState(TriggerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
