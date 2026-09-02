using System.IO;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 与 Python Preprocessor 完全一致的预处理：短边缩放(INTER_AREA) -> 中心裁剪 -> BGR→RGB -> /255 -> ImageNet 归一化 -> CHW。
/// </summary>
public static class ImagePreprocessService
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>读取图片为 BGR（用字节流 ImDecode，规避中文/Unicode 路径的 imread 失败）。</summary>
    public static Mat LoadBgr(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"无法读取图片: {path}");
        }

        var bytes = File.ReadAllBytes(path);
        var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (mat.Empty())
        {
            throw new InvalidDataException($"无法解码图片: {path}");
        }

        return mat;
    }

    public static DenseTensor<float> Prepare(Mat bgr, int resize, int imageSize)
    {
        using var resized = ResizeShortSide(bgr, resize);
        using var cropped = CenterCrop(resized, imageSize);
        return ToTensor(cropped);
    }

    private static Mat ResizeShortSide(Mat img, int resize)
    {
        var h = img.Height;
        var w = img.Width;
        var scale = (double)resize / Math.Min(h, w);
        var newW = Math.Max(1, (int)Math.Round(w * scale, MidpointRounding.ToEven));
        var newH = Math.Max(1, (int)Math.Round(h * scale, MidpointRounding.ToEven));
        var dst = new Mat();
        Cv2.Resize(img, dst, new Size(newW, newH), 0, 0, InterpolationFlags.Area);
        return dst;
    }

    private static Mat CenterCrop(Mat img, int size)
    {
        var h = img.Height;
        var w = img.Width;
        if (h < size || w < size)
        {
            throw new InvalidDataException($"图片尺寸 {w}x{h} 小于裁剪尺寸 {size}，请调大 resize");
        }

        var y0 = (h - size) / 2;
        var x0 = (w - size) / 2;
        return new Mat(img, new OpenCvSharp.Rect(x0, y0, size, size));
    }

    private static DenseTensor<float> ToTensor(Mat bgr)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        var h = rgb.Height;
        var w = rgb.Width;
        var tensor = new DenseTensor<float>([1, 3, h, w]);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = rgb.At<Vec3b>(y, x);
                for (var c = 0; c < 3; c++)
                {
                    var value = p[c] / 255.0f;
                    tensor[0, c, y, x] = (value - Mean[c]) / Std[c];
                }
            }
        }

        return tensor;
    }
}
