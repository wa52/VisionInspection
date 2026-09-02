using OpenCvSharp;

namespace SpeakerVisionInspection.Detection;

/// <summary>单个轮廓匹配实例（搜索图原始像素坐标）：基准点位置 + 旋转角(度) + 分数(0~1)。</summary>
public sealed record ShapeMatchInstance(double X, double Y, double Angle, double Score);

/// <summary>
/// 方向轮廓匹配器（VisionMaster 轮廓匹配同类原理，由粗到细）：
/// 粗层全位置×粗角度步长评分（Score = 方向匹配点数/模板有效点数；点积≥cos(45°) 视为方向匹配；
/// 贪心早停：必然失配点数超出预算即剪枝）→ 候选 NMS → 逐层细化位置/角度 → 原始分辨率 → 圆域 IoU NMS 取前 N。
/// 方向极性：启用时要求梯度方向一致（dot≥阈值），忽略时允许反向（|dot|≥阈值）。
/// </summary>
public sealed class ShapeMatcher
{
    private const double MaxDirectionDiffDeg = 45.0;
    private const double FineAngleStep = 1.0;
    private const double MaxCoarseAngleStep = 12.0;
    private const int MaxLevels = 6;

    private readonly ShapeTemplate _template;
    private readonly double _angleStart;
    private readonly double _angleExtent;
    private readonly int _numMatches;
    private readonly double _minScore;
    private readonly double _maxOverlap;
    private readonly bool _usePolarity;
    private readonly double _minContrast;
    private readonly double _sigma;

    private readonly double _cosDirThreshold;

    public ShapeMatcher(ShapeTemplate template, double minScore, double angleStart, double angleExtent,
        int numMatches, double maxOverlap, double minContrast, double sigma, bool usePolarity)
    {
        _template = template;
        _minScore = Math.Clamp(minScore, 0.01, 1.0);
        _angleStart = angleStart;
        _angleExtent = Math.Max(0, angleExtent);
        _numMatches = Math.Max(1, numMatches);
        _maxOverlap = Math.Clamp(maxOverlap, 0.05, 0.95);
        _usePolarity = usePolarity;
        _minContrast = Math.Max(1, minContrast);
        _sigma = Math.Max(0, sigma);
        _cosDirThreshold = Math.Cos(MaxDirectionDiffDeg * Math.PI / 180.0);
    }

    /// <summary>在灰度搜索图中查找全部实例（按分数降序）。</summary>
    public List<ShapeMatchInstance> Find(Mat graySearch)
    {
        if (graySearch.Empty() || graySearch.Channels() != 1 || _template.Levels == 0)
        {
            return [];
        }

        var levels = LevelsForImage(Math.Min(graySearch.Width, graySearch.Height), _template.Levels);
        var coarsest = levels - 1;

        // 金字塔：images[i] = 1/2^i 缩放层；每层预计算归一化梯度方向与幅值
        var dirX = new float[levels][];
        var dirY = new float[levels][];
        var mag = new float[levels][];
        var width = new int[levels];
        var height = new int[levels];
        var current = graySearch.Clone();
        try
        {
            for (var i = 0; i < levels; i++)
            {
                if (i > 0)
                {
                    var next = new Mat();
                    Cv2.PyrDown(current, next);
                    current.Dispose();
                    current = next;
                }
                (dirX[i], dirY[i], mag[i]) = ExtractDirections(current, _sigma);
                width[i] = current.Width;
                height[i] = current.Height;
            }
        }
        finally
        {
            current.Dispose();
        }

        // 由粗到细
        var candidates = SearchCoarsestLevel(dirX, dirY, mag, width, height, coarsest);
        var previousStep = Math.Min(FineAngleStep * Math.Pow(2, coarsest), MaxCoarseAngleStep);
        for (var l = coarsest - 1; l >= 0; l--)
        {
            candidates = RefineLevel(candidates, l, previousStep, dirX, dirY, mag, width, height);
            previousStep = FineAngleStep * Math.Pow(2, l);
        }

        // 圆域 IoU NMS（半径取模板最远轮廓点），按分数降序取前 N；
        // 同分按 |角度| 升序稳定胜负（List.Sort 不稳定，恒等位姿附近 ±1° 同分会在多次执行间乱跳）
        candidates.Sort(CompareCandidates);
        var radius = TemplateRadius();
        var result = new List<ShapeMatchInstance>();
        foreach (var c in candidates)
        {
            if (result.Count >= _numMatches) break;
            var suppressed = result.Any(r => CircleIou(r.X, r.Y, radius, c.X, c.Y, radius) > _maxOverlap);
            if (!suppressed)
            {
                // 亚像素细化：3 点抛物线插值分数面顶点（位置 ±1px、角度 ±1°），整数网格精度 → 亚像素
                var (ddx, ddy, dAngle) = SubPixelOffset(c.X, c.Y, c.Angle, dirX, dirY, mag, width, height);
                result.Add(new ShapeMatchInstance(c.X + ddx, c.Y + ddy, c.Angle + dAngle, c.Score));
            }
        }
        return result;
    }

