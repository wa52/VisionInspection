namespace SpeakerVisionInspection.Camera;

/// <summary>触发模式。</summary>
public enum CameraTriggerMode
{
    /// <summary>连续采集（TriggerMode=Off）。</summary>
    Continuous = 0,

    /// <summary>软触发：按钮/命令触发单帧（TriggerMode=On，Source=Software）。</summary>
    Software = 1,

    /// <summary>硬触发：PLC 经相机 IO 线触发（TriggerMode=On，Source=Line0/Line1）。</summary>
    Hardware = 2,
}
