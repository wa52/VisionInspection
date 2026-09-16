using System.Globalization;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 直线查找节点（VM 卡尺找线式）：单搜索区域内沿长轴布卡尺，法线方向找亚像素边缘点 → 最小二乘拟合直线。
/// 输出直线几何（起点/终点/角度/得分）+ loc_x/loc_y/loc_angle/loc_valid 定位契约（中点+直线角度），
/// 可作为位置修正的定位来源。判定方式：找到即OK[默认→未找到=NG]/找到即NG。
/// 算法核在 Detection/LineFind/（纯逻辑，合成图精度+性能有单测）。
/// </summary>
public sealed class LineFindNode : IModelNode
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
        new ParamDef { Key = "caliper_num", Label = "卡尺数量(≥2)", Kind = "int", Default = "16" },
        new ParamDef { Key = "search_length", Label = "卡尺搜索长度(px，0=ROI短边全范围)", Kind = "int", Default = "0" },
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
        ["caliper_num"] = "16",
        ["search_length"] = "0",
        ["edge_type"] = LineFindEdgeType.Strongest,
        ["reject_num"] = "0",
        ["reject_dist"] = "3",
        ["fit_method"] = LineFindFitMethod.Robust,
        ["decision_mode"] = DecisionFoundOk,
    };

    /// <summary>运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }
    private LineFindPrior? _lastPrior;

    public LineFindNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "LineFind";
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

        // 搜索区域：单区域语义（VM 同款，一个直线查找模块找一条线）
        var items = NodeRois.ParseOwnFull(_params.GetValueOrDefault("own_rois"));
        if (items.Count == 0)
        {
            // 未画框：自判 ERROR 停线，但透传源图——显示区有图才能画 ROI（提前抛异常会导致永远无图可画，死循环）
            var message = $"节点 {Name}: 未绘制搜索区域（请在参数面板「检测区域 (ROI)」画框，长边=预期直线方向）";
            Log?.Invoke("[直线查找] " + message);
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
        if (items.Count > 1)
        {
            Log?.Invoke($"[直线查找] {Name}: 绘制了 {items.Count} 个区域，直线查找只使用第一个（单区域语义，其余忽略）");
        }
        var regionName = items[0].Name;
        var rois = NodeRois.ApplyPoseCorrection(
            [(items[0].Name, items[0].Rect)], Name, img.Width, img.Height, ctx.PoseCorrection);
        var (cx, cy, w, h, angleDeg) = rois[0].Rect.ToPixels(img.Width, img.Height);

        // 长轴 = 框的较长边（h>w 时角度+90 并交换宽高）
        double axisAngleDeg, halfLen, halfWidth;
        if (h > w)
        {
            axisAngleDeg = angleDeg + 90;
            halfLen = h / 2.0;
            halfWidth = w / 2.0;
        }
        else
        {
            axisAngleDeg = angleDeg;
            halfLen = w / 2.0;
            halfWidth = h / 2.0;
        }

        // 先裁剪后灰度（大图小区域省全图转换）；灰度转 byte[]（GetArray 只读）
        var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angleDeg, img.Width, img.Height);
        using var bgrCrop = new Mat(img, cropRect);
        using var grayCrop = CharRecNode.ToGray(bgrCrop);
        if (!grayCrop.GetArray(out byte[] grayBuf))
        {
            throw new InvalidOperationException($"节点 {Name}: 灰度图读取失败（像素格式不支持）");
        }

        var region = new LineSearchRegion(
            cx - cropRect.X, cy - cropRect.Y, halfLen, halfWidth, axisAngleDeg * Math.PI / 180.0);
        var p = new LineFindParams
        {
            Polarity = _params.GetValueOrDefault("edge_polarity") ?? LineFindPolarity.DarkToBright,
            EdgeThreshold = ParseDouble(_params.GetValueOrDefault("edge_threshold"), 30),
            FilterSize = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("filter_size"), 3)),
            CaliperNum = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("caliper_num"), 16)),
            SearchLength = ParseDouble(_params.GetValueOrDefault("search_length"), 0),
            EdgeType = _params.GetValueOrDefault("edge_type") ?? LineFindEdgeType.Strongest,
            RejectNum = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("reject_num"), 0)),
            RejectDist = ParseDouble(_params.GetValueOrDefault("reject_dist"), 3),
            FitMethod = _params.GetValueOrDefault("fit_method") ?? LineFindFitMethod.Lsq,
        };

        var priorSource = _lastPrior is { } last
            ? (LineFindPrior?)new LineFindPrior(last.X - cropRect.X, last.Y - cropRect.Y, last.AngleDeg)
            : null;
        var result = LineFindCore.Find(grayBuf, cropRect.Width, cropRect.Height, region, p, priorSource);

        // 坐标映射回源图（裁剪区偏移）
        if (result.Found)
        {
            result = result with
            {
                X1 = result.X1 + cropRect.X,
                Y1 = result.Y1 + cropRect.Y,
                X2 = result.X2 + cropRect.X,
                Y2 = result.Y2 + cropRect.Y,
                Edges = result.Edges
                    .Select(e => e with { X = e.X + cropRect.X, Y = e.Y + cropRect.Y })
                    .ToList(),
                Inliers = result.Inliers
                    .Select(e => e with { X = e.X + cropRect.X, Y = e.Y + cropRect.Y })
                    .ToList(),
            };
            _lastPrior = new LineFindPrior(result.MidX, result.MidY, result.AngleDeg);
        }
        else
        {
            _lastPrior = null;
        }

        var reliable = result.Found && result.Score >= MinimumReliableScore;
        var decision = ComputeDecision(_params.GetValueOrDefault("decision_mode"), reliable);

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = BuildBaseImage(img),
        };

        // 输出值 + 定位契约（中点 + 直线角度）
        var values = nodeResult.Values;
        values["count"] = result.Edges.Count.ToString(CultureInfo.InvariantCulture);
        if (result.Found)
        {
            values["x1"] = result.X1.ToString("F2", CultureInfo.InvariantCulture);
            values["y1"] = result.Y1.ToString("F2", CultureInfo.InvariantCulture);
            values["x2"] = result.X2.ToString("F2", CultureInfo.InvariantCulture);
            values["y2"] = result.Y2.ToString("F2", CultureInfo.InvariantCulture);
            values["angle"] = result.AngleDeg.ToString("F2", CultureInfo.InvariantCulture);
            values["mid_x"] = result.MidX.ToString("F2", CultureInfo.InvariantCulture);
            values["mid_y"] = result.MidY.ToString("F2", CultureInfo.InvariantCulture);
            values["score"] = result.Score.ToString("F3", CultureInfo.InvariantCulture);
            values["mean_contrast"] = result.MeanContrast.ToString("F1", CultureInfo.InvariantCulture);
            values["loc_x"] = values["mid_x"];
            values["loc_y"] = values["mid_y"];
            values["loc_angle"] = values["angle"];
            values["loc_valid"] = reliable ? "1" : "0";
            if (!reliable)
            {
                var message = $"有效卡尺比例过低（{result.Edges.Count}/{Math.Max(2, p.CaliperNum)}，得分 {result.Score:F3}，要求 ≥{MinimumReliableScore:F2}）";
                values["error"] = message;
                nodeResult.Error = message;
                Log?.Invoke($"[直线查找] {Name}: {message}");
            }
        }
        else
        {
            values["loc_valid"] = "0";
            values["error"] = result.Error ?? "未找到直线";
            nodeResult.Error = result.Error;
            if (decision == "NG")
            {
                Log?.Invoke($"[直线查找] {Name}: {nodeResult.Error}");
            }
        }

        // 矢量标注（不进位图）：拟合线段 + 卡尺边缘点 + 搜索区域四边形
        var foundOk = decision != "NG";
        if (result.Found)
        {
            nodeResult.Annotations.Add(new NodeShape
            {
                Polys = [new[] { new Point((int)Math.Round(result.X1), (int)Math.Round(result.Y1)), new Point((int)Math.Round(result.X2), (int)Math.Round(result.Y2)) }],
                Label = $"角度{result.AngleDeg:F2}° 得分{result.Score:F2}",
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
            Polys = [PatchCoreNode.RoiQuad(cx, cy, w, h, angleDeg)],
            Label = regionName,
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
