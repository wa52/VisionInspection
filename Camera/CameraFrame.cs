namespace VisionInspection.Camera;

public static class CameraPixelFormat
{
    public const uint Mono8 = 0x01080001;
    public const uint Rgb8 = 0x02180014;

    public static int ChannelsOf(uint format) => format == Rgb8 ? 3 : 1;
}

/// <summary>
/// 一帧图像（已转换为可显示像素格式）。缓冲由控制器拥有并在事件返回后释放，
/// handler 必须同步消费（如立即拷贝像素）；调用方不得 Dispose。
/// </summary>
public sealed class CameraFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required uint PixelFormat { get; init; }
    public required IntPtr Data { get; init; }
    public required int DataLength { get; init; }

    public int Channels => CameraPixelFormat.ChannelsOf(PixelFormat);
}
