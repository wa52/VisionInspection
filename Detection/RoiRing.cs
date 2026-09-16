using System.Globalization;

namespace VisionInspection.Detection;

/// <summary>
/// 归一化圆环 ROI（圆查找节点搜索区域）：圆心 + 外半径 + 内半径。
/// 归一化约定：cx 按图像宽、cy 按图像高（同 RoiRect），半径按图像宽（保证像素圆各向同性）。
/// 序列化格式 "cx,cy,ro,ri"（ro>ri>0）；own_rings 条目内与名字以 ';' 分隔（own_rois 矩形集不受影响）。
/// </summary>
public readonly record struct RoiRing(double CenterX, double CenterY, double ROuter, double RInner)
{
    /// <summary>解析；空串/格式错/数值非法（ro≤0 或 ri<0 或 ri≥ro）返回 null。</summary>
    public static RoiRing? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',');
        if (parts.Length != 4) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
            {
                return null;
            }
        }
        if (v.Any(d => double.IsNaN(d) || double.IsInfinity(d))) return null;
        if (v[2] <= 0 || v[3] < 0 || v[3] >= v[2]) return null;
        return new RoiRing(
            Math.Clamp(v[0], 0.0, 1.0),
            Math.Clamp(v[1], 0.0, 1.0),
            Math.Min(v[2], 1.0),
            Math.Min(v[3], v[2]));
    }

    public string Serialize() =>
        $"{CenterX.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{CenterY.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{ROuter.ToString("0.####", CultureInfo.InvariantCulture)}," +
        $"{RInner.ToString("0.####", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 归一化 → 像素圆环（圆心 + 外/内半径，半径按图像宽换算）。
    /// 圆心钳制保证外圆尽量落在图像内；半径钳制到 [4, 图像宽]，且 ro−ri ≥ 4px（剖面采样下限）。
    /// </summary>
    public (double Cx, double Cy, double Ro, double Ri) ToPixels(int imgW, int imgH)
    {
        var ro = Math.Clamp(ROuter * imgW, 4.0, imgW);
        var ri = Math.Clamp(RInner * imgW, 0.0, Math.Max(0.0, ro - 4.0));
        var cx = Math.Clamp(Math.Round(CenterX * imgW), ro / 2, Math.Max(ro / 2, imgW - ro / 2));
        var cy = Math.Clamp(Math.Round(CenterY * imgH), ro / 2, Math.Max(ro / 2, imgH - ro / 2));
        return (cx, cy, ro, ri);
    }

    /// <summary>由像素圆环构造（UI 拖拽结果）；ro 或环宽不足 4px 返回 null。</summary>
    public static RoiRing? FromPixelsCenter(double cx, double cy, double ro, double ri, int imgW, int imgH)
    {
        if (imgW <= 0 || imgH <= 0) return null;
        if (ro < 4) return null;
        if (ri < 0) ri = 0;
        if (ro - ri < 4) return null;
        ro = Math.Clamp(ro, 4, imgW);
        ri = Math.Clamp(ri, 0, ro - 4);
        return new RoiRing(
            Math.Clamp(cx / imgW, 0.0, 1.0),
            Math.Clamp(cy / imgH, 0.0, 1.0),
            ro / imgW,
            ri / imgW);
    }
}
