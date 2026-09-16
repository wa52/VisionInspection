using System.Globalization;

namespace VisionInspection.Detection;

/// <summary>
/// 归一化 ROI（中心点 + 宽高 + 旋转角，0~1 相对坐标，抗图像分辨率变化）。
/// 序列化格式 "cx,cy,w,h,angle"（angle 度，屏幕顺时针为正，y 向下坐标系）；
/// 兼容旧 4 值格式 "x,y,w,h"（左上角 + 无旋转）。
/// 只支持单个检测区域：为空表示全图检测。
/// </summary>
public readonly record struct RoiRect(double CenterX, double CenterY, double W, double H, double AngleDeg)
{
    /// <summary>解析；空串/格式错/数值非法返回 null（回退全图检测）。</summary>
    public static RoiRect? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',');
        if (parts.Length is not (4 or 5)) return null;
        var v = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
            {
                return null;
            }
        }

        if (v.Any(d => double.IsNaN(d) || double.IsInfinity(d))) return null;

        if (parts.Length == 4)
        {
            // 旧格式：左上角 x,y + 宽高（无旋转）
            if (v[0] < 0 || v[1] < 0 || v[2] <= 0 || v[3] <= 0) return null;
            var x = Math.Min(v[0], 1.0);
            var y = Math.Min(v[1], 1.0);
            var w = Math.Min(v[2], 1.0 - x);
            var h = Math.Min(v[3], 1.0 - y);
            if (w <= 0 || h <= 0) return null;
            return new RoiRect(x + w / 2, y + h / 2, w, h, 0);
        }

        // 新格式：中心 cx,cy + 宽高 + 角度
        if (v[2] <= 0 || v[3] <= 0) return null;
        var cx = Math.Clamp(v[0], 0.0, 1.0);
        var cy = Math.Clamp(v[1], 0.0, 1.0);
        var nw = Math.Min(v[2], 1.0);
        var nh = Math.Min(v[3], 1.0);
        return new RoiRect(cx, cy, nw, nh, NormalizeAngle(v[4]));
    }

    public string Serialize() =>
        $"{CenterX.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{CenterY.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{W.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{H.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{AngleDeg.ToString("0.##", CultureInfo.InvariantCulture)}";

    /// <summary>角度归一化到 (-180, 180]。</summary>
    public static double NormalizeAngle(double deg)
    {
        var a = deg % 360;
        if (a <= -180) a += 360;
        else if (a > 180) a -= 360;
        return a;
    }

    /// <summary>
    /// 归一化 → 像素 ROI（中心 + 宽高 + 角度）。
    /// 按旋转外接框钳制中心，保证整个 ROI 尽量落在图像内；宽高至少 2px。
    /// </summary>
    public (double Cx, double Cy, int W, int H, double Angle) ToPixels(int imgW, int imgH)
    {
        var w = Math.Clamp((int)Math.Round(W * imgW), 2, imgW);
        var h = Math.Clamp((int)Math.Round(H * imgH), 2, imgH);
        var a = AngleDeg * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(a));
        var sin = Math.Abs(Math.Sin(a));
        var aabbW = w * cos + h * sin;
        var aabbH = w * sin + h * cos;
        var cx = Math.Round(CenterX * imgW);
        var cy = Math.Round(CenterY * imgH);
        cx = aabbW >= imgW ? imgW / 2.0 : Math.Clamp(cx, aabbW / 2, imgW - aabbW / 2);
        cy = aabbH >= imgH ? imgH / 2.0 : Math.Clamp(cy, aabbH / 2, imgH - aabbH / 2);
        return (cx, cy, w, h, NormalizeAngle(AngleDeg));
    }

    /// <summary>由像素中心矩形构造（UI 拖拽结果）；宽高钳制到图像内且不小于 2px，无效返回 null。</summary>
    public static RoiRect? FromPixelsCenter(double cx, double cy, double w, double h, double angle, int imgW, int imgH)
    {
        if (imgW <= 0 || imgH <= 0) return null;
        if (w < 2 || h < 2) return null;
        w = Math.Clamp(w, 2, imgW);
        h = Math.Clamp(h, 2, imgH);
        var a = NormalizeAngle(angle) * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(a));
        var sin = Math.Abs(Math.Sin(a));
        var aabbW = w * cos + h * sin;
        var aabbH = w * sin + h * cos;
        cx = aabbW >= imgW ? imgW / 2.0 : Math.Clamp(cx, aabbW / 2, imgW - aabbW / 2);
        cy = aabbH >= imgH ? imgH / 2.0 : Math.Clamp(cy, aabbH / 2, imgH - aabbH / 2);
        return new RoiRect(cx / imgW, cy / imgH, w / imgW, h / imgH, NormalizeAngle(angle));
    }
}

/// <summary>ROI 拖拽命中部位（8 手柄 + 主体移动 + 旋转柄 + 圆环内外半径柄）。</summary>
public enum RoiHandle
{
    None,
    Body,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Rotate,
    RingOuter,
    RingInner,
}

