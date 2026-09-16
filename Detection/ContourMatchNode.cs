using System.IO;
using OpenCvSharp;
using VisionInspection.Models;
using VisionInspection.Services;

namespace VisionInspection.Detection;

/// <summary>
/// 轮廓匹配节点（基于边缘方向的形状模板匹配，VisionMaster 轮廓匹配同类原理）。
/// 模板目录契约：shape_template.json（建模弹窗生成：分层边缘点集 + 基准点 + 建模参数）。
/// 输出基准点在搜索图中的 X/Y/Angle/Score（金字塔由粗到细，Score=方向匹配点占比）。
/// 判定模式：匹配成功即OK（默认，缺失→NG）/ 匹配成功即NG（存在→NG）。
/// 快速匹配节点（FastMatchNode）继承本节点，仅搜索内核换快速模式（点集抽稀+单轮精修）。
/// </summary>
public class ContourMatchNode : IModelNode
{
    public const string DecisionFoundOk = "匹配成功即OK";
    public const string DecisionFoundNg = "匹配成功即NG";

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "model_dir", Label = "模板目录(shape_template.json)", Kind = "folder" },
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "min_score", Label = "最低匹配分数", Kind = "double", Default = "0.7" },
        new ParamDef { Key = "angle_start", Label = "起始角度(度)", Kind = "double", Default = "-30" },
        new ParamDef { Key = "angle_extent", Label = "角度范围(度)", Kind = "double", Default = "60" },
        new ParamDef { Key = "num_matches", Label = "最大实例数", Kind = "double", Default = "1" },
        new ParamDef { Key = "min_contrast", Label = "边缘阈值(搜索)", Kind = "double", Default = "30" },
        new ParamDef { Key = "max_overlap", Label = "实例NMS重叠阈值", Kind = "double", Default = "0.3" },
        new ParamDef { Key = "polarity", Label = "方向极性", Kind = "choice", Default = "启用", Choices = ["启用", "忽略"] },
        new ParamDef { Key = "decision_mode", Label = "判定模式", Kind = "choice", Default = DecisionFoundOk, Choices = [DecisionFoundOk, DecisionFoundNg] },
        new ParamDef { Key = "max_workers", Label = "并行线程数(0=自动)", Kind = "double", Default = "0" },
    ];

    protected ShapeTemplate? _template;
    protected readonly Dictionary<string, string> _params = new()
    {
        ["model_dir"] = "",
        ["source"] = "@input",
        ["min_score"] = "0.7",
        ["angle_start"] = "-30",
        ["angle_extent"] = "60",
        ["num_matches"] = "1",
        ["min_contrast"] = "30",
        ["max_overlap"] = "0.3",
        ["polarity"] = "启用",
        ["decision_mode"] = DecisionFoundOk,
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public ContourMatchNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        _params.TryAdd("max_workers", "0");
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public virtual string Type => "ContourMatch";
    public bool Enabled { get; set; } = true;
    public virtual IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    /// <summary>运行时模板目录（已解析绝对路径）。</summary>
    public string ResolvedModelDir { get; private set; } = "";

    public void SetParam(string key, string value)
    {
        _params[key] = value;
        // 模板文件可能在同一目录被覆盖，model_dir 热应用时也必须失效缓存。
        if (key == "model_dir")
        {
            _template = null;
        }
    }

    /// <summary>确保模板已加载（模型目录解析为绝对路径后加载 shape_template.json）。</summary>
    public void EnsureLoaded(string baseDir)
    {
        if (_template != null) return;
        ResolvedModelDir = RecipeStore.Resolve(new Recipe { BaseDir = baseDir }, _params["model_dir"]);
        if (!Directory.Exists(ResolvedModelDir))
        {
            throw new DirectoryNotFoundException($"模板目录不存在: {ResolvedModelDir}");
        }

        _template = ShapeTemplate.Load(ResolvedModelDir);
        if (_template.LevelPoints.Count == 0 || _template.LevelPoints.All(l => l.Count == 0))
        {
            _template = null;
            throw new InvalidDataException("模板无有效轮廓点（请降低边缘对比度阈值后重新建模）");
        }
    }

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

        if (_template == null)
        {
            // 模板未加载：自判 ERROR 停线，但透传源图——显示区有图才能画搜索 ROI/对照建模
            // （提前抛异常会导致永远无图可画，与直线查找同款死循环防御）
            var dirText = string.IsNullOrWhiteSpace(ResolvedModelDir) ? _params.GetValueOrDefault("model_dir") : ResolvedModelDir;
            var message = $"节点 {Name}: 模板未加载（请先在参数面板「创建模板（轮廓建模）」建模，或检查「模板目录」: {dirText}）";
            Log?.Invoke("[轮廓匹配] " + message);
            var errorResult = new NodeResult
            {
                Decision = "ERROR",
                Error = message,
                OutputImage = BuildBaseImage(img),
            };
            errorResult.Values["error"] = message;
            errorResult.Values["loc_valid"] = "0";
            errorResult.Values["matches"] = "0";
            return errorResult;
        }

        // 搜索区域：只支持本节点绘制的私有 ROI（轮廓匹配不引用方案 ROI 库）；空 = 全图
        var rois = NodeRois.ApplyPoseCorrection(
            NodeRois.ParseOwn(_params.GetValueOrDefault("own_rois")), Name, img.Width, img.Height, ctx.PoseCorrection);
        var minScore = ParseDouble(_params.GetValueOrDefault("min_score"), 0.7);
        var angleStart = ParseDouble(_params.GetValueOrDefault("angle_start"), -30);
        var angleExtent = ParseDouble(_params.GetValueOrDefault("angle_extent"), 60);
        var numMatches = Math.Max(1, (int)Math.Round(ParseDouble(_params.GetValueOrDefault("num_matches"), 1)));
        var minContrast = ParseDouble(_params.GetValueOrDefault("min_contrast"), 30);
        var maxOverlap = ParseDouble(_params.GetValueOrDefault("max_overlap"), 0.3);
        // 搜索侧平滑与模板建模一致（用模板存储的 Sigma），避免两边不一致造成方向失配
        var sigma = _template!.Sigma;
        var usePolarity = !string.Equals(_params.GetValueOrDefault("polarity"), "忽略", StringComparison.Ordinal);

        var matches = new List<ShapeMatchInstance>();
        if (rois.Count == 0)
        {
            using var gray = ToGray(img);
            matches.AddRange(FindInRegion(gray, minScore, angleStart, angleExtent, numMatches, minContrast, maxOverlap, sigma, usePolarity));
        }
        else
        {
            // 先裁剪后转灰度（逐像素独立，结果与先整图转灰度严格一致）：大图 + 小 ROI 时省掉全图灰度转换
            foreach (var (name, rect) in rois)
            {
                var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
                var region = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, img.Width, img.Height);
                using var cropBgr = new Mat(img, region);
                using var cropView = ToGray(cropBgr);
                var found = FindInRegion(cropView, minScore, angleStart, angleExtent, numMatches, minContrast, maxOverlap, sigma, usePolarity);
                matches.AddRange(found
                    .Select(m => new ShapeMatchInstance(m.X + region.X, m.Y + region.Y, m.Angle, m.Score))
                    .Where(m => IsMatchInsideRoi(m, rect, img.Width, img.Height)));
            }
        }

        var decision = ComputeDecision(_params.GetValueOrDefault("decision_mode"), matches.Count > 0);

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = BuildBaseImage(img),
        };
        foreach (var shape in BuildAnnotations(matches, decision))
        {
            nodeResult.Annotations.Add(shape);
        }
        foreach (var shape in BuildContourOverlay(matches, _template!, decision))
        {
            nodeResult.Annotations.Add(shape);
        }

        matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = matches.Count > 0 ? matches[0] : null;
        nodeResult.Values["matches"] = matches.Count.ToString();
        nodeResult.Values["score"] = (best?.Score ?? 0).ToString("F3");
        nodeResult.Values["x"] = (best?.X ?? 0).ToString("F2");
        nodeResult.Values["y"] = (best?.Y ?? 0).ToString("F2");
        nodeResult.Values["angle"] = (best?.Angle ?? 0).ToString("F2");
        // 定位标准契约键：位置修正节点按此接入（后续新增定位手段输出同样键即可）
        nodeResult.Values["loc_x"] = nodeResult.Values["x"];
        nodeResult.Values["loc_y"] = nodeResult.Values["y"];
        nodeResult.Values["loc_angle"] = nodeResult.Values["angle"];
        nodeResult.Values["loc_valid"] = matches.Count > 0 ? "1" : "0";
        for (var i = 0; i < matches.Count; i++)
        {
            nodeResult.Values[$"match_{i + 1}_x"] = matches[i].X.ToString("F2");
            nodeResult.Values[$"match_{i + 1}_y"] = matches[i].Y.ToString("F2");
            nodeResult.Values[$"match_{i + 1}_angle"] = matches[i].Angle.ToString("F2");
            nodeResult.Values[$"match_{i + 1}_score"] = matches[i].Score.ToString("F3");
        }
        return nodeResult;
    }

    private bool IsMatchInsideRoi(ShapeMatchInstance match, RoiRect roi, int imgW, int imgH)
    {
        if (_template is null || _template.LevelPoints.Count == 0) return false;
        var rad = match.Angle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return _template.LevelPoints[0].All(p =>
        {
            var x = match.X + p.X * cos - p.Y * sin;
            var y = match.Y + p.X * sin + p.Y * cos;
            return NodeRois.ContainsPixel(roi, x, y, imgW, imgH);
        });
    }

    /// <summary>判定（纯逻辑，可单测）：匹配成功即OK（默认）/ 匹配成功即NG。</summary>
    internal static string ComputeDecision(string? mode, bool found)
    {
        if (string.Equals((mode ?? "").Trim(), DecisionFoundNg, StringComparison.Ordinal))
        {
            return found ? "NG" : "OK";
        }
        return found ? "OK" : "NG";
    }

    protected virtual List<ShapeMatchInstance> FindInRegion(
        Mat gray, double minScore, double angleStart, double angleExtent, int numMatches,
        double minContrast, double maxOverlap, double sigma, bool usePolarity)
    {
        // ≤0 或非法值 = 自动（ShapeMatcher 内部取 ProcessorCount/2）；分块按索引序合并，与串行逐位一致
        var maxWorkers = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("max_workers"), 0));
        var matcher = new ShapeMatcher(_template!, minScore, angleStart, angleExtent, numMatches, maxOverlap,
            minContrast, sigma, usePolarity, fastMode: false, maxWorkers: maxWorkers);
        return matcher.Find(gray);
    }

    /// <summary>任意通道 → 单通道灰度（每次运行新 Mat，调用方负责释放）。</summary>
    private static Mat ToGray(Mat img)
    {
        if (img.Channels() == 1) return img.Clone();
        var dst = new Mat();
        Cv2.CvtColor(img, dst, img.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return dst;
    }

    /// <summary>输出底图：3 通道克隆（十字/角度线/文字由 UI 矢量叠加层渲染，不进位图）。</summary>
    private static Mat BuildBaseImage(Mat img)
    {
        if (img.Channels() == 3) return img.Clone();
        var mat = new Mat();
        Cv2.CvtColor(img, mat, img.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        return mat;
    }

    /// <summary>矢量标注：基准点十字 + 角度指示线 + 序号/分数/角度标签（NG=红 / OK=绿）。</summary>
    internal static List<NodeShape> BuildAnnotations(List<ShapeMatchInstance> matches, string decision)
    {
        var shapes = new List<NodeShape>();
        var kind = decision == "NG" ? NodeShapeKind.Defect : NodeShapeKind.Ok;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var cos = Math.Cos(m.Angle * Math.PI / 180.0);
            var sin = Math.Sin(m.Angle * Math.PI / 180.0);
            var cx = (int)Math.Round(m.X);
            var cy = (int)Math.Round(m.Y);
            shapes.Add(new NodeShape
            {
                Polys =
                [
                    // 十字横线（沿模板 X 轴）
                    [new Point((int)Math.Round(m.X - 12 * cos), (int)Math.Round(m.Y - 12 * sin)),
                     new Point((int)Math.Round(m.X + 12 * cos), (int)Math.Round(m.Y + 12 * sin))],
                    // 十字纵线
                    [new Point((int)Math.Round(m.X + 12 * sin), (int)Math.Round(m.Y - 12 * cos)),
                     new Point((int)Math.Round(m.X - 12 * sin), (int)Math.Round(m.Y + 12 * cos))],
                    // 角度指示线（模板 X 轴正向 30px）
                    [new Point(cx, cy), new Point((int)Math.Round(m.X + 30 * cos), (int)Math.Round(m.Y + 30 * sin))],
                ],
                Label = $"#{i + 1} {m.Score:F2} {m.Angle:F1}°",
                Kind = kind,
            });
        }
        return shapes;
    }

    /// <summary>
    /// 模板轮廓叠加（VisionMaster 轮廓匹配同款效果）：把模板边缘方向点按匹配位姿变换后
    /// 画成小实心点叠在图上，直观显示对齐质量。每个实例最多 MaxSegments 点（按幅值序均匀抽样）。
    /// 点大小为屏幕常量（UI 渲染），坐标为源图像素。
    /// </summary>
    internal static List<NodeShape> BuildContourOverlay(List<ShapeMatchInstance> matches, ShapeTemplate template, string decision)
    {
        const int MaxPoints = 400;
        var shapes = new List<NodeShape>();
        var kind = decision == "NG" ? NodeShapeKind.Defect : NodeShapeKind.Ok;
        foreach (var m in matches)
        {
            var pts = template.LevelPoints[0];
            if (pts.Count == 0) continue;
            var step = Math.Max(1, (int)Math.Ceiling(pts.Count / (double)MaxPoints));
            var cos = Math.Cos(m.Angle * Math.PI / 180.0);
            var sin = Math.Sin(m.Angle * Math.PI / 180.0);
            var points = new Point[pts.Count / step + 1];
            var count = 0;
            for (var i = 0; i < pts.Count; i += step)
            {
                var p = pts[i];
                points[count++] = new Point(
                    (int)Math.Round(m.X + p.X * cos - p.Y * sin),
                    (int)Math.Round(m.Y + p.X * sin + p.Y * cos));
            }
            shapes.Add(new NodeShape
            {
                Polys = [points[..count]],
                AsPoints = true,
                Label = "",
                Kind = kind,
            });
            // 模板范围框：建模 ROI 宽高绕基准点随位姿旋转（VisionMaster 同款，与 ROI 框区分）
            if (template.RoiW >= 4 && template.RoiH >= 4)
            {
                var hw = template.RoiW / 2.0;
                var hh = template.RoiH / 2.0;
                shapes.Add(new NodeShape
                {
                    Polys =
                    [
                        new[]
                        {
                            new Point((int)Math.Round(m.X - hw * cos + hh * sin), (int)Math.Round(m.Y - hw * sin - hh * cos)),
                            new Point((int)Math.Round(m.X + hw * cos + hh * sin), (int)Math.Round(m.Y + hw * sin - hh * cos)),
                            new Point((int)Math.Round(m.X + hw * cos - hh * sin), (int)Math.Round(m.Y + hw * sin + hh * cos)),
                            new Point((int)Math.Round(m.X - hw * cos - hh * sin), (int)Math.Round(m.Y - hw * sin + hh * cos)),
                        },
                    ],
                    Label = "",
                    Kind = kind,
                });
            }
        }
        return shapes;
    }

    protected static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, out var v) ? v : fallback;

    public void Dispose()
    {
        _template = null;
    }
}
