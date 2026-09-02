namespace SpeakerVisionInspection.Camera;

/// <summary>触发配置（触发模式 + 硬触发参数）。</summary>
public sealed record TriggerSettings
{
    /// <summary>触发模式：连续 / 软触发 / 硬触发。</summary>
    public string TriggerMode { get; set; } = "连续";

    /// <summary>触发源：Line0 / Line1。</summary>
    public string TriggerSource { get; set; } = "Line0";

    /// <summary>触发沿：Rising / Falling（适配 PLC PNP/NPN）。</summary>
    public string TriggerActivation { get; set; } = "Rising";

    /// <summary>触发延迟（us）。</summary>
    public double TriggerDelayUs { get; set; }

    /// <summary>输入滤波/去抖（us）。</summary>
    public double TriggerFilterUs { get; set; }

    /// <summary>最小触发间隔（ms）。</summary>
    public double MinTriggerIntervalMs { get; set; } = 100;

    /// <summary>取图超时（ms）：已产出过帧后，出帧间隔超过该值即报"取图超时"（PLC 触发后相机未出帧）。</summary>
    public double GrabTimeoutMs { get; set; } = 500;
}
