namespace SpeakerVisionInspection.Camera;

/// <summary>采集参数（MVS 参数键见 MvCameraControlCameraController）。</summary>
public sealed record CameraParameters
{
    public double ExposureTimeUs { get; set; } = 5000;
    public double Gain { get; set; } = 0;
    public double Gamma { get; set; } = 1;
    public double FrameRate { get; set; } = 0;
    public string PixelFormat { get; set; } = "Mono8";
}
