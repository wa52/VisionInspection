namespace VisionInspection.Detection;

/// <summary>单个 YOLO 检测结果（源图像素坐标，中心点表示）。</summary>
public readonly record struct YoloDetection(int ClassIndex, string Class, float Conf, double Cx, double Cy, double W, double H);

/// <summary>
/// Ultralytics YOLO ONNX（v8/v11 标准格式）后处理纯函数：输出解析、置信度过滤、按类 NMS、letterbox 逆映射。
/// 模型输出 [1, 4+nc, N]：前 4 行为 cx,cy,w,h（模型输入像素坐标系），后 nc 行为各类别置信度。
/// </summary>
public static class YoloPostprocess
{
    /// <summary>letterbox 等比适配参数：scale = 缩放比，(Dx,Dy) = 模型输入内的留白偏移。</summary>
    public static (double Scale, double Dx, double Dy) LetterboxFit(int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return (1, 0, 0);
        var scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        return (scale, (dstW - srcW * scale) / 2.0, (dstH - srcH * scale) / 2.0);
    }

    /// <summary>
    /// 解析模型输出并过滤+NMS。data 为 [1, 4+nc, N] 展平（行主序：data[c * n + i]）。
    /// 返回模型输入坐标系下的检测框（未做 letterbox 逆映射）。
    /// </summary>
    public static List<(float Cx, float Cy, float W, float H, int ClassIndex, float Conf)> ParseAndNms(
        float[] data, int channels, int count, float confThreshold, float iouThreshold)
    {
        // 仅支持 v8/v11 无 objectness 格式（channels = 4 + nc）；v5（带 objectness）不支持
        if (channels < 5) return [];

        var candidates = new List<(float Cx, float Cy, float W, float H, int Cls, float Conf)>();
        for (var i = 0; i < count; i++)
        {
            var bestCls = -1;
            var bestConf = 0f;
            for (var c = 4; c < channels; c++)
            {
                var v = data[c * count + i];
                if (v > bestConf)
                {
                    bestConf = v;
                    bestCls = c - 4;
                }
            }

            if (bestCls < 0 || bestConf <= confThreshold) continue;
            candidates.Add((
                data[0 * count + i], data[1 * count + i], data[2 * count + i], data[3 * count + i],
                bestCls, bestConf));
        }

        // 按置信度降序，逐类贪心 NMS
        candidates.Sort((a, b) => b.Conf.CompareTo(a.Conf));
        var kept = new List<(float Cx, float Cy, float W, float H, int Cls, float Conf)>();
        var suppressed = new HashSet<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (suppressed.Contains(i)) continue;
            var c = candidates[i];
            kept.Add(c);
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed.Contains(j)) continue;
                var o = candidates[j];
                if (o.Cls != c.Cls) continue;
                if (Iou(c.Cx, c.Cy, c.W, c.H, o.Cx, o.Cy, o.W, o.H) > iouThreshold)
                {
                    suppressed.Add(j);
                }
            }
        }

        return kept;
    }

    /// <summary>两框 IoU（中心点+宽高表示）。</summary>
    public static double Iou(double cx1, double cy1, double w1, double h1, double cx2, double cy2, double w2, double h2)
    {
        var x1 = Math.Max(cx1 - w1 / 2, cx2 - w2 / 2);
        var y1 = Math.Max(cy1 - h1 / 2, cy2 - h2 / 2);
        var x2 = Math.Min(cx1 + w1 / 2, cx2 + w2 / 2);
        var y2 = Math.Min(cy1 + h1 / 2, cy2 + h2 / 2);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = w1 * h1 + w2 * h2 - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>模型输入坐标 → 源图像坐标（letterbox 逆映射，中心点+宽高）。</summary>
    public static (double Cx, double Cy, double W, double H) MapToSource(
        double cx, double cy, double w, double h, double scale, double dx, double dy)
    {
        if (scale <= 0) return (0, 0, 0, 0);
        return ((cx - dx) / scale, (cy - dy) / scale, w / scale, h / scale);
    }
}
