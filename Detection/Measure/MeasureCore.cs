namespace VisionInspection.Detection;

/// <summary>
/// 测量用直线：线段两端点（像素坐标）。垂距/夹角/交点等计算按无限直线处理（VM「贯穿线」语义），
/// 端点仅用于 线线测量 的 4 端点平均垂距与结果回显。
/// </summary>
public readonly record struct MeasureLine(double X1, double Y1, double X2, double Y2)
{
    public double Dx => X2 - X1;
    public double Dy => Y2 - Y1;
    public double Length => Math.Sqrt(Dx * Dx + Dy * Dy);
}

/// <summary>测量用圆（圆心 + 半径，像素）。</summary>
public readonly record struct MeasureCircle(double Cx, double Cy, double R);

/// <summary>输出角度范围（VM OutputAngleRange）：-90°~90°（锐角折叠）/ -180°~180°（线性）。</summary>
public static class MeasureAngleRange
{
    public const string Segment = "-90°~90°";
    public const string Linear = "-180°~180°";
}

/// <summary>两圆位置关系（VM C2CType 的中文化输出：Inside内含/Inscribe内切/Intersect相交/Circumscribe外切/Outside外离）。</summary>
public static class CircleRelation
{
    public const string Inside = "内含";
    public const string Inscribe = "内切";
    public const string Intersect = "相交";
    public const string Circumscribe = "外切";
    public const string Outside = "外离";
}

/// <summary>
/// 几何测量纯逻辑核（VM 线线/线圆/圆圆/点圆测量的距离与角度语义，图像坐标系 y 向下）。
/// 角度符号约定（对齐 VM 用户手册，逐模块不同）：
/// 线线夹角 = 从直线1到直线2方向顺时针为正；线圆角度 = 垂足→圆心向量指向图像 y+ 为正；
/// 圆圆/点圆角度 = 连线位于水平线下方为负、上方为正（视觉上方为正）。
/// </summary>
public static class MeasureCore
{
    /// <summary>点到直线的垂直距离（直线退化时返回 0）。</summary>
    public static double PointLineDistance(double px, double py, MeasureLine line)
    {
        var len = line.Length;
        if (len < 1e-12) return 0;
        var cross = (px - line.X1) * line.Dy - (py - line.Y1) * line.Dx;
        return Math.Abs(cross) / len;
    }

    /// <summary>点到直线的垂足（直线退化时返回线起点）。</summary>
    public static (double X, double Y) PointLineFoot(double px, double py, MeasureLine line)
    {
        var len2 = line.Dx * line.Dx + line.Dy * line.Dy;
        if (len2 < 1e-24) return (line.X1, line.Y1);
        var t = ((px - line.X1) * line.Dx + (py - line.Y1) * line.Dy) / len2;
        return (line.X1 + t * line.Dx, line.Y1 + t * line.Dy);
    }

    /// <summary>直线方向角（atan2，度；图像 y 向下）。直线退化返回 0。</summary>
    public static double DirectionAngleDeg(MeasureLine line)
    {
        if (line.Length < 1e-12) return 0;
        return Math.Atan2(line.Dy, line.Dx) * 180.0 / Math.PI;
    }

    /// <summary>两无限直线交点；平行/共线（行列式≈0）返回 null。</summary>
    public static (double X, double Y)? LineLineIntersection(MeasureLine a, MeasureLine b)
    {
        var det = a.Dx * b.Dy - a.Dy * b.Dx;
        var scale = Math.Max(1e-12, a.Length * b.Length);
        if (Math.Abs(det) < 1e-9 * scale) return null;
        var t = ((b.X1 - a.X1) * b.Dy - (b.Y1 - a.Y1) * b.Dx) / det;
        return (a.X1 + t * a.Dx, a.Y1 + t * a.Dy);
    }

    /// <summary>角度归一化：Linear → (-180,180]；Segment → 折叠到 (-90,90]（其余/未知范围按 Linear）。</summary>
    public static double NormalizeAngle(double deg, string range)
    {
        var x = deg - 360.0 * Math.Round(deg / 360.0); // [-180, 180]
        if (x <= -180) x += 360;                        // (-180, 180]
        if (string.Equals(range, MeasureAngleRange.Segment, StringComparison.Ordinal))
        {
            if (x > 90) x -= 180;
            else if (x <= -90) x += 180;
        }
        return x;
    }

    // ===== 线线测量 =====

    /// <summary>
    /// 线线绝对距离（VM L2LAbsDist 语义）：两条直线的起点+终点共 4 个点，
    /// 各自到对面直线的垂直距离的平均值（平行线即垂距，非平行也按此定义）。
    /// </summary>
    public static double L2LAbsDistance(MeasureLine a, MeasureLine b)
    {
        return (PointLineDistance(a.X1, a.Y1, b)
              + PointLineDistance(a.X2, a.Y2, b)
              + PointLineDistance(b.X1, b.Y1, a)
              + PointLineDistance(b.X2, b.Y2, a)) / 4.0;
    }

    /// <summary>
    /// 线线夹角（VM L2LAngle 语义）：从直线1方向到直线2方向，视觉顺时针为正（图像 y 向下坐标系下 Δ=θ2−θ1 天然满足）。
    /// Segment 范围输出锐角折叠 [-90,90]，Linear 范围输出原始带符号差 (-180,180]。
    /// </summary>
    public static double L2LAngle(MeasureLine a, MeasureLine b, string range)
    {
        var delta = DirectionAngleDeg(b) - DirectionAngleDeg(a);
        return NormalizeAngle(delta, range);
    }

    // ===== 线圆测量 =====

