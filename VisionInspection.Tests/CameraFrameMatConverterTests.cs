using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionInspection.Camera;
using VisionInspection.Production;
using Xunit;

namespace VisionInspection.Tests;

public class CameraFrameMatConverterTests
{
    [Fact]
    public void Mono8_ConvertsToBgr3Channels()
    {
        var w = 4;
        var h = 3;
        var data = new byte[w * h];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 40);
        using var bgr = CameraFrameMatConverter.ToBgrMat(data, w, h, CameraPixelFormat.Mono8);
        Assert.Equal(3, bgr.Channels());
        Assert.Equal(w, bgr.Width);
        Assert.Equal(h, bgr.Height);
        // 灰阶转 BGR：三通道值相等
        var p = bgr.At<Vec3b>(1, 1);
        Assert.Equal(p[0], p[1]);
        Assert.Equal(p[1], p[2]);
    }

    [Fact]
    public void Rgb8_ConvertsToBgrChannelSwap()
    {
        var w = 2;
        var h = 1;
        // 红色像素 RGB=(255,0,0) → BGR=(0,0,255)
        var data = new byte[] { 255, 0, 0, 0, 255, 0 };
        using var bgr = CameraFrameMatConverter.ToBgrMat(data, w, h, CameraPixelFormat.Rgb8);
        var p = bgr.At<Vec3b>(0, 0);
        Assert.Equal(0, p[0]); // B
        Assert.Equal(0, p[1]); // G
        Assert.Equal(255, p[2]); // R
    }

    [Fact]
    public void FromCameraFrame_CopiesBuffer()
    {
        var w = 2;
        var h = 2;
        var data = new byte[w * h * 3];
        for (var i = 0; i < data.Length; i++) data[i] = 100;
        var hGlobal = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, hGlobal, data.Length);
            var frame = new CameraFrame
            {
                Width = w,
                Height = h,
                PixelFormat = CameraPixelFormat.Rgb8,
                Data = hGlobal,
                DataLength = data.Length,
            };
            using var bgr = CameraFrameMatConverter.ToBgrMat(frame);
            Assert.Equal(3, bgr.Channels());
            // 修改原缓冲不影响已转换 Mat（证明已拷贝）
            Marshal.WriteByte(hGlobal, 0, 0);
            Assert.NotEqual(0, bgr.At<Vec3b>(0, 0)[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(hGlobal);
        }
    }
}
