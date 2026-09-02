namespace SpeakerVisionInspection.Camera;

/// <summary>相机信息（枚举结果）。</summary>
public sealed record CameraInfo(
    string DisplayName,
    string SerialNumber,
    string? ModelName,
    string? IpAddress,
    string InterfaceType)
{
    public override string ToString() => DisplayName;
}
