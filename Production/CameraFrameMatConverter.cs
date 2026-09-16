using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionInspection.Camera;

namespace VisionInspection.Production;

/// <summary>
/// 相机帧 → OpenCV BGR Mat。
/// 约束：CameraFrame.Data 在事件返回后由控制器释放，必须在事件内同步转换（CvtColor 即完成拷贝）。
/// Mono8 → GRAY2BGR；RGB8 → RGB2BGR（PatchCore 必须吃 BGR，不能把 RGB 当 BGR 喂）。
/// </summary>
public static class CameraFrameMatConverter
{
    /// <summary>把相机帧同步转换为 BGR Mat（返回 Mat 拥有自己的像素缓冲，可跨事件使用）。</summary>
    public static Mat ToBgrMat(CameraFrame frame)
    {
        return ToBgrMat(frame.Data, frame.Width, frame.Height, frame.PixelFormat);
    }

    /// <summary>从已复制的像素缓冲构造 BGR Mat。data 必须已是像素连续缓冲（Mono8/RGB8）。</summary>
    public static Mat ToBgrMat(IntPtr data, int width, int height, uint pixelFormat)
    {
        if (width <= 0 || height <= 0 || data == IntPtr.Zero)
        {
            throw new ArgumentException($"非法帧尺寸: {width}x{height}");
        }

        var channels = CameraPixelFormat.ChannelsOf(pixelFormat);
        var matType = channels == 3 ? MatType.CV_8UC3 : MatType.CV_8UC1;
        using var src = Mat.FromPixelData(height, width, matType, data);

        var bgr = new Mat();
        Cv2.CvtColor(src, bgr, channels == 3 ? ColorConversionCodes.RGB2BGR : ColorConversionCodes.GRAY2BGR);
        return bgr;
    }

    /// <summary>从字节数组构造 BGR Mat（生产者线程已把帧缓冲拷进数组，工作线程转换）。</summary>
    public static Mat ToBgrMat(byte[] data, int width, int height, uint pixelFormat)
    {
        if (data.Length < width * height * CameraPixelFormat.ChannelsOf(pixelFormat))
        {
            throw new ArgumentException("像素缓冲长度不足");
        }

        var hGlobal = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, hGlobal, data.Length);
            return ToBgrMat(hGlobal, width, height, pixelFormat);
        }
        finally
        {
            Marshal.FreeHGlobal(hGlobal);
        }
    }
}
