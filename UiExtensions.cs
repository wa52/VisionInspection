using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VisionInspection.Detection;

namespace VisionInspection;

/// <summary>WPF UI 辅助扩展。</summary>
public static class UiExtensions
{
    /// <summary>初始化后返回自身（流畅式 UI 构建）。</summary>
    public static T Apply<T>(this T element, Action<T> init) where T : System.Windows.UIElement
    {
        init(element);
        return element;
    }

    /// <summary>Mat → 冻结的 BGR24 BitmapSource（单通道自动转 BGR；空图/已释放返回 null）。</summary>
    public static BitmapSource? MatToBitmap(Mat mat)
    {
        if (mat.IsDisposed || mat.Empty())
        {
            return null;
        }
        using Mat mat2 = mat.Clone();
        if (mat2.Channels() == 1)
        {
            Cv2.CvtColor(mat2, mat2, ColorConversionCodes.GRAY2BGR);
        }
        BitmapSource bitmapSource = BitmapSource.Create(mat2.Width, mat2.Height, 96.0, 96.0, PixelFormats.Bgr24, null, mat2.Data, mat2.Width * mat2.Height * 3, mat2.Width * 3);
        ((Freezable)bitmapSource).Freeze();
        return bitmapSource;
    }

    /// <summary>
    /// 节点输出图批量转 WPF 位图（缩略图条/主图显示用）：同一 Mat 实例可能在多个节点键下共享（透传/复用），
    /// 按引用去重转换一次、多次复用；转换完成后统一释放输入 Mat（单个失败只跳过该图，不中断整轮渲染）。
    /// </summary>
    public static Dictionary<string, BitmapSource> ConvertNodeImages(IReadOnlyDictionary<string, Mat> nodeImages)
    {
        var result = new Dictionary<string, BitmapSource>();
        var converted = new Dictionary<Mat, BitmapSource?>(ReferenceEqualityComparer.Instance);
        foreach (var (key, mat) in nodeImages)
        {
            try
            {
                if (mat.IsDisposed)
                {
                    continue;
                }
                if (!converted.TryGetValue(mat, out var bitmapSource))
                {
                    bitmapSource = MatToBitmap(mat);
                    converted[mat] = bitmapSource;
                }
                if (bitmapSource != null)
                {
                    result[key] = bitmapSource;
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }
        foreach (var mat in converted.Keys)
        {
            try
            {
                if (!mat.IsDisposed)
                {
                    mat.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }
        return result;
    }
}