/// <summary>
/// 图像等比缩放（Uniform letterbox）坐标换算 + ROI 编辑几何（纯逻辑，可单测）。
/// 旋转约定：y 向下屏幕坐标系，角度正 = 屏幕顺时针；R(a) = [cos,-sin; sin,cos] 把局部向量转到图像向量。
/// </summary>
public static class RoiGeometry
{
    /// <summary>等比适配参数：scale = 缩放比，(Ox,Oy) = 图像在显示区内的留白偏移。</summary>
    public static (double Scale, double Ox, double Oy) UniformFit(int imgW, int imgH, double dispW, double dispH)
    {
        if (imgW <= 0 || imgH <= 0 || dispW <= 0 || dispH <= 0) return (1, 0, 0);
        var scale = Math.Min(dispW / imgW, dispH / imgH);
        return (scale, (dispW - imgW * scale) / 2, (dispH - imgH * scale) / 2);
    }

    /// <summary>显示坐标 → 图像像素坐标（拖拽取点用）。</summary>
    public static (double X, double Y) DisplayToImage(double dx, double dy, int imgW, int imgH, double dispW, double dispH)
    {
        var (scale, ox, oy) = UniformFit(imgW, imgH, dispW, dispH);
        if (scale <= 0) return (0, 0);
        return ((dx - ox) / scale, (dy - oy) / scale);
    }

    /// <summary>图像像素矩形 → 显示区矩形（叠加层摆放用）。</summary>
    public static (double X, double Y, double W, double H) ImageToDisplay(
        int x, int y, int w, int h, int imgW, int imgH, double dispW, double dispH)
    {
        var (scale, ox, oy) = UniformFit(imgW, imgH, dispW, dispH);
        return (ox + x * scale, oy + y * scale, w * scale, h * scale);
    }

    /// <summary>图像坐标 → ROI 局部坐标（原点=ROI 中心，x 右 y 下，随角度反转）。</summary>
    public static (double Lx, double Ly) ToLocal(double x, double y, double cx, double cy, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var dx = x - cx;
        var dy = y - cy;
        return (cos * dx + sin * dy, -sin * dx + cos * dy);
    }

    /// <summary>ROI 局部坐标 → 图像坐标（ToLocal 的逆变换）。</summary>
    public static (double X, double Y) ToImage(double lx, double ly, double cx, double cy, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        return (cx + cos * lx - sin * ly, cy + sin * lx + cos * ly);
    }

    /// <summary>命中测试（ROI 局部坐标系，矩形 = (-w/2,-h/2,w,h)）。tol 单位与坐标一致。</summary>
    public static RoiHandle HitTest(double rx, double ry, double rw, double rh, double px, double py, double tol)
    {
        var nearL = Math.Abs(px - rx) <= tol;
        var nearR = Math.Abs(px - (rx + rw)) <= tol;
        var nearT = Math.Abs(py - ry) <= tol;
        var nearB = Math.Abs(py - (ry + rh)) <= tol;
        var inX = px >= rx - tol && px <= rx + rw + tol;
        var inY = py >= ry - tol && py <= ry + rh + tol;
        if (!inX || !inY) return RoiHandle.None;

        if (nearL && nearT) return RoiHandle.TopLeft;
        if (nearR && nearT) return RoiHandle.TopRight;
        if (nearL && nearB) return RoiHandle.BottomLeft;
        if (nearR && nearB) return RoiHandle.BottomRight;
        if (nearL) return RoiHandle.Left;
        if (nearR) return RoiHandle.Right;
        if (nearT) return RoiHandle.Top;
        if (nearB) return RoiHandle.Bottom;
        return RoiHandle.Body;
    }

    /// <summary>
    /// 旋转 ROI 的手柄缩放（局部坐标系）：返回新宽高 + 新中心相对原中心的局部偏移。
    /// lx/ly = 当前鼠标在原 ROI 局部系中的坐标；宽高钳制到 [minSize, max]。
    /// </summary>
    public static (double W, double H, double LocalCx, double LocalCy) ApplyRotatedEdgeDrag(
        RoiHandle handle, double w, double h, double lx, double ly, double minSize, double maxSize)
    {
        var l = -w / 2;
        var t = -h / 2;
        var r = w / 2;
        var b = h / 2;

        if (handle is RoiHandle.Left or RoiHandle.TopLeft or RoiHandle.BottomLeft)
        {
            l = Math.Clamp(lx, r - maxSize, r - minSize);
        }
        else if (handle is RoiHandle.Right or RoiHandle.TopRight or RoiHandle.BottomRight)
        {
            r = Math.Clamp(lx, l + minSize, l + maxSize);
        }

        if (handle is RoiHandle.Top or RoiHandle.TopLeft or RoiHandle.TopRight)
        {
            t = Math.Clamp(ly, b - maxSize, b - minSize);
        }
        else if (handle is RoiHandle.Bottom or RoiHandle.BottomLeft or RoiHandle.BottomRight)
        {
            b = Math.Clamp(ly, t + minSize, t + maxSize);
        }

        return (r - l, b - t, (l + r) / 2, (t + b) / 2);
    }
}
