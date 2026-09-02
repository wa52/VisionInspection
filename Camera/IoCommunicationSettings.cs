namespace SpeakerVisionInspection.Camera;

/// <summary>IO 通信配置：PLC 触发输入与 Strobe 光耦结果输出。</summary>
public sealed class IoCommunicationSettings
{
    public bool Enabled { get; set; } = true;

    public string TriggerInputLine { get; set; } = "Line0";

    public string TriggerEdge { get; set; } = "Rising";

    public string OutputMode { get; set; } = "NgOnly";

    public string NgOutputLine { get; set; } = "Line1";

    public string StrobeSource { get; set; } = "FrameTriggerWait";

    public string ActiveLevel { get; set; } = "Low";

    public double PulseMs { get; set; } = 500;
}
