namespace VisionInspection.Camera;

/// <summary>采集参数（MVS 参数键见 MvCameraControlCameraController）。</summary>
public sealed record CameraParameters
{
    /// <summary>自动曝光模式：Off / Once / Continuous（Off=手动曝光）。</summary>
    public string ExposureAuto { get; set; } = "Off";

    public double ExposureTimeUs { get; set; } = 5000;

    /// <summary>自动增益模式：Off / Once / Continuous（Off=手动增益）。</summary>
    public string GainAuto { get; set; } = "Off";

    public double Gain { get; set; } = 0;
    public double Gamma { get; set; } = 1;
    public double FrameRate { get; set; } = 0;

    /// <summary>图像宽度（px）；0 = 保持相机当前值。</summary>
    public int ImageWidth { get; set; } = 0;

    /// <summary>图像高度（px）；0 = 保持相机当前值。</summary>
    public int ImageHeight { get; set; } = 0;

    public string PixelFormat { get; set; } = "Mono8";
}