    /// <summary>
    /// 亚像素偏移：对最终候选在 x±1 / y±1 / angle±1° 处重评分（minMatched=0 关闭早停，取真实分数），
    /// 三点抛物线顶点公式 δ = (s⁻−s⁺) / (2(s⁻−2s₀+s⁺))，分母过小（平台）时偏移取 0。
    /// </summary>
    private (double Dx, double Dy, double DAngle) SubPixelOffset(double x, double y, double angleDeg,
        float[][] dirX, float[][] dirY, float[][] mag, int[] width, int[] height)
    {
        var level = 0;
        var points = _template.LevelPoints[level];
        var (xs, ys, dxs, dys) = SplitPoints(points);
        var n = xs.Length;
        if (n == 0) return (0, 0, 0);
        var w = width[level];
        var ix = (int)x;
        var iy = (int)y;
        double S(int px, int py, double deg) =>
            ScoreAt(xs, ys, dxs, dys, n, dirX[level], dirY[level], mag[level], w, px, py, deg * Math.PI / 180.0, 0);
        var s0 = S(ix, iy, angleDeg);
        var sxm = S(ix - 1, iy, angleDeg);
        var sxp = S(ix + 1, iy, angleDeg);
        var sym = S(ix, iy - 1, angleDeg);
        var syp = S(ix, iy + 1, angleDeg);
        var sam = S(ix, iy, angleDeg - 1.0);
        var sap = S(ix, iy, angleDeg + 1.0);
        return (Vertex(sxm, s0, sxp), Vertex(sym, s0, syp), Vertex(sam, s0, sap));
    }

    private static double Vertex(double sMinus, double s0, double sPlus)
    {
        var den = sMinus - 2 * s0 + sPlus;
        if (Math.Abs(den) < 1e-9) return 0;
        return Math.Clamp(0.5 * (sMinus - sPlus) / den, -0.5, 0.5);
    }

    private List<(double X, double Y, double Angle, double Score)> SearchCoarsestLevel(
        float[][] dirX, float[][] dirY, float[][] mag, int[] width, int[] height, int level)
    {
        var points = _template.LevelPoints[level];
        var (xs, ys, dxs, dys) = SplitPoints(points);
        var n = xs.Length;
        var result = new List<(double X, double Y, double Angle, double Score)>();
        if (n == 0)
        {
            return result;
        }

        // 候选位置范围：基准点须落在层图像内即可；模板点越界按失配计（早停预算会快速剪枝）。
        // 不按旋转包络收缩范围——否则 ROI 裁剪图与模板尺寸接近时会无可搜位置，表现为"画了 ROI 模板就不生效"。
        var stride = Math.Min(width[level], height[level]) > 200 ? 2 : 1;
        var x0 = 1;
        var y0 = 1;
        var x1 = width[level] - 2;
        var y1 = height[level] - 2;
        if (x1 < x0 || y1 < y0)
        {
            return result;
        }

        var step = Math.Min(FineAngleStep * Math.Pow(2, level), MaxCoarseAngleStep);
        // 非原始层放宽 0.1：位置/角度量化会损失几个百分点，避免真值候选在粗层被阈值挡掉
        var minScoreLevel = LevelMinScore(level);
        var minMatched = (int)Math.Ceiling(minScoreLevel * n);
        for (var angleDeg = _angleStart; angleDeg <= _angleStart + _angleExtent + 1e-9; angleDeg += step)
        {
            var angleRad = angleDeg * Math.PI / 180.0;
            for (var py = y0; py <= y1; py += stride)
            {
                for (var px = x0; px <= x1; px += stride)
                {
                    var score = ScoreAt(xs, ys, dxs, dys, n, dirX[level], dirY[level], mag[level], width[level], px, py, angleRad, minMatched);
                    if (score >= minScoreLevel)
                    {
                        // 层坐标 → 原始分辨率坐标（×2^level，层级 0 = 原始分辨率）
                        result.Add((px * Math.Pow(2, level), py * Math.Pow(2, level), angleDeg, score));
                    }
                }
            }
        }
        // 粗层候选限流：按分数取前 32 个进入细化（同分按 |角度| 升序，保证确定性）
        result.Sort(CompareCandidates);
        return result.Take(32).ToList();
    }

