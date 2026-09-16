using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>检测项级元数据：启用开关 / 判断(空=参与判定，仅观察=只记录不判NG) / 阈值(检测项级，节点不再有总阈值) / 是否存图 / 目标字符(字符识别节点用，空=不做文本比对)。</summary>
public sealed record RoiMeta(bool Enabled = true, string Judge = "", string Threshold = "", bool SaveImage = true, string Target = "")
{
    /// <summary>序列化字符串（全默认返回空串，保持旧格式干净）。</summary>
    public string MetaString()
    {
        if (IsDefault) return "";
        var parts = new List<string>();
        if (!Enabled) parts.Add("e=0");
        if (Judge.Length > 0) parts.Add("d=" + Judge);
        if (Threshold.Length > 0) parts.Add("t=" + Threshold);
        if (!SaveImage) parts.Add("s=0");
        // 目标字符是任意文本，可能含 , ; | = 等分隔符 → URL 转义后存取
        if (Target.Length > 0) parts.Add("x=" + Uri.EscapeDataString(Target));
        return string.Join(",", parts);
    }

    public bool IsDefault => Enabled && Judge.Length == 0 && Threshold.Length == 0 && SaveImage && Target.Length == 0;

    /// <summary>阈值数值（空/非法返回 null）。</summary>
    public double? ThresholdValue => double.TryParse(Threshold, out var v) ? v : null;
}

/// <summary>own_rois 条目：几何 + 检测项元数据。</summary>
public sealed record RoiItem(string Name, RoiRect Rect, RoiMeta Meta);

/// <summary>
/// 节点私有 ROI 集合的解析/序列化（方案级 ROI 库已移除，所有 ROI 都画在本节点输出图上）。
/// 序列化格式（条目间用 '|' 分隔）：
/// 旧格式 "name;cx,cy,w,h,angle"；新格式追加第 3 段元数据 "name;cx,cy,w,h,angle;e=1,d=仅观察,t=0.6,s=0,x=ABC"
/// （e=启用 d=判断 t=阈值 s=存图 x=目标字符(URL转义)；缺省段=全默认，旧配方零迁移兼容）。
/// </summary>
public static class NodeRois
{
    /// <summary>生成源图坐标系下的精确旋转矩形 mask（255=ROI 内）。调用方负责释放。</summary>
    public static Mat CreateMask(RoiRect roi, int imgW, int imgH)
    {
        var mask = new Mat(imgH, imgW, MatType.CV_8UC1, Scalar.All(0));
        var (cx, cy, w, h, angle) = roi.ToPixels(imgW, imgH);
        var rad = angle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var points = new[] { (-w / 2.0, -h / 2.0), (w / 2.0, -h / 2.0), (w / 2.0, h / 2.0), (-w / 2.0, h / 2.0) }
            .Select(p => new Point(
                (int)Math.Round(cx + p.Item1 * cos - p.Item2 * sin),
                (int)Math.Round(cy + p.Item1 * sin + p.Item2 * cos)))
            .ToArray();
        Cv2.FillConvexPoly(mask, points, Scalar.All(255));
        return mask;
    }

    /// <summary>判断轴对齐像素框是否与旋转 ROI 相交。</summary>
    public static bool IntersectsPixelBox(RoiRect roi, double x, double y, double width, double height, int imgW, int imgH)
    {
        if (width < 0) { x += width; width = -width; }
        if (height < 0) { y += height; height = -height; }
        var box = new[]
        {
            new Point2d(x, y), new Point2d(x + width, y),
            new Point2d(x + width, y + height), new Point2d(x, y + height)
        };
        var polygon = RoiPolygon(roi, imgW, imgH);
        if (box.Any(p => PointInPolygon(p, polygon)) || polygon.Any(p => PointInPolygon(p, box))) return true;
        for (var i = 0; i < 4; i++)
        {
            if (SegmentsIntersect(box[i], box[(i + 1) % 4], polygon[i], polygon[(i + 1) % 4])) return true;
        }
        return false;
    }

    private static Point2d[] RoiPolygon(RoiRect roi, int imgW, int imgH)
    {
        var (cx, cy, w, h, angle) = roi.ToPixels(imgW, imgH);
        var rad = angle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return new[] { (-w / 2.0, -h / 2.0), (w / 2.0, -h / 2.0), (w / 2.0, h / 2.0), (-w / 2.0, h / 2.0) }
            .Select(p => new Point2d(cx + p.Item1 * cos - p.Item2 * sin, cy + p.Item1 * sin + p.Item2 * cos))
            .ToArray();
    }

