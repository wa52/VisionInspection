using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>检测项级元数据：启用开关 / 判断(空=参与判定，仅观察=只记录不判NG) / 阈值(检测项级，节点不再有总阈值) / 是否存图。</summary>
public sealed record RoiMeta(bool Enabled = true, string Judge = "", string Threshold = "", bool SaveImage = true)
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
        return string.Join(",", parts);
    }

    public bool IsDefault => Enabled && Judge.Length == 0 && Threshold.Length == 0 && SaveImage;

    /// <summary>阈值数值（空/非法返回 null）。</summary>
    public double? ThresholdValue => double.TryParse(Threshold, out var v) ? v : null;
}

/// <summary>own_rois 条目：几何 + 检测项元数据。</summary>
public sealed record RoiItem(string Name, RoiRect Rect, RoiMeta Meta);

/// <summary>
/// 节点私有 ROI 集合的解析/序列化（方案级 ROI 库已移除，所有 ROI 都画在本节点输出图上）。
/// 序列化格式（条目间用 '|' 分隔）：
/// 旧格式 "name;cx,cy,w,h,angle"；新格式追加第 3 段元数据 "name;cx,cy,w,h,angle;e=1,d=仅观察,t=0.6,s=0"
/// （e=启用 d=判断 t=阈值 s=存图；缺省段=全默认，旧配方零迁移兼容）。
/// </summary>
public static class NodeRois
{
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