    /// <summary>直线与圆的交点（0/1/2 个；相切 1 个）。</summary>
    public static List<(double X, double Y)> LineCircleIntersections(MeasureLine line, MeasureCircle c)
    {
        var result = new List<(double X, double Y)>();
        var len2 = line.Dx * line.Dx + line.Dy * line.Dy;
        if (len2 < 1e-24 || c.R < 0) return result;
        var fx = line.X1 - c.Cx;
        var fy = line.Y1 - c.Cy;
        var halfB = (fx * line.Dx + fy * line.Dy) / len2;          // (u·f)/|u|²
        var disc = halfB * halfB - (fx * fx + fy * fy - c.R * c.R) / len2;
        var eps = 1e-9 * (1.0 + c.R + line.Length);
        if (disc < -eps) return result;
        var root = Math.Sqrt(Math.Max(0, disc));
        if (disc <= eps)
        {
            var t = -halfB;
            result.Add((line.X1 + t * line.Dx, line.Y1 + t * line.Dy));
            return result;
        }
        result.Add((line.X1 + (-halfB - root) * line.Dx, line.Y1 + (-halfB - root) * line.Dy));
        result.Add((line.X1 + (-halfB + root) * line.Dx, line.Y1 + (-halfB + root) * line.Dy));
        return result;
    }

    /// <summary>
    /// 线圆角度（VM L2C Angle 语义）：垂足→圆心向量与 x 轴正半轴的夹角，
    /// 向量指向图像 y+（向下）为正（VM 手册「指向 y 轴正方向为正值」）。
    /// </summary>
    public static double L2CAngle(double footX, double footY, double cx, double cy, string range)
    {
        return NormalizeAngle(Math.Atan2(cy - footY, cx - footX) * 180.0 / Math.PI, range);
    }

    // ===== 圆圆测量 =====

    /// <summary>
    /// 两圆位置关系：内含/内切/相交/外切/外离（等半径不判内切；等半径分离判相交、重合判内含）。
    /// eps 为像素容差（输入几何来自两位小数解析，1e-6 足够）。
    /// </summary>
    public static string CircleCircleRelation(MeasureCircle a, MeasureCircle b, double eps = 1e-6)
    {
        var d = Math.Sqrt((b.Cx - a.Cx) * (b.Cx - a.Cx) + (b.Cy - a.Cy) * (b.Cy - a.Cy));
        var rd = Math.Abs(a.R - b.R);
        if (d >= a.R + b.R - eps) return d > a.R + b.R + eps ? CircleRelation.Outside : CircleRelation.Circumscribe;
        if (rd <= eps) return d <= eps ? CircleRelation.Inside : CircleRelation.Intersect;
        if (d <= rd - eps) return CircleRelation.Inside;
        if (d <= rd + eps) return CircleRelation.Inscribe;
        return CircleRelation.Intersect;
    }

    /// <summary>两圆交点（0/1/2 个；内切/外切 1 个；内含/外离 0 个）。</summary>
    public static List<(double X, double Y)> CircleCircleIntersections(MeasureCircle a, MeasureCircle b)
    {
        var result = new List<(double X, double Y)>();
        var dx = b.Cx - a.Cx;
        var dy = b.Cy - a.Cy;
        var d = Math.Sqrt(dx * dx + dy * dy);
        var eps = 1e-6;
        if (d <= eps || d > a.R + b.R + eps || d < Math.Abs(a.R - b.R) - eps) return result;
        var t = (d * d + a.R * a.R - b.R * b.R) / (2 * d);
        var h2 = a.R * a.R - t * t;
        if (h2 < 0)
        {
            if (h2 < -eps * (1 + a.R)) return result;
            h2 = 0;
        }
        var h = Math.Sqrt(h2);
        var ux = dx / d;
        var uy = dy / d;
        var bx = a.Cx + t * ux;
        var by = a.Cy + t * uy;
        result.Add((bx + h * (-uy), by + h * ux));
        if (h > eps)
        {
            result.Add((bx - h * (-uy), by - h * ux));
        }
        return result;
    }

    /// <summary>
    /// 圆圆角度（VM C2C Angle 语义）：圆心1→圆心2 连线与水平线的夹角，连线位于水平线下方为负、上方为正
    /// （图像 y 向下，故取 −atan2）。Linear 范围按向量方向、Segment 范围两方向等价。
    /// </summary>
    public static double C2CAngle(double c1x, double c1y, double c2x, double c2y, string range)
    {
        return NormalizeAngle(-Math.Atan2(c2y - c1y, c2x - c1x) * 180.0 / Math.PI, range);
    }

    // ===== 点圆测量 =====

    /// <summary>点圆三距离：中心距离=|点−圆心|；最近距离=中心距离−半径；最远距离=中心距离+半径（点在圆内时最近为负）。</summary>
    public static (double Center, double Closest, double Farthest) P2CDistances(double px, double py, MeasureCircle c)
    {
        var center = Math.Sqrt((px - c.Cx) * (px - c.Cx) + (py - c.Cy) * (py - c.Cy));
        return (center, center - c.R, center + c.R);
    }

    /// <summary>
    /// 点圆角度（VM P2C Angle 语义）：点与圆心连线与水平线的夹角，连线位于水平线下方为负、上方为正
    /// （取 圆心→点 向量，图像 y 向下，故取 −atan2）。
    /// </summary>
    public static double P2CAngle(double px, double py, double cx, double cy, string range)
    {
        return NormalizeAngle(-Math.Atan2(py - cy, px - cx) * 180.0 / Math.PI, range);
    }

    // ===== 判定限 =====

    /// <summary>判定限（VM 限值语义）：未启用恒过；启用后值须落在 [low, high]（含边界）。</summary>
    public static bool CheckLimit(bool enable, double low, double high, double value)
    {
        if (!enable) return true;
        return value >= low && value <= high;
    }
}