    private static int CompareCandidates(
        (double X, double Y, double Angle, double Score) a, (double X, double Y, double Angle, double Score) b)
    {
        var byScore = b.Score.CompareTo(a.Score);
        return byScore != 0 ? byScore : Math.Abs(a.Angle).CompareTo(Math.Abs(b.Angle));
    }

    private List<(double X, double Y, double Angle, double Score)> RefineLevel(
        List<(double X, double Y, double Angle, double Score)> candidates, int level, double previousStep,
        float[][] dirX, float[][] dirY, float[][] mag, int[] width, int[] height)
    {
        var points = _template.LevelPoints[level];
        var (xs, ys, dxs, dys) = SplitPoints(points);
        var n = xs.Length;
        var result = new List<(double X, double Y, double Angle, double Score)>();
        if (n == 0)
        {
            return result;
        }

        var minScoreLevel = LevelMinScore(level);
        var minMatched = (int)Math.Ceiling(minScoreLevel * n);
        var step = FineAngleStep * Math.Pow(2, level);
        var scale = Math.Pow(2, level);
        foreach (var c in candidates)
        {
            // 候选为原始分辨率坐标 → 本层坐标（÷2^level）；邻域 ±2px、角度 ±上层步长
            var cx = c.X / scale;
            var cy = c.Y / scale;
            var bestScore = -1.0;
            var bestX = 0.0;
            var bestY = 0.0;
            var bestAngle = 0.0;
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    for (var angleDeg = c.Angle - previousStep; angleDeg <= c.Angle + previousStep + 1e-9; angleDeg += step)
                    {
                        var angleRad = angleDeg * Math.PI / 180.0;
                        var score = ScoreAt(xs, ys, dxs, dys, n, dirX[level], dirY[level], mag[level], width[level],
                            (int)Math.Round(cx) + dx, (int)Math.Round(cy) + dy, angleRad, minMatched);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = (int)Math.Round(cx) + dx;
                            bestY = (int)Math.Round(cy) + dy;
                            bestAngle = angleDeg;
                        }
                    }
                }
            }
            if (bestScore >= minScoreLevel)
            {
                // 本层最优位置 → 原始分辨率坐标（×2^level）
                result.Add((bestX * scale, bestY * scale, bestAngle, bestScore));
            }
        }
        return result;
    }

    /// <summary>层评分阈值：原始层用 minScore；上层放宽 0.1（量化损失补偿），最终结果仍由原始层把关。</summary>
    private double LevelMinScore(int level) =>
        level > 0 ? Math.Max(0.01, _minScore - 0.1) : _minScore;

    /// <summary>单候选评分：变换模板点 → 图像梯度方向比对。早停：剩余点全部命中也达不到 minMatched 时中断。</summary>
    internal double ScoreAt(
        float[] xs, float[] ys, float[] dxs, float[] dys, int n,
        float[] dirX, float[] dirY, float[] mag, int width,
        int px, int py, double angleRad, int minMatched)
    {
        var cos = (float)Math.Cos(angleRad);
        var sin = (float)Math.Sin(angleRad);
        var matched = 0;
        for (var i = 0; i < n; i++)
        {
            var rx = xs[i] * cos - ys[i] * sin;
            var ry = xs[i] * sin + ys[i] * cos;
            var ix = px + (int)Math.Round(rx);
            var iy = py + (int)Math.Round(ry);
            if (ix >= 0 && iy >= 0 && ix < width)
            {
                var idx = iy * width + ix;
                if (idx < mag.Length)
                {
                    var m = mag[idx];
                    if (m >= _minContrast)
                    {
                        var rdx = dxs[i] * cos - dys[i] * sin;
                        var rdy = dxs[i] * sin + dys[i] * cos;
                        var dot = rdx * dirX[idx] + rdy * dirY[idx];
                        if (_usePolarity ? dot >= _cosDirThreshold : Math.Abs(dot) >= _cosDirThreshold)
                        {
                            matched++;
                        }
                    }
                }
            }
            var remaining = n - i - 1;
            if (matched + remaining < minMatched)
            {
                return 0; // 早停：达不到最低命中数
            }
        }
        return n == 0 ? 0 : (double)matched / n;
    }

    internal (float[] Xs, float[] Ys, float[] Dxs, float[] Dys) SplitPoints(List<TemplatePoint> points)
    {
        var xs = new float[points.Count];
        var ys = new float[points.Count];
        var dxs = new float[points.Count];
        var dys = new float[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
            dxs[i] = points[i].Dx;
            dys[i] = points[i].Dy;
        }
        return (xs, ys, dxs, dys);
    }

    /// <summary>单层灰度图 → 归一化梯度方向 + 幅值（平滑 → Sobel）。</summary>
    internal static (float[] DirX, float[] DirY, float[] Mag) ExtractDirections(Mat gray, double sigma)
    {
        using var blurred = sigma > 0.01 ? new Mat() : null;
        if (blurred != null)
        {
            Cv2.GaussianBlur(gray, blurred, new Size(0, 0), sigma);
        }
        var src = blurred ?? gray;
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(src, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(src, gy, MatType.CV_32F, 0, 1, 3);
        if (!gx.GetArray(out float[] gxData) || !gy.GetArray(out float[] gyData))
        {
            return ([], [], []);
        }
        var count = gxData.Length;
        var dirX = new float[count];
        var dirY = new float[count];
        var mag = new float[count];
        for (var i = 0; i < count; i++)
        {
            var x = gxData[i];
            var y = gyData[i];
            var m = MathF.Sqrt(x * x + y * y);
            mag[i] = m;
            if (m > 1e-6f)
            {
                dirX[i] = x / m;
                dirY[i] = y / m;
            }
        }
        return (dirX, dirY, mag);
    }

    /// <summary>搜索图需要的层数：最粗层最短边 ≥~150px，且不超过模板层数。</summary>
    private static int LevelsForImage(int minSide, int templateLevels)
    {
        var l = 1;
        while (l < MaxLevels && l < templateLevels && (minSide >> l) >= 150)
        {
            l++;
        }
        return Math.Max(1, l);
    }

    private double TemplateRadius()
    {
        var radius = 10.0;
        if (_template.LevelPoints.Count == 0) return radius;
        foreach (var p in _template.LevelPoints[0])
        {
            radius = Math.Max(radius, Math.Sqrt(p.X * p.X + p.Y * p.Y));
        }
        return radius;
    }

    /// <summary>两圆的 IoU（NMS 用，一般公式）。</summary>
    internal static double CircleIou(double x1, double y1, double r1, double x2, double y2, double r2)
    {
        var d = Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
        if (d >= r1 + r2) return 0;
        if (d <= Math.Abs(r1 - r2)) return 1;
        var a1 = r1 * r1 * Math.Acos(Math.Clamp((d * d + r1 * r1 - r2 * r2) / (2 * d * r1), -1, 1));
        var a2 = r2 * r2 * Math.Acos(Math.Clamp((d * d + r2 * r2 - r1 * r1) / (2 * d * r2), -1, 1));
        var tri = 0.5 * Math.Sqrt(Math.Max(0, (-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2)));
        var area = a1 + a2 - tri;
        var union = Math.PI * r1 * r1 + Math.PI * r2 * r2 - area;
        return union <= 0 ? 0 : area / union;
    }
}
