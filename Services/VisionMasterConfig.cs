namespace SpeakerVisionInspection.Services;

public sealed record VisionMasterConfig
{
    public string? VisionMasterRoot { get; init; }
    public string? MvdSdkRoot { get; init; }
    public string? MvsRuntimeDir { get; init; }

    /// <summary>MVS SDK Development 根目录（含 Development\DotNet\win64\netstandard2.0\MvCameraControl.Net.dll）。</summary>
    public string? MvsSdkDevRoot { get; init; }

    /// <summary>图像保存目录；未配置时回退到应用目录下 images。</summary>
    public string? SaveImageDir { get; init; }
}
