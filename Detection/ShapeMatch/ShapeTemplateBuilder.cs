using OpenCvSharp;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 轮廓模板建模：灰度图 → 高斯平滑 → Sobel 梯度 → 幅值非极大值抑制 → 对比度阈值筛选
/// → 归一化梯度方向 → 金字塔分层点集（每层按对比度取前 N，防止点数爆炸）。
/// 层级：LevelPoints[0] = 原始分辨率，逐层 pyrDown（1/2^i）。
/// </summary>
public static class ShapeTemplateBuilder
{
    /// <summary>最粗层最小边（低于该尺寸不再下采样）。</summary>
    public const int MinCoarsestSide = 64;

    public const int DefaultMaxPoints = 2000;

    /// <summary>
    /// 从模板灰度图构建模板。refX/refY 为基准点（patch 内原始分辨率像素坐标）。
    /// </summary>
    public static ShapeTemplate Build(
        Mat grayPatch, double refX, double refY, double sigma, double minContrast,
        int maxLevels, int maxPointsPerLevel = DefaultMaxPoints)
    {
        if (grayPatch.Empty() || grayPatch.Channels() != 1)
        {
            throw new ArgumentException("模板图需为单通道灰度图");
        }

        var levelPoints = new List<List<TemplatePoint>>();
        var current = grayPatch.Clone();
        try
        {
            var scale = 1.0;
            for (var level = 0; level < Math.Max(1, maxLevels); level++)
            {
                // 层基准点 = 原始基准点 / 2^level（点坐标相对层内基准点）
                levelPoints.Add(ExtractLevel(current, sigma, minContrast, maxPointsPerLevel, refX / scale, refY / scale));
                if (Math.Min(current.Width, current.Height) / 2 < MinCoarsestSide)
                {
                    break;
                }
                var next = new Mat();
                Cv2.PyrDown(current, next);
                current.Dispose();
                current = next;
                scale *= 2;
            }
        }
        finally
        {
            current.Dispose();
        }

        return new ShapeTemplate
        {
            ReferenceX = refX,
            ReferenceY = refY,
            Sigma = sigma,
            MinContrast = minContrast,
            MaxPointsPerLevel = maxPointsPerLevel,
            LevelPoints = levelPoints,
        };
    }

    /// <summary>单层边缘点提取：平滑 → Sobel → 幅值 NMS → 阈值 → 前 N。坐标相对该层基准点。</summary>
    internal static List<TemplatePoint> ExtractLevel(
        Mat gray, double sigma, double minContrast, int maxPoints, double refXLevel, double refYLevel)
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
            return [];
        }

        var w = src.Width;
        var h = src.Height;
        var candidates = new List<(float X, float Y, float Mag, float Dx, float Dy)>();
        for (var y = 1; y < h - 1; y++)
        {
            var row = y * w;
            for (var x = 1; x < w - 1; x++)
            {
                var idx = row + x;
                var mag = MathF.Sqrt(gxData[idx] * gxData[idx] + gyData[idx] * gyData[idx]);
                if (mag < minContrast) continue;
                // 轴向非极大值抑制：幅值需为左右、上下邻域的最大值
                var magL = MagAt(gxData, gyData, idx - 1);
                var magR = MagAt(gxData, gyData, idx + 1);
                var magU = MagAt(gxData, gyData, idx - w);
                var magD = MagAt(gxData, gyData, idx + w);
                if (mag < magL || mag < magR || mag < magU || mag < magD) continue;

                candidates.Add((x, y, mag, gxData[idx] / mag, gyData[idx] / mag));
            }
        }

        candidates.Sort((a, b) => b.Mag.CompareTo(a.Mag));
        var result = new List<TemplatePoint>(Math.Min(candidates.Count, maxPoints));
        foreach (var c in candidates)
        {
            if (result.Count >= maxPoints) break;
            result.Add(new TemplatePoint(c.X - (float)refXLevel, c.Y - (float)refYLevel, c.Dx, c.Dy));
        }
        return result;
    }

    private static float MagAt(float[] gx, float[] gy, int idx)
    {
        var x = gx[idx];
        var y = gy[idx];
        return MathF.Sqrt(x * x + y * y);
    }
}