    private static bool PointInPolygon(Point2d point, IReadOnlyList<Point2d> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsIntersect(Point2d a, Point2d b, Point2d c, Point2d d)
    {
        static double Cross(Point2d p, Point2d q, Point2d r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return ((abC >= 0 && abD <= 0) || (abC <= 0 && abD >= 0)) &&
               ((cdA >= 0 && cdB <= 0) || (cdA <= 0 && cdB >= 0));
    }

    /// <summary>解析 own_rois 参数（几何投影）；格式非法的条目跳过。</summary>
    public static List<(string Name, RoiRect Rect)> ParseOwn(string? raw) =>
        ParseOwnFull(raw).Select(i => (i.Name, i.Rect)).ToList();

    public static string SerializeOwn(IEnumerable<(string Name, RoiRect Rect)> rois) =>
        SerializeOwnFull(rois.Select(r => new RoiItem(r.Name, r.Rect, new RoiMeta())));

    /// <summary>解析 own_rois 参数（含检测项元数据）；非法条目跳过；元数据段缺省=全默认。</summary>
    public static List<RoiItem> ParseOwnFull(string? raw)
    {
        var result = new List<RoiItem>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var entry in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(';');
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var rect = RoiRect.Parse(parts[1]);
            if (name.Length == 0 || rect is null) continue;
            result.Add(new RoiItem(name, rect.Value, parts.Length > 2 ? ParseMeta(parts[2]) : new RoiMeta()));
        }
        return result;
    }

    public static string SerializeOwnFull(IEnumerable<RoiItem> items) =>
        string.Join("|", items.Select(i =>
        {
            var meta = i.Meta.MetaString();
            return meta.Length == 0 ? $"{i.Name};{i.Rect.Serialize()}" : $"{i.Name};{i.Rect.Serialize()};{meta}";
        }));

    private static RoiMeta ParseMeta(string s)
    {
        var meta = new RoiMeta();
        foreach (var kv in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var key = kv[..eq].Trim();
            var value = kv[(eq + 1)..].Trim();
            switch (key)
            {
                case "e": meta = meta with { Enabled = value != "0" }; break;
                case "d": meta = meta with { Judge = value }; break;
                case "t": meta = meta with { Threshold = value }; break;
                case "s": meta = meta with { SaveImage = value != "0" }; break;
                case "x": meta = meta with { Target = Uri.UnescapeDataString(value) }; break;
            }
        }
        return meta;
    }

    /// <summary>检测项是否参与判定（启用 且 判断≠仅观察）。</summary>
    public static bool Judges(RoiMeta meta) =>
        meta.Enabled && !string.Equals(meta.Judge, "仅观察", StringComparison.Ordinal);

    /// <summary>own 集合内取唯一名。</summary>
    public static string UniqueOwnName(IEnumerable<(string Name, RoiRect Rect)> rois, string baseName)
    {
        var text = baseName;
        var i = 2;
        while (rois.Any(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase)))
        {
            text = baseName + "_" + i++;
        }
        return text;
    }

    /// <summary>解析「总范围」ROI 索引（scope_index 参数；-1/非法 = 未设置）。</summary>
    public static int ParseScopeIndex(IReadOnlyDictionary<string, string>? p) =>
        p is not null && int.TryParse(p.GetValueOrDefault("scope_index"), out var v) && v >= 0 ? v : -1;

    /// <summary>旋转矩形（归一化）是否包含源图像素点（总范围约束判定用）。</summary>
    public static bool ContainsPixel(RoiRect rect, double px, double py, int imgW, int imgH)
    {
        var (cx, cy, w, h, angle) = rect.ToPixels(imgW, imgH);
        var rad = -angle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = px - cx;
        var dy = py - cy;
        var lx = dx * cos - dy * sin;
        var ly = dx * sin + dy * cos;
        return Math.Abs(lx) <= w / 2.0 && Math.Abs(ly) <= h / 2.0;
    }

