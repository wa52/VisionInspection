using System.Globalization;
using System.IO;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// Blob 分析节点（传统视觉路线，VisionMaster Blob查找/斑点分析同类原理）：
/// 检测项 ROI 内 二值化（固定阈值[默认]/Otsu/双阈值/无二值化 + 极性 亮斑暗底[默认]/暗斑亮底）→ 孔洞填充 →
/// 连通域(8邻接[默认]/4邻接)提取斑点 → 特征筛选（面积[默认开]/周长/圆度/矩形度/长轴/短轴）→ 排序 → 检测项级判定。
/// 无外部模型/模板，参数全部热生效；位置修正跟随、启停/仅观察/检测项阈值/存图/总范围/切图/整图留存全部与 Seg 同机制。
/// 判定：检出即NG[默认]/缺失即NG/数量≥阈值即NG/最大面积≥阈值即NG/仅检出不判定（阈值=检测项级，缺省 1）。
/// </summary>
public sealed class BlobNode : IModelNode
{
    public const string DecisionDetectNg = "检出即NG";
    public const string DecisionMissingNg = "缺失即NG";
    public const string DecisionCountNg = "数量≥阈值即NG";
    public const string DecisionAreaNg = "最大面积≥阈值即NG";
    public const string DecisionObserveOnly = "仅检出不判定";

    public const string PolarityBright = "亮斑暗底";
    public const string PolarityDark = "暗斑亮底";

