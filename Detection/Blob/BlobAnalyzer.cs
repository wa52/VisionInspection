using OpenCvSharp;

namespace VisionInspection.Detection;

/// <summary>单个斑点特征（源图像素坐标；质心/外接框/长短轴/圆度/矩形度与 VM Blob查找 输出同名对应）。</summary>
public sealed record BlobFeature(
    double Area,
    double Perimeter,
    double CentroidX,
    double CentroidY,
    Rect Bound,
    double BoxCx,
    double BoxCy,
    double BoxWidth,
    double BoxHeight,
    double BoxAngle,
    double LongAxis,
    double ShortAxis,
    double Circularity,
    double Rectangularity,
    double MinGray,
    double MaxGray,
    Point[] Contour);

/// <summary>
/// 斑点筛选条件（VM Blob查找 SelectBy* 同款：各特征独立开关 + min/max 范围，默认只按面积筛）。
/// </summary>
public sealed record BlobFilter(
    bool ByArea = true, double MinArea = 10, double MaxArea = 999999999,
    bool ByPerimeter = false, double MinPerimeter = 0, double MaxPerimeter = 999999999,
    bool ByCircularity = false, double MinCircularity = 0, double MaxCircularity = 1,
    bool ByRectangularity = false, double MinRectangularity = 0, double MaxRectangularity = 1,
    bool ByLongAxis = false, double MinLongAxis = 0, double MaxLongAxis = 999999999,
    bool ByShortAxis = false, double MinShortAxis = 0, double MaxShortAxis = 999999999)
{
    /// <summary>除面积外的特征筛选（面积用连通域统计的像素数在提取阶段预筛，不走这里）。</summary>
    public bool Pass(BlobFeature b) =>
        (!ByPerimeter || (b.Perimeter >= MinPerimeter && b.Perimeter <= MaxPerimeter))
        && (!ByCircularity || (b.Circularity >= MinCircularity && b.Circularity <= MaxCircularity))
        && (!ByRectangularity || (b.Rectangularity >= MinRectangularity && b.Rectangularity <= MaxRectangularity))
        && (!ByLongAxis || (b.LongAxis >= MinLongAxis && b.LongAxis <= MaxLongAxis))
        && (!ByShortAxis || (b.ShortAxis >= MinShortAxis && b.ShortAxis <= MaxShortAxis));
}

/// <summary>
/// Blob 分析算法核（传统视觉路线，VM Blob查找/斑点分析同类原理，纯逻辑可单测）：
/// 二值化（固定阈值/Otsu/双阈值/无二值化 + 极性 亮斑暗底/暗斑亮底）→ 孔洞填充（面积≤阈值）
/// → 连通域标记（8/4 邻接，统计面积/质心/外接矩形）→ 逐斑点特征（周长/最小外接矩形/长短轴/圆度/矩形度/灰度范围）
/// → 特征筛选 → 排序 → 截取前 findNum 个。
/// </summary>
public static class BlobAnalyzer
{
    public const string ThFixed = "固定阈值";
    public const string ThOtsu = "Otsu自动";
    public const string ThDouble = "双阈值";
    public const string ThNone = "无二值化";

    public const string SortArea = "面积";
    public const string SortPerimeter = "周长";
    public const string SortCircularity = "圆度";
    public const string SortRectangularity = "矩形度";
    public const string SortCentroidX = "质心X";
    public const string SortCentroidY = "质心Y";
    public const string SortBoxAngle = "外接框角度";

    public const string SortAsc = "升序";
    public const string SortDesc = "降序";
    public const string SortNone = "不排序";

    /// <summary>
    /// 二值化（前景=斑点=255）：固定阈值/Otsu 按极性取 Binary/BinaryInv；
    /// 双阈值保留 [low, high] 灰度区间（暗斑取反：保留区间外）；
    /// 无二值化 = 输入已是二值图，非零像素即前景。
    /// </summary>
    public static Mat Binarize(Mat gray, string? thresholdType, bool brightOnDark, double threshold, double thresholdHigh)
    {
        var dst = new Mat();
        if (string.Equals(thresholdType, ThNone, StringComparison.Ordinal))
        {
            Cv2.Threshold(gray, dst, 0, 255, ThresholdTypes.Binary);
            return dst;
        }
        if (string.Equals(thresholdType, ThDouble, StringComparison.Ordinal))
        {
            Cv2.InRange(gray, new Scalar(threshold), new Scalar(thresholdHigh), dst);
            if (!brightOnDark)
            {
                Cv2.BitwiseNot(dst, dst);
            }
            return dst;
        }
        var type = brightOnDark ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv;
        if (string.Equals(thresholdType, ThOtsu, StringComparison.Ordinal))
        {
            type |= ThresholdTypes.Otsu;
        }
        Cv2.Threshold(gray, dst, threshold, 255, type);
        return dst;
    }

