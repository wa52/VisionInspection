using System.IO;
using OpenCvSharp;
using VisionInspection.Models;
using VisionInspection.Services;

namespace VisionInspection.Detection;

/// <summary>PatchCore 模型节点：包装 PatchCoreRuntime + 预处理。threshold 为节点参数（空则用模型自带阈值）。
/// 支持单个归一化 ROI：只把 ROI 区域喂给模型检测；展示图像可选热力图/检测图；可按判定保存 ROI 切图。</summary>
public sealed class PatchCoreNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "model_dir", Label = "模型路径", Kind = "folder" },
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "display_image", Label = "展示图像", Kind = "choice", Default = "热力图", Choices = ["热力图", "检测图"] },
        new ParamDef { Key = "crop_dir", Label = "ROI切图保存目录(可选·按OK/NG分目录)", Kind = "folder", Default = "" },
        new ParamDef { Key = "vm_label", Label = "VM图像标识(可选)", Kind = "string", Default = "" },
        new ParamDef { Key = "vm_image_dir", Label = "VM图像目录(可选)", Kind = "folder", Default = "" },
        new ParamDef { Key = "score_dir", Label = "打分目录(可选)", Kind = "folder", Default = "" },
    ];

    /// <summary>检测项默认阈值（节点级总阈值已移除；检测项阈值缺省时回退模型训练阈值）。</summary>
    public const string DefaultThreshold = "0.5";

    private PatchCoreRuntime? _runtime;
    private readonly Dictionary<string, string> _params = new()
    {
        // 默认从 bin\Debug\net8.0-windows 向上 4 级到仓库根（新增节点开箱即用；可在参数里改绝对路径）
        ["model_dir"] = @"..\..\..\..\patchcore train\models\foam_patchcore",
        ["source"] = "@input",
        ["display_image"] = "热力图",
        ["crop_dir"] = "",
        ["vm_label"] = "",
        ["vm_image_dir"] = "",
        ["score_dir"] = "",
    };

    /// <summary>失配/切图保存日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public string Name { get; set; } = "PatchCore";
    public string Type => "PatchCore";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    /// <summary>运行时模型目录（已解析绝对路径）。</summary>
    public string ResolvedModelDir { get; private set; } = "";

    public PatchCoreNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public void SetParam(string key, string value)
    {
        var oldModelDir = _params.GetValueOrDefault("model_dir");
        _params[key] = value;
        // 只有模型目录变化才需要重载运行时；ROI/展示/切图目录参数直接热生效（避免每敲一键重载模型）
        if (key == "model_dir" && value != oldModelDir)
        {
            _runtime?.Dispose();
            _runtime = null;
        }
    }

    /// <summary>确保运行时已加载（模型目录解析为绝对路径后加载）。</summary>
    public void EnsureLoaded(string baseDir)
    {
        if (_runtime != null) return;
        ResolvedModelDir = RecipeStore.Resolve(new Recipe { BaseDir = baseDir }, _params["model_dir"]);
        _runtime = new PatchCoreRuntime(ResolvedModelDir);
    }

    /// <summary>生效阈值：模型自带训练阈值（检测项阈值缺省时回退此值）。</summary>
    public double? EffectiveThreshold => _runtime?.Threshold;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        if (_runtime == null)
        {
            throw new InvalidOperationException(
                $"节点 {Name}: 模型未加载（构建时加载失败，请检查「模型路径」: {ResolvedModelDir}）");
        }

        // 图像来源：@input（生产=相机帧；单次/连续执行=流水线输入）或上游节点输出（如图像源节点）
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

        // 归一化输入为 3 通道 BGR：二值化等上游节点可能输出单通道图（灰度→BGR 后走标准预处理与热力合成）
        Mat detectSrc = img;
        using var normalized = new Mat();
        if (img.Channels() == 1)
        {
            Cv2.CvtColor(img, normalized, ColorConversionCodes.GRAY2BGR);
            detectSrc = normalized;
        }
        else if (img.Channels() == 4)
        {
            Cv2.CvtColor(img, normalized, ColorConversionCodes.BGRA2BGR);
            detectSrc = normalized;
        }

        // ROI 解析：rois 引用方案级 ROI 库（多 ROI）；旧版单 roi 数值参数兼容保留
        var (rois, metas) = ResolveNodeRoisFull(detectSrc, ctx);
        var modelThreshold = EffectiveThreshold;

        List<(string Name, RoiRect Rect, double Score, string Decision, float[,] PatchMap, Mat Crop, bool SaveImage)> results = new();
        double score;
        string decision;
        float[,] heatMap;

        if (rois.Count == 0)
        {
            // 全图检测（原行为；无检测项 → 用模型训练阈值）
            var det = _runtime.Detect(detectSrc);
            score = det.Score;
            heatMap = det.PatchMap;
            decision = modelThreshold is not null && det.Score > modelThreshold.Value ? "NG" : "OK";
        }
        else
        {
            for (var ri = 0; ri < rois.Count; ri++)
            {
                var (name, rect) = rois[ri];
                var meta = ri < metas.Count ? metas[ri] : new RoiMeta();
                if (!meta.Enabled)
                {
                    Log?.Invoke($"[ROI] {Name}/{name}: 停用，跳过检测");
                    continue;
                }
                // 检测项级阈值（缺省回退模型训练阈值）；仅观察项只记分不判 NG
                var thr = meta.ThresholdValue ?? modelThreshold;
                using var crop = CropRoiView(detectSrc, rect);
                var det = _runtime.Detect(crop);
                var d = NodeRois.Judges(meta)
                    ? (thr is not null && det.Score > thr.Value ? "NG" : "OK")
                    : "观察";
                results.Add((name, rect, det.Score, d, det.PatchMap, crop.Clone(), meta.SaveImage));
                Log?.Invoke($"[ROI] {Name}/{name}: score={det.Score:F4} {d}{(thr is not null ? $" (阈值 {thr.Value:F4})" : "")}");
            }

            score = results.Count > 0 ? results.Max(r => r.Score) : 0.0;
            heatMap = results.Count > 0 ? results.OrderByDescending(r => r.Score).First().PatchMap : new float[0, 0];
            decision = results.Any(r => r.Decision == "NG") ? "NG" : "OK"; // 观察项/停用项不影响判定
        }

        // 合成输出图（进缩略图条 / 下游 SaveImage 可引用）；框线/文字走矢量标注（UI 屏幕常量渲染）
        var display = _params.GetValueOrDefault("display_image") ?? "热力图";
        var annotations = new List<NodeShape>();
        Mat output;
        if (rois.Count == 0)
        {
            output = display == "检测图"
                ? BuildInspectImage(detectSrc, null, score, decision, annotations)
                : BuildHeatImage(detectSrc, null, heatMap, modelThreshold, annotations);
        }
        else
        {
            var roiList = results
                .Select(r => (Rect: r.Rect, Name: r.Name, Score: r.Score, Decision: r.Decision, PatchMap: r.PatchMap))
                .ToList();
            output = display == "检测图"
                ? BuildInspectImageMulti(detectSrc, roiList.Select(r => (r.Rect, r.Name, r.Score, r.Decision)).ToList(), annotations)
                : BuildHeatImageMulti(detectSrc, roiList.Select(r => (r.Rect, r.Name, r.PatchMap)).ToList(), modelThreshold, annotations);
        }

        // ROI 切图保存（按节点自身判定分 OK/NG 目录；文件名含 ROI 名；检测项级「是否存图」过滤）；目录未配置时记一次日志
        var cropDir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(cropDir) && results.Any(r => r.SaveImage))
        {
            Log?.Invoke($"[切图] {Name}: 未配置「ROI切图保存目录」(crop_dir)，本次 {results.Count(r => r.SaveImage)} 个检测项切图未保存");
        }
        foreach (var r in results)
        {
            if (r.SaveImage)
            {
                SaveCrop(r.Crop, decision, r.Score, r.Name, r.Rect);
            }
            r.Crop.Dispose();
        }

        var nodeResult = new NodeResult
        {
            Decision = decision,
            HeatMap = heatMap,
            OutputImage = output,
            Threshold = modelThreshold,
        };
        foreach (var shape in annotations)
        {
            nodeResult.Annotations.Add(shape);
        }
        foreach (var kv in BuildValues(results, score, modelThreshold))
        {
            nodeResult.Values[kv.Key] = kv.Value;
        }
        return nodeResult;
    }

    /// <summary>输出值：score（多 ROI 取最大）+ threshold + 每个 ROI 的独立分数（roi_名称）。</summary>
    private Dictionary<string, string> BuildValues(
        List<(string Name, RoiRect Rect, double Score, string Decision, float[,] PatchMap, Mat Crop, bool SaveImage)> results,
        double score, double? threshold)
    {
        var values = new Dictionary<string, string>
        {
            ["score"] = score.ToString("F4"),
            ["threshold"] = threshold?.ToString("F4") ?? "",
        };
        foreach (var r in results)
        {
            var key = $"roi_{r.Name}";
            var i = 2;
            while (values.ContainsKey(key))
            {
                key = $"roi_{r.Name}_{i++}";
            }
            values[key] = r.Score.ToString("F4");
        }
        return values;
    }

    /// <summary>
    /// 解析本节点的检测区域列表：节点私有 own_rois（方案级 ROI 库已移除）；
    /// own_rois 为空时回退旧版单 roi 数值参数（名称记为「ROI」）；两者都空 → 空列表（全图检测）。
    /// </summary>
    /// <summary>
    /// 解析本节点的检测区域：节点私有 own_rois（含检测项元数据）+ 位置修正；旧版单 roi 数值参数兼容保留（默认元数据）。
    /// 返回的 rois 与 metas 按索引一一对应。
    /// </summary>
    private (List<(string Name, RoiRect Rect)> Rois, List<RoiMeta> Metas) ResolveNodeRoisFull(Mat img, PipelineRunContext ctx)
    {
        var items = NodeRois.ParseOwnFull(_params.GetValueOrDefault("own_rois"));
        if (items.Count == 0)
        {
            var legacy = RoiRect.Parse(_params.GetValueOrDefault("roi"));
            if (legacy is not null)
            {
                items.Add(new RoiItem("ROI", legacy.Value, new RoiMeta()));
            }
        }
        var rois = NodeRois.ApplyPoseCorrection(
            items.Select(i => (i.Name, i.Rect)).ToList(), Name, img.Width, img.Height, ctx.PoseCorrection);
        return (rois, items.Select(i => i.Meta).ToList());
    }

    /// <summary>
    /// ROI 裁剪（支持旋转）：无角度时取共享视图；有角度时用 warpAffine 把旋转区域转正为 w×h 拷贝（调用方负责释放）。
    /// </summary>
    private static Mat CropRoiView(Mat img, RoiRect roi)
    {
        var (cx, cy, w, h, angle) = roi.ToPixels(img.Width, img.Height);
        if (Math.Abs(RoiRect.NormalizeAngle(angle)) < 0.01)
        {
            return new Mat(img, new Rect((int)(cx - w / 2), (int)(cy - h / 2), w, h)).Clone();
        }

        using var m = BuildCropMatrix(cx, cy, w, h, angle);
        var dst = new Mat();
        Cv2.WarpAffine(img, dst, m, new Size(w, h));
        return dst;
    }

    /// <summary>裁剪矩阵：dst(x,y) = src(R(a)·(x-w/2, y-h/2) + c) —— 把屏幕顺时针旋转 a 的 ROI 区域转正取样。</summary>
    internal static Mat BuildCropMatrix(double cx, double cy, double w, double h, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var tx = cx - (cos * (w / 2.0) - sin * (h / 2.0));
        var ty = cy - (sin * (w / 2.0) + cos * (h / 2.0));
        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, cos);
        m.Set(0, 1, -sin);
        m.Set(0, 2, tx);
        m.Set(1, 0, sin);
        m.Set(1, 1, cos);
        m.Set(1, 2, ty);
        return m;
    }

    /// <summary>贴回矩阵：full(x,y) = heat(R(-a)·(x,y) + R(-a)·(-c) + (w/2,h/2))——把转正内容按 ROI 位置/角度铺回全图。</summary>
    internal static Mat BuildPasteMatrix(double cx, double cy, double w, double h, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var tx = (w / 2.0) - (cos * cx + sin * cy);
        var ty = (h / 2.0) - (-sin * cx + cos * cy);
        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, cos);
        m.Set(0, 1, sin);
        m.Set(0, 2, tx);
        m.Set(1, 0, -sin);
        m.Set(1, 1, cos);
        m.Set(1, 2, ty);
        return m;
    }

    /// <summary>ROI 四角（图像像素坐标，屏幕顺时针 angle）。</summary>
    internal static OpenCvSharp.Point[] RoiQuad(double cx, double cy, double w, double h, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        (double Lx, double Ly)[] locals =
        [
            (-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2),
        ];
        return locals.Select(p => new OpenCvSharp.Point(
            (int)Math.Round(cx + cos * p.Lx - sin * p.Ly),
            (int)Math.Round(cy + sin * p.Lx + cos * p.Ly))).ToArray();
    }

    /// <summary>检测图：原图克隆 + 矢量标注（四边形框/score 角标由 UI 渲染）。无 ROI 返回原图克隆。</summary>
    internal static Mat BuildInspectImage(Mat img, RoiRect? roi, double score, string decision, List<NodeShape>? annotations = null)
    {
        var mat = img.Clone();
        if (roi is null) return mat;
        var (cx, cy, w, h, angle) = roi.Value.ToPixels(img.Width, img.Height);
        annotations?.Add(new NodeShape
        {
            Polys = [RoiQuad(cx, cy, w, h, angle)],
            Label = $"{score:F3}",
            Kind = decision == "NG" ? NodeShapeKind.Defect : NodeShapeKind.Ok,
        });
        return mat;
    }

    /// <summary>热力图：ROI 外保持原图，ROI 内贴热力（旋转区域用掩膜贴回）+ 矢量框。无 ROI = 热力与原图全图叠加。</summary>
    internal static Mat BuildHeatImage(Mat img, RoiRect? roi, float[,] patchMap, double? threshold, List<NodeShape>? annotations = null)
    {
        var mat = img.Clone();
        using var heat = HeatMapToMat(patchMap, threshold);
        if (roi is null)
        {
            using var heatFull = new Mat();
            Cv2.Resize(heat, heatFull, mat.Size());
            Cv2.AddWeighted(mat, 0.4, heatFull, 0.6, 0, mat);
            return mat;
        }

        var (cx, cy, w, h, angle) = roi.Value.ToPixels(img.Width, img.Height);
        if (Math.Abs(RoiRect.NormalizeAngle(angle)) < 0.01)
        {
            // 无旋转：直接矩形混合
            var rect = new Rect((int)(cx - w / 2), (int)(cy - h / 2), w, h);
            using var heatRoi = new Mat();
            Cv2.Resize(heat, heatRoi, rect.Size);
            using var roiView = new Mat(mat, rect);
            Cv2.AddWeighted(roiView, 0.35, heatRoi, 0.65, 0, roiView);
            annotations?.Add(new NodeShape { Box = rect, Kind = NodeShapeKind.Ok });
            return mat;
        }

        // 旋转：热力铺回整图（掩膜内覆盖）+ 矢量四边形框
        using var mPaste = BuildPasteMatrix(cx, cy, w, h, angle);
        using var heatCanvas = new Mat(img.Rows, img.Cols, MatType.CV_8UC3, Scalar.All(0));
        Cv2.WarpAffine(heat, heatCanvas, mPaste, img.Size());
        using var maskWhite = new Mat(w, h, MatType.CV_8UC1, Scalar.All(255));
        using var maskCanvas = new Mat();
        Cv2.WarpAffine(maskWhite, maskCanvas, mPaste, img.Size());
        using var maskBin = new Mat();
        Cv2.Threshold(maskCanvas, maskBin, 127, 255, ThresholdTypes.Binary);
        heatCanvas.CopyTo(mat, maskBin);
        annotations?.Add(new NodeShape { Polys = [RoiQuad(cx, cy, w, h, angle)], Kind = NodeShapeKind.Ok });
        return mat;
    }

    /// <summary>多 ROI 检测图：原图克隆（各 ROI 四边形框+ROI名/分数改由 UI 矢量叠加层渲染）。</summary>
    internal static Mat BuildInspectImageMulti(
        Mat img, List<(RoiRect Rect, string Name, double Score, string Decision)> rois, List<NodeShape>? annotations = null)
    {
        var mat = img.Clone();
        foreach (var r in rois)
        {
            var (cx, cy, w, h, angle) = r.Rect.ToPixels(img.Width, img.Height);
            annotations?.Add(new NodeShape
            {
                Polys = [RoiQuad(cx, cy, w, h, angle)],
                Label = $"{r.Name} {r.Score:F3}",
                Kind = r.Decision == "NG" ? NodeShapeKind.Defect : NodeShapeKind.Ok,
            });
        }
        return mat;
    }

    /// <summary>多 ROI 热力图：ROI 外保持原图，每个 ROI 内贴各自热力（旋转区域掩膜贴回）+ 矢量框。</summary>
    internal static Mat BuildHeatImageMulti(
        Mat img, List<(RoiRect Rect, string Name, float[,] PatchMap)> rois, double? threshold, List<NodeShape>? annotations = null)
    {
        var mat = img.Clone();
        foreach (var r in rois)
        {
            using var heat = HeatMapToMat(r.PatchMap, threshold);
            var (cx, cy, w, h, angle) = r.Rect.ToPixels(img.Width, img.Height);
            if (Math.Abs(RoiRect.NormalizeAngle(angle)) < 0.01)
            {
                var rect = new Rect((int)(cx - w / 2), (int)(cy - h / 2), w, h);
                using var heatRoi = new Mat();
                Cv2.Resize(heat, heatRoi, rect.Size);
                using var roiView = new Mat(mat, rect);
                Cv2.AddWeighted(roiView, 0.35, heatRoi, 0.65, 0, roiView);
                annotations?.Add(new NodeShape { Box = rect, Kind = NodeShapeKind.Ok });
            }
            else
            {
                using var mPaste = BuildPasteMatrix(cx, cy, w, h, angle);
                using var heatCanvas = new Mat(img.Rows, img.Cols, MatType.CV_8UC3, Scalar.All(0));
                Cv2.WarpAffine(heat, heatCanvas, mPaste, img.Size());
                using var maskWhite = new Mat(w, h, MatType.CV_8UC1, Scalar.All(255));
                using var maskCanvas = new Mat();
                Cv2.WarpAffine(maskWhite, maskCanvas, mPaste, img.Size());
                using var maskBin = new Mat();
                Cv2.Threshold(maskCanvas, maskBin, 127, 255, ThresholdTypes.Binary);
                heatCanvas.CopyTo(mat, maskBin);
                annotations?.Add(new NodeShape { Polys = [RoiQuad(cx, cy, w, h, angle)], Kind = NodeShapeKind.Ok });
            }
        }
        return mat;
    }

    /// <summary>ROI 切图保存：crop_dir\OK|NG\时间戳_ROI名_score.jpg + sidecar（ROI 指纹）。失败只记日志不中断检测。</summary>
    private void SaveCrop(Mat crop, string decision, double score, string roiName, RoiRect rect)
    {
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir)) return; // 未配置目录的日志在 Run 里统一记，避免每个 ROI 重复刷屏

        try
        {
            var sub = decision == "NG" ? "NG" : "OK";
            var full = Path.GetFullPath(Path.Combine(dir, sub));
            Directory.CreateDirectory(full);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var tag = string.IsNullOrWhiteSpace(roiName) ? "" : $"_{roiName}";
            var outPath = Path.Combine(full, $"{ts}{tag}_{score:F4}.jpg");
            Cv2.ImWrite(outPath, crop);
            // sidecar：记录切图时的 ROI 指纹（名称+几何），供状态页校验「切图与当前检测区域是否一致」
            File.WriteAllText(outPath + ".json", System.Text.Json.JsonSerializer.Serialize(new
            {
                roi_name = roiName,
                roi = rect.Serialize(),
                model_dir = ResolvedModelDir,
                score,
                created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            }));
            Log?.Invoke($"[切图] {Name}: 已保存 {sub} 切图 -> {outPath}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[切图] {Name}: 切图保存失败: {ex.Message}");
        }
    }

    /// <summary>热图 float[,] → 彩色 Mat（供下游 SaveImage 保存异常图）。归一化用阈值做参考最大值。</summary>
    internal static Mat HeatMapToMat(float[,] patchMap, double? threshold)
    {
        var h = patchMap.GetLength(0);
        var w = patchMap.GetLength(1);
        var refMax = threshold is > 0 ? threshold.Value : 0.0;
        var norm = new float[h, w];
        if (refMax > 0)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    norm[y, x] = Math.Clamp((float)(patchMap[y, x] / refMax), 0f, 1f);
        }
        else
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    min = Math.Min(min, patchMap[y, x]);
                    max = Math.Max(max, patchMap[y, x]);
                }
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    norm[y, x] = max > min ? (patchMap[y, x] - min) / (max - min) : 0f;
        }

        using var gray = new Mat(h, w, MatType.CV_8UC1);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                gray.At<byte>(y, x) = (byte)Math.Round(norm[y, x] * 255);

        var heat = new Mat();
        Cv2.ApplyColorMap(gray, heat, ColormapTypes.Jet);
        return heat;
    }

    public void Dispose()
    {
        _runtime?.Dispose();
        _runtime = null;
    }
}
