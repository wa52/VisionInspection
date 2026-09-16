namespace VisionInspection.Services;

/// <summary>相机运行时配置。名称保留以兼容现有调用，程序不依赖 VisionMaster 或其授权服务。</summary>
public sealed record VisionMasterConfig
{
    public string? MvsRuntimeDir { get; init; }
    /// <summary>图像保存目录；未配置时回退到应用目录下 images。</summary>
    public string? SaveImageDir { get; init; }
}
