using OpenCvSharp;

namespace VisionInspection.Detection;

/// <summary>单个分割字符：源图像素矩形 + 识别结果（未匹配 = ?）。</summary>
public sealed record SegChar(Rect Box, string Label, double Score);

/// <summary>
/// 字符识别算法核（传统视觉路线，VisionMaster 字符识别同类原理，纯逻辑可单测）：
/// 二值化（固定阈值/Otsu + 极性：亮字暗底/暗字亮底）→ 膨胀/腐蚀（先膨胀后腐蚀）
/// → 连通域外轮廓字符分割（按 X 排序 + 高度过滤）→ 归一化 32×48 二值样本
/// → 字模库逐样本 Dice 相似度匹配（多样本取最高）。
/// </summary>
public static class CharRecognizer
{
    /// <summary>二值化：前景=字符=255。brightOnDark=true（亮字暗底）用 Binary，否则 BinaryInv。</summary>
    public static Mat Binarize(Mat gray, double threshold, bool otsu, bool brightOnDark)
    {
        var type = brightOnDark ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv;
        if (otsu)
        {
            type |= ThresholdTypes.Otsu;
        }
        var dst = new Mat();
        Cv2.Threshold(gray, dst, threshold, 255, type);
        return dst;
    }

    /// <summary>形态学（原地，3×3 矩形核）：先膨胀（连接断裂笔画）后腐蚀（分离粘连字符），次数 ≤0 跳过。</summary>
    public static void MorphApply(Mat bin, int dilateIter, int erodeIter)
    {
        if (dilateIter <= 0 && erodeIter <= 0) return;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        if (dilateIter > 0)
        {
            Cv2.Dilate(bin, bin, kernel, iterations: dilateIter);
        }
        if (erodeIter > 0)
        {
            Cv2.Erode(bin, bin, kernel, iterations: erodeIter);
        }
    }

    /// <summary>字符分割：外轮廓 → 外接矩形，按 X 升序；高度过滤（maxCharH≤0 不限），剔除宽度 &lt;2px 的噪点。</summary>
    public static List<Rect> SegmentChars(Mat bin, int minCharH, int maxCharH)
    {
        Cv2.FindContours(bin, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours
            .Select(Cv2.BoundingRect)
            .Where(r => r.Width >= 2 && r.Height >= Math.Max(1, minCharH) && (maxCharH <= 0 || r.Height <= maxCharH))
            .OrderBy(r => r.X)
            .ToList();
    }

    /// <summary>字符样本归一化：裁剪 → 缩放 32×48 → 128 阈值二值化 → 0/255 字节数组（行优先）。</summary>
    public static byte[] NormalizeSample(Mat bin, Rect charRect)
    {
        using var crop = new Mat(bin, charRect);
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(CharTemplateLibrary.NormW, CharTemplateLibrary.NormH),
            0, 0, InterpolationFlags.Area);
        using var bw = new Mat();
        Cv2.Threshold(resized, bw, 128, 255, ThresholdTypes.Binary);
        return bw.GetArray(out byte[] buf)
            ? buf // 只读拷贝
            : new byte[CharTemplateLibrary.NormW * CharTemplateLibrary.NormH];
    }

    /// <summary>与字模库匹配：逐样本 Dice 取最高。库为空 → ("?", 0)。</summary>
    public static (string Label, double Score) Match(byte[] sample, IReadOnlyList<CharSample> templates)
    {
        var bestLabel = "?";
        var bestScore = 0.0;
        foreach (var t in templates)
        {
            var s = Dice(sample, t.Pixels);
            if (s > bestScore)
            {
                bestLabel = t.Label;
                bestScore = s;
            }
        }
        return (bestLabel, bestScore);
    }

    /// <summary>Dice 系数 2|A∩B|/(|A|+|B|)（前景 = 像素≥128；双方均无前景 → 0）。</summary>
    public static double Dice(byte[] a, byte[] b)
    {
        int inter = 0, sa = 0, sb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var fa = a[i] >= 128;
            var fb = b[i] >= 128;
            if (fa) sa++;
            if (fb) sb++;
            if (fa && fb) inter++;
        }
        return sa + sb == 0 ? 0.0 : 2.0 * inter / (sa + sb);
    }

    /// <summary>整段识别：分割 + 逐字符匹配（按 X 排序的文本序列）。</summary>
    public static List<SegChar> Recognize(Mat bin, int minCharH, int maxCharH, IReadOnlyList<CharSample> templates)
    {
        var result = new List<SegChar>();
        foreach (var r in SegmentChars(bin, minCharH, maxCharH))
        {
            var (label, score) = Match(NormalizeSample(bin, r), templates);
            result.Add(new SegChar(r, label, score));
        }
        return result;
    }
}