    /// <summary>
    /// 应用位置修正（纯函数）：本节点在修正目标列表中时，把 ROI 从基准位姿坐标系映射到当前位姿
    /// p' = R(Δθ)·S·(p − P0) + P1（P0=基准定位点，P1=当前定位点，Δθ=当前角−基准角，
    /// S=diag(ScaleX,ScaleY) 方向尺度，图像坐标系 y 向下）。
    /// ROI 尺寸按 (ScaleX, ScaleY) 缩放、角度加 Δθ（各向异性尺度+旋转的耦合近似忽略，尺度默认 1 时精确）。
    /// correction 为 null 或本节点不在目标列表 → 原样返回。
    /// </summary>
    public static List<(string Name, RoiRect Rect)> ApplyPoseCorrection(
        List<(string Name, RoiRect Rect)> rois, string nodeName, int imgW, int imgH, PoseCorrection? correction)
    {
        if (correction is null || rois.Count == 0) return rois;
        var targeted = correction.TargetNodes.Any(t => string.Equals(t, nodeName, StringComparison.OrdinalIgnoreCase));
        if (!targeted) return rois;

        var deltaAngle = correction.CurAngle - correction.RefAngle;
        var rad = deltaAngle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var result = new List<(string, RoiRect)>(rois.Count);
        foreach (var (name, rect) in rois)
        {
            var (cx, cy, w, h, angle) = rect.ToPixels(imgW, imgH);
            var lx = (cx - correction.RefX) * correction.ScaleX;
            var ly = (cy - correction.RefY) * correction.ScaleY;
            var nx = correction.CurX + lx * cos - ly * sin;
            var ny = correction.CurY + lx * sin + ly * cos;
            var mapped = RoiRect.FromPixelsCenter(
                nx, ny, w * correction.ScaleX, h * correction.ScaleY, angle + deltaAngle, imgW, imgH);
            result.Add((name, mapped ?? rect));
        }
        return result;
    }
}

/// <summary>
/// 节点私有圆环集（own_rings 参数，圆查找节点搜索区域专用；own_rois 矩形检测项集不受影响）。
/// 条目格式 "名字;cx,cy,ro,ri"（条目间 '|' 分隔，与 own_rois 同风格；半径按图像宽归一化）。
/// </summary>
public static class NodeRings
{
    /// <summary>解析 own_rings 参数；格式非法的条目跳过。</summary>
    public static List<(string Name, RoiRing Ring)> Parse(string? raw)
    {
        var result = new List<(string, RoiRing)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var entry in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(';');
            if (parts.Length != 2) continue;
            var name = parts[0].Trim();
            var ring = RoiRing.Parse(parts[1]);
            if (name.Length == 0 || ring is null) continue;
            result.Add((name, ring.Value));
        }
        return result;
    }

    public static string Serialize(IEnumerable<(string Name, RoiRing Ring)> rings) =>
        string.Join("|", rings.Select(r => $"{r.Name};{r.Ring.Serialize()}"));

    /// <summary>own_rings 集合内取唯一名。</summary>
    public static string UniqueName(IEnumerable<(string Name, RoiRing Ring)> rings, string baseName)
    {
        var text = baseName;
        var i = 2;
        while (rings.Any(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase)))
        {
            text = baseName + "_" + i++;
        }
        return text;
    }

    /// <summary>
    /// 应用位置修正（纯函数，镜像 NodeRois.ApplyPoseCorrection）：圆心做 p' = R(Δθ)·S·(p − P0) + P1，
    /// 半径按 (ScaleX+ScaleY)/2 缩放（各向异性尺度近似，同矩形旋转+尺度耦合的近似级别）。
    /// correction 为 null 或本节点不在目标列表 → 原样返回。
    /// </summary>
    public static List<(string Name, RoiRing Ring)> ApplyPoseCorrection(
        List<(string Name, RoiRing Ring)> rings, string nodeName, int imgW, int imgH, PoseCorrection? correction)
    {
        if (correction is null || rings.Count == 0) return rings;
        var targeted = correction.TargetNodes.Any(t => string.Equals(t, nodeName, StringComparison.OrdinalIgnoreCase));
        if (!targeted) return rings;

        var deltaAngle = correction.CurAngle - correction.RefAngle;
        var rad = deltaAngle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var scale = (correction.ScaleX + correction.ScaleY) / 2;
        var result = new List<(string, RoiRing)>(rings.Count);
        foreach (var (name, ring) in rings)
        {
            var (cx, cy, ro, ri) = ring.ToPixels(imgW, imgH);
            var lx = (cx - correction.RefX) * correction.ScaleX;
            var ly = (cy - correction.RefY) * correction.ScaleY;
            var nx = correction.CurX + lx * cos - ly * sin;
            var ny = correction.CurY + lx * sin + ly * cos;
            var mapped = RoiRing.FromPixelsCenter(nx, ny, ro * scale, ri * scale, imgW, imgH);
            result.Add((name, mapped ?? ring));
        }
        return result;
    }
}