    /// <summary>检测项默认阈值（数量/最大面积判定模式共用；新建检测项预填，缺省回退此值）。</summary>
    public const string DefaultThreshold = "1";

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "decision_mode", Label = "判定模式", Kind = "choice", Default = DecisionDetectNg, Choices = [DecisionDetectNg, DecisionMissingNg, DecisionCountNg, DecisionAreaNg, DecisionObserveOnly] },
        new ParamDef { Key = "threshold_type", Label = "二值化方式", Kind = "choice", Default = BlobAnalyzer.ThFixed, Choices = [BlobAnalyzer.ThFixed, BlobAnalyzer.ThOtsu, BlobAnalyzer.ThDouble, BlobAnalyzer.ThNone] },
        new ParamDef { Key = "polarity", Label = "斑点极性", Kind = "choice", Default = PolarityBright, Choices = [PolarityBright, PolarityDark] },
        new ParamDef { Key = "threshold", Label = "二值化阈值(0-255)", Kind = "double", Default = "100" },
        new ParamDef { Key = "threshold_high", Label = "高阈值(双阈值用,0-255)", Kind = "double", Default = "150" },
        new ParamDef { Key = "connectivity", Label = "连通性", Kind = "choice", Default = "8邻接", Choices = ["8邻接", "4邻接"] },
        new ParamDef { Key = "hole_min_area", Label = "孔洞填充面积上限(px,面积≤此值的孔填回,0=不填充)", Kind = "double", Default = "0" },
        new ParamDef { Key = "find_num", Label = "最大查找斑点数", Kind = "double", Default = "100" },
        new ParamDef { Key = "select_by_area", Label = "按面积筛选", Kind = "bool", Default = "true" },
        new ParamDef { Key = "min_area", Label = "最小面积(px)", Kind = "double", Default = "10" },
        new ParamDef { Key = "max_area", Label = "最大面积(px)", Kind = "double", Default = "999999999" },
        new ParamDef { Key = "select_by_perimeter", Label = "按周长筛选", Kind = "bool", Default = "false" },
        new ParamDef { Key = "min_perimeter", Label = "最小周长(px)", Kind = "double", Default = "10" },
        new ParamDef { Key = "max_perimeter", Label = "最大周长(px)", Kind = "double", Default = "999999999" },
        new ParamDef { Key = "select_by_circularity", Label = "按圆度筛选(0-1)", Kind = "bool", Default = "false" },
        new ParamDef { Key = "min_circularity", Label = "最小圆度", Kind = "double", Default = "0.1" },
        new ParamDef { Key = "max_circularity", Label = "最大圆度", Kind = "double", Default = "1" },
        new ParamDef { Key = "select_by_rectangularity", Label = "按矩形度筛选(0-1)", Kind = "bool", Default = "false" },
        new ParamDef { Key = "min_rectangularity", Label = "最小矩形度", Kind = "double", Default = "0.1" },
        new ParamDef { Key = "max_rectangularity", Label = "最大矩形度", Kind = "double", Default = "1" },
        new ParamDef { Key = "select_by_long_axis", Label = "按长轴筛选", Kind = "bool", Default = "false" },
        new ParamDef { Key = "min_long_axis", Label = "最小长轴(px)", Kind = "double", Default = "10" },
        new ParamDef { Key = "max_long_axis", Label = "最大长轴(px)", Kind = "double", Default = "999999999" },
        new ParamDef { Key = "select_by_short_axis", Label = "按短轴筛选", Kind = "bool", Default = "false" },
        new ParamDef { Key = "min_short_axis", Label = "最小短轴(px)", Kind = "double", Default = "1" },
        new ParamDef { Key = "max_short_axis", Label = "最大短轴(px)", Kind = "double", Default = "999999999" },
        new ParamDef { Key = "sort_feature", Label = "排序特征", Kind = "choice", Default = BlobAnalyzer.SortArea, Choices = [BlobAnalyzer.SortArea, BlobAnalyzer.SortPerimeter, BlobAnalyzer.SortCircularity, BlobAnalyzer.SortRectangularity, BlobAnalyzer.SortCentroidX, BlobAnalyzer.SortCentroidY, BlobAnalyzer.SortBoxAngle] },
        new ParamDef { Key = "sort_mode", Label = "排序方式", Kind = "choice", Default = BlobAnalyzer.SortDesc, Choices = [BlobAnalyzer.SortDesc, BlobAnalyzer.SortAsc, BlobAnalyzer.SortNone] },
        new ParamDef { Key = "crop_dir", Label = "斑点区切图保存目录(可选·按OK/NG分目录)", Kind = "folder", Default = "" },
        new ParamDef { Key = "save_mode", Label = "整图保存", Kind = "choice", Default = "全部", Choices = ["全部", "仅OK", "仅NG"] },
        new ParamDef { Key = "show_binary", Label = "显示二值预处理图(调参辅助)", Kind = "bool", Default = "false" },
        new ParamDef { Key = "scope_index", Label = "总范围ROI索引(检测项管理设置)", Kind = "hidden", Default = "-1" },
    ];

    /// <summary>单检测项分析结果（与 rois 按索引一一对应；停用项 Blobs 为空且 Enabled=false）。</summary>
    internal sealed record RoiResult(string Name, bool Enabled, List<BlobFeature> Blobs, RoiMeta Meta, Rect CropRect)
    {
        /// <summary>合格斑点数（通过特征筛选后保留的）。</summary>
        public int Count => Blobs.Count;

        /// <summary>最大斑点面积；无斑点 = 0。</summary>
        public double MaxArea => Blobs.Count == 0 ? 0.0 : Blobs.Max(b => b.Area);

        /// <summary>斑点总面积。</summary>
        public double TotalArea => Blobs.Sum(b => b.Area);

        public static RoiResult Disabled(string name) => new(name, false, [], new RoiMeta(), default);
    }

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["decision_mode"] = DecisionDetectNg,
        ["threshold_type"] = BlobAnalyzer.ThFixed,
        ["polarity"] = PolarityBright,
        ["threshold"] = "100",
        ["threshold_high"] = "150",
        ["connectivity"] = "8邻接",
        ["hole_min_area"] = "0",
        ["find_num"] = "100",
        ["select_by_area"] = "true",
        ["min_area"] = "10",
        ["max_area"] = "999999999",
        ["select_by_perimeter"] = "false",
        ["min_perimeter"] = "10",
        ["max_perimeter"] = "999999999",
        ["select_by_circularity"] = "false",
        ["min_circularity"] = "0.1",
        ["max_circularity"] = "1",
        ["select_by_rectangularity"] = "false",
        ["min_rectangularity"] = "0.1",
        ["max_rectangularity"] = "1",
        ["select_by_long_axis"] = "false",
        ["min_long_axis"] = "10",
        ["max_long_axis"] = "999999999",
        ["select_by_short_axis"] = "false",
        ["min_short_axis"] = "1",
        ["max_short_axis"] = "999999999",
        ["sort_feature"] = BlobAnalyzer.SortArea,
        ["sort_mode"] = BlobAnalyzer.SortDesc,
        ["crop_dir"] = "",
        ["save_mode"] = "全部",
        ["show_binary"] = "false",
        ["scope_index"] = "-1",
    };

    /// <summary>切图/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public BlobNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "Blob";
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

        var thresholdType = _params.GetValueOrDefault("threshold_type") ?? BlobAnalyzer.ThFixed;
        var brightOnDark = !string.Equals(_params.GetValueOrDefault("polarity"), PolarityDark, StringComparison.Ordinal);
        var threshold = ParseDouble(_params.GetValueOrDefault("threshold"), 100);
        var thresholdHigh = ParseDouble(_params.GetValueOrDefault("threshold_high"), 150);
        var conn4 = string.Equals(_params.GetValueOrDefault("connectivity"), "4邻接", StringComparison.Ordinal);
        var holeMinArea = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("hole_min_area"), 0));
        var findNum = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("find_num"), 100));
        var filter = new BlobFilter(
            ByArea: ParseBool(_params.GetValueOrDefault("select_by_area"), true),
            MinArea: ParseDouble(_params.GetValueOrDefault("min_area"), 10),
            MaxArea: ParseDouble(_params.GetValueOrDefault("max_area"), 999999999),
            ByPerimeter: ParseBool(_params.GetValueOrDefault("select_by_perimeter"), false),
            MinPerimeter: ParseDouble(_params.GetValueOrDefault("min_perimeter"), 10),
            MaxPerimeter: ParseDouble(_params.GetValueOrDefault("max_perimeter"), 999999999),
            ByCircularity: ParseBool(_params.GetValueOrDefault("select_by_circularity"), false),
            MinCircularity: ParseDouble(_params.GetValueOrDefault("min_circularity"), 0.1),
            MaxCircularity: ParseDouble(_params.GetValueOrDefault("max_circularity"), 1),
            ByRectangularity: ParseBool(_params.GetValueOrDefault("select_by_rectangularity"), false),
            MinRectangularity: ParseDouble(_params.GetValueOrDefault("min_rectangularity"), 0.1),
            MaxRectangularity: ParseDouble(_params.GetValueOrDefault("max_rectangularity"), 1),
            ByLongAxis: ParseBool(_params.GetValueOrDefault("select_by_long_axis"), false),
            MinLongAxis: ParseDouble(_params.GetValueOrDefault("min_long_axis"), 10),
            MaxLongAxis: ParseDouble(_params.GetValueOrDefault("max_long_axis"), 999999999),
            ByShortAxis: ParseBool(_params.GetValueOrDefault("select_by_short_axis"), false),
            MinShortAxis: ParseDouble(_params.GetValueOrDefault("min_short_axis"), 1),
            MaxShortAxis: ParseDouble(_params.GetValueOrDefault("max_short_axis"), 999999999));
        var sortFeature = _params.GetValueOrDefault("sort_feature") ?? BlobAnalyzer.SortArea;
        var sortMode = _params.GetValueOrDefault("sort_mode") ?? BlobAnalyzer.SortDesc;
        var mode = _params.GetValueOrDefault("decision_mode") ?? DecisionDetectNg;
        var defaultThr = double.Parse(DefaultThreshold, CultureInfo.InvariantCulture);

        // ROI 解析：节点私有 own_rois（含检测项元数据）+ 位置修正；空 = 全图当单一检测区（按判定模式判定）
        var (rois, metas) = ResolveRoisFull(img, ctx);
        using var gray = CharRecNode.ToGray(img);
        var perRoi = new List<RoiResult>(Math.Max(1, rois.Count));

        if (rois.Count == 0)
        {
            using var bin = BlobAnalyzer.Binarize(gray, thresholdType, brightOnDark, threshold, thresholdHigh);
            var blobs = BlobAnalyzer.Analyze(gray, bin, conn4 ? 4 : 8, holeMinArea, findNum, filter, sortFeature, sortMode);
            perRoi.Add(new RoiResult("", true, blobs, new RoiMeta(), new Rect(0, 0, img.Width, img.Height)));
        }
        else
        {
            for (var ri = 0; ri < rois.Count; ri++)
            {
                var (name, rect) = rois[ri];
                var meta = ri < metas.Count ? metas[ri] : new RoiMeta();
                if (!meta.Enabled)
                {
                    perRoi.Add(RoiResult.Disabled(name)); // 停用：不检测，输出 roi_名称=停用
                    continue;
                }
                var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
                var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, img.Width, img.Height);
                List<BlobFeature> blobs;
                using (var cropView = new Mat(gray, cropRect))
                using (var bin = BlobAnalyzer.Binarize(cropView, thresholdType, brightOnDark, threshold, thresholdHigh))
                using (var preciseMask = NodeRois.CreateMask(rect, img.Width, img.Height))
                using (var preciseCrop = new Mat(preciseMask, cropRect))
                {
                    Cv2.BitwiseAnd(bin, preciseCrop, bin);
                    blobs = BlobAnalyzer.Analyze(cropView, bin, conn4 ? 4 : 8, holeMinArea, findNum, filter, sortFeature, sortMode);
                }
                // 斑点坐标从 ROI 裁剪区映射回源图（轮廓/外接框/质心）
                for (var bi = 0; bi < blobs.Count; bi++)
                {
                    var b = blobs[bi];
                    blobs[bi] = b with
                    {
                        Bound = new Rect(b.Bound.X + cropRect.X, b.Bound.Y + cropRect.Y, b.Bound.Width, b.Bound.Height),
                        Contour = b.Contour.Select(p => new Point(p.X + cropRect.X, p.Y + cropRect.Y)).ToArray(),
                        CentroidX = b.CentroidX + cropRect.X,
                        CentroidY = b.CentroidY + cropRect.Y,
                        BoxCx = b.BoxCx + cropRect.X,
                        BoxCy = b.BoxCy + cropRect.Y,
                    };
                }
                perRoi.Add(new RoiResult(name, true, blobs, meta, cropRect));
            }

            // 总范围约束：scope_index 指向的 ROI 为总范围，其余 ROI 的斑点只保留质心落在总范围内的
            var scopeIndex = NodeRois.ParseScopeIndex(_params);
            if (scopeIndex >= 0 && scopeIndex < rois.Count)
            {
                var scope = rois[scopeIndex].Rect;
                var removed = 0;
                for (var ri = 0; ri < perRoi.Count; ri++)
                {
                    if (ri == scopeIndex || !perRoi[ri].Enabled) continue;
                    var kept = perRoi[ri].Blobs
                        .Where(b => NodeRois.ContainsPixel(scope, b.CentroidX, b.CentroidY, img.Width, img.Height))
                        .ToList();
                    removed += perRoi[ri].Blobs.Count - kept.Count;
                    perRoi[ri] = perRoi[ri] with { Blobs = kept };
                }
                if (removed > 0)
                {
                    Log?.Invoke($"[总范围] {Name}: 按总范围 ROI「{rois[scopeIndex].Name}」过滤，移除范围外斑点 {removed} 个");
                }
            }
        }

        // 判定：启用且参与判定项任一不过 → NG
        var decision = ComputeDecision(mode, perRoi, defaultThr);

        // 斑点区切图 + 整图留存（按节点判定分 OK/NG 目录）
        SaveBlobCrops(img, perRoi, decision);
        SaveWholeImage(img, decision);

        // 输出图：调参辅助开关打开时显示二值化预处理图（看到分割器看到什么，调 阈值/极性 时每次执行立见效果）；
        // 否则输出原图克隆（框线/文字由 UI 矢量叠加层渲染，不进位图）
        Mat output;
        if (ParseBool(_params.GetValueOrDefault("show_binary"), false))
        {
            using var binPreview = BlobAnalyzer.Binarize(gray, thresholdType, brightOnDark, threshold, thresholdHigh);
            output = new Mat();
            Cv2.CvtColor(binPreview, output, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            output = BuildBaseImage(img);
        }

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = output,
        };

        // 矢量标注：斑点轮廓+面积/质心（检测项NG红/正常绿，含整图模式）+ 检测项四边形+斑点数（NG红/停用灰黄）
        var observing = string.Equals(mode, DecisionObserveOnly, StringComparison.Ordinal); // 仅检出不判定：标注不染红，与 OK 判定一致
        for (var ri = 0; ri < perRoi.Count; ri++)
        {
            var item = perRoi[ri];
            if (!item.Enabled) continue;
            var itemNg = !observing && NodeRois.Judges(item.Meta)
                && !CheckPass(mode, item.Count, item.MaxArea, item.Meta.ThresholdValue ?? defaultThr);
            foreach (var b in item.Blobs)
            {
                nodeResult.Annotations.Add(new NodeShape
                {
                    Polys = [b.Contour],
                    Label = $"面积{b.Area:F0} ({b.CentroidX:F0},{b.CentroidY:F0})",
                    Kind = itemNg ? NodeShapeKind.Defect : NodeShapeKind.Ok,
                });
            }
        }
        for (var ri = 0; ri < rois.Count && ri < perRoi.Count; ri++)
        {
            var (name, rect) = rois[ri];
            var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
            var item = perRoi[ri];
            var itemNg = !observing && item.Enabled && NodeRois.Judges(item.Meta)
                && !CheckPass(mode, item.Count, item.MaxArea, item.Meta.ThresholdValue ?? defaultThr);
            nodeResult.Annotations.Add(new NodeShape
            {
                Polys = [PatchCoreNode.RoiQuad(cx, cy, w, h, angle)],
                Label = item.Enabled ? $"{name}:{item.Count}个" : $"{name}(停用)",
                Kind = itemNg ? NodeShapeKind.Defect : NodeShapeKind.Info,
            });
        }

        // 输出值：斑点数/总面积/最大面积 + 逐检测项 count/area/total
        var enabledItems = perRoi.Where(r => r.Enabled).ToList();
        nodeResult.Values["count"] = enabledItems.Sum(i => i.Count).ToString();
        nodeResult.Values["total_area"] = enabledItems.Sum(i => i.TotalArea).ToString("F0", CultureInfo.InvariantCulture);
        nodeResult.Values["max_area"] = (enabledItems.Count == 0 ? 0.0 : enabledItems.Max(i => i.MaxArea))
            .ToString("F0", CultureInfo.InvariantCulture);
        foreach (var item in perRoi)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue; // 整图模式无检测项名
            var key = $"roi_{item.Name}";
            if (!item.Enabled)
            {
                nodeResult.Values[key] = "停用";
                continue;
            }
            nodeResult.Values[key] = item.Count.ToString();
            nodeResult.Values[$"{key}_area"] = item.MaxArea.ToString("F0", CultureInfo.InvariantCulture);
            nodeResult.Values[$"{key}_total"] = item.TotalArea.ToString("F0", CultureInfo.InvariantCulture);
        }

        return nodeResult;
    }

    /// <summary>
    /// 判定（纯逻辑，可单测）：仅检出不判定 → 恒 OK；
    /// 否则逐启用且参与判定项 CheckPass，任一不过 → NG。
    /// </summary>
    internal static string ComputeDecision(string mode, IReadOnlyList<RoiResult> items, double defaultThreshold)
    {
        if (string.Equals(mode, DecisionObserveOnly, StringComparison.Ordinal)) return "OK";
        foreach (var item in items)
        {
            if (!item.Enabled || !NodeRois.Judges(item.Meta)) continue;
            if (!CheckPass(mode, item.Count, item.MaxArea, item.Meta.ThresholdValue ?? defaultThreshold))
            {
                return "NG";
            }
        }
        return "OK";
    }

    /// <summary>
    /// 单检测项判定（纯逻辑）：检出即NG[默认]=合格斑点数&gt;0 → 不过；
    /// 缺失即NG=数量=0 → 不过；数量≥阈值即NG=数量≥阈值 → 不过；最大面积≥阈值即NG=最大面积≥阈值 → 不过。
    /// </summary>
    internal static bool CheckPass(string mode, int count, double maxArea, double threshold)
    {
        if (string.Equals(mode, DecisionMissingNg, StringComparison.Ordinal)) return count > 0;
        if (string.Equals(mode, DecisionCountNg, StringComparison.Ordinal)) return count < threshold;
        if (string.Equals(mode, DecisionAreaNg, StringComparison.Ordinal)) return maxArea < threshold;
        return count <= 0; // 检出即NG（默认/未知模式兜底）
    }

    /// <summary>解析本节点的检测区域：节点私有 own_rois（含检测项元数据）+ 位置修正（若本节点在修正目标列表中）。</summary>
    private (List<(string Name, RoiRect Rect)> Rois, List<RoiMeta> Metas) ResolveRoisFull(Mat img, PipelineRunContext ctx)
    {
        var items = NodeRois.ParseOwnFull(_params.GetValueOrDefault("own_rois"));
        var rois = NodeRois.ApplyPoseCorrection(
            items.Select(i => (i.Name, i.Rect)).ToList(), Name, img.Width, img.Height, ctx.PoseCorrection);
        return (rois, items.Select(i => i.Meta).ToList());
    }

    /// <summary>输出底图：3 通道 BGR 克隆（框/文字改由 UI 矢量叠加层渲染，不进位图）。</summary>
    private static Mat BuildBaseImage(Mat img)
    {
        if (img.Channels() == 3) return img.Clone();
        var mat = new Mat();
        Cv2.CvtColor(img, mat, img.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return mat;
    }

    /// <summary>斑点区切图：crop_dir\OK|NG\时间戳_ROI名_序号.jpg + sidecar（面积/周长/质心/外接框角度/圆度/矩形度）；检测项级「是否存图」过滤；目录未配置记日志。</summary>
    private void SaveBlobCrops(Mat img, IReadOnlyList<RoiResult> items, string decision)
    {
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir))
        {
            var pending = items.Where(i => i.Enabled && i.Count > 0).Sum(i => i.Count);
            if (pending > 0)
            {
                Log?.Invoke($"[切图] {Name}: 未配置「斑点区切图保存目录」(crop_dir)，本次 {pending} 个斑点未保存切图");
            }
            return;
        }
        var sub = decision == "NG" ? "NG" : "OK";
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var idx = 0; // 跨检测项全局编号：重名检测项（RoiManagerDialog 允许）文件名不互覆
        foreach (var item in items)
        {
            if (!item.Enabled || !item.Meta.SaveImage) continue; // 停用/检测项级不存图跳过
            for (var i = 0; i < item.Blobs.Count; i++)
            {
                var b = item.Blobs[i];
                try
                {
                    var box = ClampRect(b.Bound.X, b.Bound.Y, b.Bound.Width, b.Bound.Height, img.Width, img.Height);
                    using var crop = new Mat(img, box).Clone();
                    var full = Path.GetFullPath(Path.Combine(dir, sub));
                    Directory.CreateDirectory(full);
                    var outPath = Path.Combine(full, $"{ts}_{SafeFileText(string.IsNullOrWhiteSpace(item.Name) ? "整图" : item.Name)}_{idx++}.jpg");
                    Cv2.ImWrite(outPath, crop);
                    File.WriteAllText(outPath + ".json", System.Text.Json.JsonSerializer.Serialize(new
                    {
                        roi_name = item.Name,
                        index = i,
                        area = b.Area,
                        perimeter = b.Perimeter,
                        centroid = new[] { b.CentroidX, b.CentroidY },
                        box_angle = b.BoxAngle,
                        circularity = b.Circularity,
                        rectangularity = b.Rectangularity,
                        created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                    }));
                    Log?.Invoke($"[切图] {Name}: 已保存 {sub} 斑点区切图 {item.Name}#{i}(面积{b.Area:F0}) -> {outPath}");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[切图] {Name}: 保存失败: {ex.Message}");
                }
            }
        }
    }

    /// <summary>整图留存：按本节点判定把原图存到 crop_dir\OK|NG（save_mode = 全部/仅OK/仅NG）。失败只记日志不中断。</summary>
    private void SaveWholeImage(Mat img, string decision)
    {
        if (!SaveImageNode.ShouldSave(_params.GetValueOrDefault("save_mode"), decision)) return;
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir))
        {
            Log?.Invoke($"[整图] {Name}: 整图保存已启用(保存类型={_params.GetValueOrDefault("save_mode")})但未配置「斑点区切图保存目录」(crop_dir)，本次不保存");
            return;
        }
        try
        {
            var sub = decision == "NG" ? "NG" : "OK";
            var full = Path.GetFullPath(Path.Combine(dir, sub));
            Directory.CreateDirectory(full);
            var outPath = Path.Combine(full, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{SafeFileText(Name)}.jpg");
            Cv2.ImWrite(outPath, img);
            Log?.Invoke($"[整图] {Name}: 已保存 {sub} 整图 -> {outPath}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[整图] {Name}: 整图保存失败: {ex.Message}");
        }
    }

    /// <summary>矩形钳制到图像内（永不抛异常；最小 1×1）。</summary>
    private static Rect ClampRect(int x, int y, int w, int h, int maxW, int maxH)
    {
        x = Math.Clamp(x, 0, Math.Max(0, maxW - 1));
        y = Math.Clamp(y, 0, Math.Max(0, maxH - 1));
        w = Math.Clamp(Math.Min(w, maxW - x), 1, Math.Max(1, maxW - x));
        h = Math.Clamp(Math.Min(h, maxH - y), 1, Math.Max(1, maxH - y));
        return new Rect(x, y, w, h);
    }

    /// <summary>文本 → 文件名安全段（剔除路径非法字符）。</summary>
    private static string SafeFileText(string text) =>
        string.Join("_", text.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>布尔参数解析（1/true/是 = 开；空/非法回退默认值）。</summary>
    private static bool ParseBool(string? raw, bool fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback
        : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v != 0
        : raw is "true" or "True" or "是";

    public void Dispose()
    {
        // 无原生资源/外部模型（二值化与连通域全部即时分配即用即释放）
    }
}
