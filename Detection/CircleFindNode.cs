using System.Globalization;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 圆查找节点（VM 卡尺找圆式）：单圆环搜索区域内沿中圆布卡尺，径向找亚像素边缘点 → Kasa 最小二乘拟合圆。
/// 搜索区域存独立 own_rings 参数（圆环 ROI，环带宽度即卡尺搜索范围，无 search_length）。
/// 输出圆几何（圆心/半径/得分）+ loc_x/loc_y/loc_angle/loc_valid 定位契约（圆心 + 角度恒 0，纯平移定位），
/// 可作为位置修正的定位来源。判定方式：找到即OK[默认→未找到=NG]/找到即NG。
/// 算法核在 Detection/CircleFind/（纯逻辑，合成图精度+性能有单测）。
/// </summary>
public sealed class CircleFindNode : IModelNode
{
    public const string DecisionFoundOk = "找到即OK";
    public const string DecisionFoundNg = "找到即NG";
    private const double MinimumReliableScore = 0.30;

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "edge_polarity", Label = "边缘极性", Kind = "choice", Default = LineFindPolarity.DarkToBright, Choices = [LineFindPolarity.DarkToBright, LineFindPolarity.BrightToDark, LineFindPolarity.Any] },
        new ParamDef { Key = "edge_threshold", Label = "边缘阈值 (0-255)", Kind = "int", Default = "30" },
        new ParamDef { Key = "filter_size", Label = "滤波尺寸(1=不滤波)", Kind = "choice", Default = "3", Choices = ["1", "2", "3", "5", "7"] },
        new ParamDef { Key = "caliper_num", Label = "卡尺数量(≥3)", Kind = "int", Default = "24" },
        new ParamDef { Key = "edge_type", Label = "边缘类型", Kind = "choice", Default = LineFindEdgeType.Strongest, Choices = [LineFindEdgeType.Strongest, LineFindEdgeType.First, LineFindEdgeType.Last] },
        new ParamDef { Key = "reject_num", Label = "剔除点数", Kind = "int", Default = "0" },
        new ParamDef { Key = "reject_dist", Label = "剔除距离(px)", Kind = "int", Default = "3" },
        new ParamDef { Key = "fit_method", Label = "拟合方式", Kind = "choice", Default = LineFindFitMethod.Robust, Choices = [LineFindFitMethod.Lsq, LineFindFitMethod.Robust] },
        new ParamDef { Key = "decision_mode", Label = "判定方式", Kind = "choice", Default = DecisionFoundOk, Choices = [DecisionFoundOk, DecisionFoundNg] },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["edge_polarity"] = LineFindPolarity.DarkToBright,
        ["edge_threshold"] = "30",
        ["filter_size"] = "3",
        ["caliper_num"] = "24",
        ["edge_type"] = LineFindEdgeType.Strongest,
        ["reject_num"] = "0",
        ["reject_dist"] = "3",
        ["fit_method"] = LineFindFitMethod.Robust,
        ["decision_mode"] = DecisionFoundOk,
    };

    /// <summary>运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public CircleFindNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "CircleFind";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        // 图像来源：@input 或上游节点输出
        var source = _params.GetValueOrDefault("source") ?? "@input";
        var img = source == "@input" ? bgr : (ctx.Images.TryGetValue(source, out var im) ? im : null);
        if (img == null)
        {
            throw new InvalidOperationException($"节点 {Name}: 图像来源未找到: {source}（来源节点不存在或不在本节点上游）");
        }
        if (img.Empty())
        {
            throw new InvalidOperationException($"节点 {Name}: 输入图像为空（单次/连续执行请把「图像来源」指向图像源节点）");
        }

        // 搜索圆环：单区域语义（VM 同款，一个圆查找模块找一个圆）
        var rings = NodeRings.Parse(_params.GetValueOrDefault("own_rings"));
        if (rings.Count == 0)
        {
            // 未画圆环：自判 ERROR 停线，但透传源图——显示区有图才能画 ROI（提前抛异常会导致永远无图可画，死循环）
            var message = $"节点 {Name}: 未绘制圆环搜索区域（请在参数面板「检测区域 (ROI)」画圆环：拖拽定外圆，内圆半径可拖内圈手柄调整）";
            Log?.Invoke("[圆查找] " + message);
            var errorResult = new NodeResult
            {
                Decision = "ERROR",
                Error = message,
                OutputImage = BuildBaseImage(img),
            };
            errorResult.Values["error"] = message;
            errorResult.Values["loc_valid"] = "0";
            return errorResult;
        }
        if (rings.Count > 1)
        {
            Log?.Invoke($"[圆查找] {Name}: 绘制了 {rings.Count} 个圆环，圆查找只使用第一个（单区域语义，其余忽略）");
        }
        var ringName = rings[0].Name;
        var applied = NodeRings.ApplyPoseCorrection([rings[0]], Name, img.Width, img.Height, ctx.PoseCorrection);
        var (cx, cy, ro, ri) = applied[0].Ring.ToPixels(img.Width, img.Height);

        // 先裁剪后灰度（外圆外接正方形）；灰度转 byte[]（GetArray 只读）
        var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, 2 * ro, 2 * ro, 0, img.Width, img.Height);
        using var bgrCrop = new Mat(img, cropRect);
        using var grayCrop = CharRecNode.ToGray(bgrCrop);
        if (!grayCrop.GetArray(out byte[] grayBuf))
        {
            throw new InvalidOperationException($"节点 {Name}: 灰度图读取失败（像素格式不支持）");
        }

        var region = new CircleSearchRegion(cx - cropRect.X, cy - cropRect.Y, ro, ri);
        var p = new CircleFindParams
        {
            Polarity = _params.GetValueOrDefault("edge_polarity") ?? LineFindPolarity.DarkToBright,
            EdgeThreshold = ParseDouble(_params.GetValueOrDefault("edge_threshold"), 30),
            FilterSize = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("filter_size"), 3)),
            CaliperNum = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("caliper_num"), 24)),
            EdgeType = _params.GetValueOrDefault("edge_type") ?? LineFindEdgeType.Strongest,
            RejectNum = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("reject_num"), 0)),
            RejectDist = ParseDouble(_params.GetValueOrDefault("reject_dist"), 3),
            FitMethod = _params.GetValueOrDefault("fit_method") ?? LineFindFitMethod.Lsq,
        };

        var result = CircleFindCore.Find(grayBuf, cropRect.Width, cropRect.Height, region, p);

        // 坐标映射回源图（裁剪区偏移）
        if (result.Found)
        {
            result = result with
            {
                CenterX = result.CenterX + cropRect.X,
                CenterY = result.CenterY + cropRect.Y,
                Edges = result.Edges
                    .Select(e => e with { X = e.X + cropRect.X, Y = e.Y + cropRect.Y })
                    .ToList(),
                Inliers = result.Inliers
                    .Select(e => e with { X = e.X + cropRect.X, Y = e.Y + cropRect.Y })
                    .ToList(),
            };
        }

        var reliable = result.Found && result.Score >= MinimumReliableScore;
        var decision = ComputeDecision(_params.GetValueOrDefault("decision_mode"), reliable);

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = BuildBaseImage(img),
        };

        // 输出值 + 定位契约（圆心 + 角度恒 0 = 纯平移定位）
        var values = nodeResult.Values;
        values["count"] = result.Edges.Count.ToString(CultureInfo.InvariantCulture);
        if (result.Found)
        {
            values["center_x"] = result.CenterX.ToString("F2", CultureInfo.InvariantCulture);
            values["center_y"] = result.CenterY.ToString("F2", CultureInfo.InvariantCulture);
            values["radius"] = result.Radius.ToString("F2", CultureInfo.InvariantCulture);
            values["score"] = result.Score.ToString("F3", CultureInfo.InvariantCulture);
            values["mean_contrast"] = result.MeanContrast.ToString("F1", CultureInfo.InvariantCulture);
            values["loc_x"] = values["center_x"];
            values["loc_y"] = values["center_y"];
            values["loc_angle"] = "0.00";
            values["loc_valid"] = reliable ? "1" : "0";
            if (!reliable)
            {
                var message = $"有效卡尺比例过低（{result.Edges.Count}/{Math.Max(3, p.CaliperNum)}，得分 {result.Score:F3}，要求 ≥{MinimumReliableScore:F2}）";
                values["error"] = message;
                nodeResult.Error = message;
                Log?.Invoke($"[圆查找] {Name}: {message}");
            }
        }
        else
        {
            values["loc_valid"] = "0";
            values["error"] = result.Error ?? "未找到圆";
            nodeResult.Error = result.Error;
            if (decision == "NG")
            {
                Log?.Invoke($"[圆查找] {Name}: {nodeResult.Error}");
            }
        }

        // 矢量标注（不进位图）：拟合圆 + 卡尺边缘点 + 搜索圆环（内外双圆）
        var foundOk = decision != "NG";
        if (result.Found)
        {
            nodeResult.Annotations.Add(new NodeShape
            {
                Polys = [CirclePoly(result.CenterX, result.CenterY, result.Radius)],
                Label = $"半径{result.Radius:F2} 得分{result.Score:F2}",
                Kind = foundOk ? NodeShapeKind.Ok : NodeShapeKind.Defect,
            });
            if (result.Edges.Count > 0)
            {
                nodeResult.Annotations.Add(new NodeShape
                {
                    Polys = [result.Edges.Select(e => new Point((int)Math.Round(e.X), (int)Math.Round(e.Y))).ToArray()],
                    AsPoints = true,
                    Kind = foundOk ? NodeShapeKind.Ok : NodeShapeKind.Defect,
                });
            }
        }
        else if (result.Edges.Count > 0)
        {
            nodeResult.Annotations.Add(new NodeShape
            {
                Polys = [result.Edges.Select(e => new Point((int)Math.Round(e.X), (int)Math.Round(e.Y))).ToArray()],
                AsPoints = true,
                Kind = NodeShapeKind.Defect,
            });
        }
        nodeResult.Annotations.Add(new NodeShape
        {
            Polys = [CirclePoly(cx, cy, ro), CirclePoly(cx, cy, ri)],
            Label = ringName,
            Kind = NodeShapeKind.Info,
        });

        return nodeResult;
    }

    public void Dispose() { }

    /// <summary>判定（纯逻辑，可单测）：找到即OK[默认]=未找到 NG；找到即NG=找到 NG。</summary>
    internal static string ComputeDecision(string? mode, bool found)
    {
        if (string.Equals((mode ?? "").Trim(), DecisionFoundNg, StringComparison.Ordinal))
        {
            return found ? "NG" : "OK";
        }
        return found ? "OK" : "NG";
    }

    /// <summary>圆周折线（segments 段均匀采样；WPF Polygon 自动闭合）。</summary>
    internal static Point[] CirclePoly(double cx, double cy, double r, int segments = 72)
    {
        if (r <= 0) return [];
        var pts = new Point[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            pts[i] = new Point((int)Math.Round(cx + r * Math.Cos(a)), (int)Math.Round(cy + r * Math.Sin(a)));
        }
        return pts;
    }

    /// <summary>输出底图：3 通道 BGR 克隆（框/文字改由 UI 矢量叠加层渲染，不进位图）。</summary>
    private static Mat BuildBaseImage(Mat img)
    {
        if (img.Channels() == 3) return img.Clone();
        var mat = new Mat();
        Cv2.CvtColor(img, mat, img.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return mat;
    }

    private static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