    /// <summary>
    /// 孔洞填充（原地）：先按外轮廓整体填充得到"无孔版本"，与原图相减取孔洞掩码，
    /// 面积 ≤ maxHoleArea 的孔洞填回前景。maxHoleArea ≤ 0 不处理。
    /// </summary>
    public static void FillHoles(Mat bin, int maxHoleArea)
    {
        if (maxHoleArea <= 0 || bin.Empty()) return;
        Cv2.FindContours(bin, out var outer, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (outer.Length == 0) return;
        using var filled = bin.Clone();
        foreach (var contour in outer)
        {
            Cv2.FillPoly(filled, new[] { contour }, Scalar.All(255));
        }
        using var holes = new Mat();
        Cv2.Subtract(filled, bin, holes);
        Cv2.FindContours(holes, out var holeContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var hole in holeContours)
        {
            if (Cv2.ContourArea(hole) > maxHoleArea) continue;
            Cv2.FillPoly(bin, new[] { hole }, Scalar.All(255));
        }
    }

    /// <summary>
    /// 提取斑点：孔洞填充 → 连通域标记（按 connectivity 取 8/4 邻接）→ 面积预筛 →
    /// 逐斑点提取轮廓并计算特征 → 特征筛选 → 排序 → 截取前 findNum 个。
    /// </summary>
    public static List<BlobFeature> Analyze(
        Mat gray, Mat bin, int connectivity, int holeMinArea, int findNum,
        BlobFilter filter, string? sortFeature, string? sortMode)
    {
        var result = new List<BlobFeature>();
        if (bin.Empty() || gray.Empty()) return result;
        using var work = bin.Clone();
        FillHoles(work, holeMinArea);

        using var labels = new Mat();
        using var stats = new Mat();
        using var cents = new Mat();
        var conn = connectivity == 4 ? PixelConnectivity.Connectivity4 : PixelConnectivity.Connectivity8;
        var count = Cv2.ConnectedComponentsWithStats(work, labels, stats, cents, conn);
        for (var i = 1; i < count; i++) // 0 = 背景
        {
            var area = stats.At<int>(i, 4);
            if (filter.ByArea && (area < filter.MinArea || area > filter.MaxArea)) continue;

            var bound = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            if (bound.Width <= 0 || bound.Height <= 0) continue;

            // 只在斑点外接矩形内取标签掩码/轮廓/灰度范围（大图小 ROI，避免逐斑点全图扫描）
            using var labView = new Mat(labels, bound);
            using var mask = new Mat();
            Cv2.InRange(labView, new Scalar(i), new Scalar(i), mask);
            Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) continue;
            var local = contours.OrderByDescending(c => c.Length).First();
            var contour = local.Select(p => new Point(p.X + bound.X, p.Y + bound.Y)).ToArray();

            var perimeter = Cv2.ArcLength(local, true);
            using var grayView = new Mat(gray, bound);
            Cv2.MinMaxLoc(grayView, out var minGray, out var maxGray, out _, out _, mask);
            var box = Cv2.MinAreaRect(local);

            var feature = new BlobFeature(
                Area: area,
                Perimeter: perimeter,
                CentroidX: cents.At<double>(i, 0),
                CentroidY: cents.At<double>(i, 1),
                Bound: bound,
                BoxCx: box.Center.X + bound.X,
                BoxCy: box.Center.Y + bound.Y,
                BoxWidth: box.Size.Width,
                BoxHeight: box.Size.Height,
                BoxAngle: box.Angle,
                LongAxis: Math.Max(box.Size.Width, box.Size.Height),
                ShortAxis: Math.Min(box.Size.Width, box.Size.Height),
                Circularity: perimeter > 0 ? Math.Min(1.0, 4 * Math.PI * area / (perimeter * perimeter)) : 0,
                Rectangularity: box.Size.Width * box.Size.Height > 0
                    ? Math.Min(1.0, area / (box.Size.Width * box.Size.Height))
                    : 0,
                MinGray: minGray,
                MaxGray: maxGray,
                Contour: contour);
            if (!filter.Pass(feature)) continue;
            result.Add(feature);
        }

        Sort(result, sortFeature, sortMode);
        if (findNum > 0 && result.Count > findNum)
        {
            result = result.Take(findNum).ToList();
        }
        return result;
    }

    /// <summary>按特征排序（降序[默认]/升序/不排序=连通域标记顺序；未知特征按面积）。</summary>
    private static void Sort(List<BlobFeature> blobs, string? feature, string? mode)
    {
        if (string.Equals(mode, SortNone, StringComparison.Ordinal)) return;
        Comparison<BlobFeature> asc = (feature ?? SortArea).Trim() switch
        {
            SortPerimeter => (a, b) => a.Perimeter.CompareTo(b.Perimeter),
            SortCircularity => (a, b) => a.Circularity.CompareTo(b.Circularity),
            SortRectangularity => (a, b) => a.Rectangularity.CompareTo(b.Rectangularity),
            SortCentroidX => (a, b) => a.CentroidX.CompareTo(b.CentroidX),
            SortCentroidY => (a, b) => a.CentroidY.CompareTo(b.CentroidY),
            SortBoxAngle => (a, b) => a.BoxAngle.CompareTo(b.BoxAngle),
            _ => (a, b) => a.Area.CompareTo(b.Area),
        };
        blobs.Sort(string.Equals(mode, SortAsc, StringComparison.Ordinal) ? asc : (a, b) => asc(b, a));
    }
}
