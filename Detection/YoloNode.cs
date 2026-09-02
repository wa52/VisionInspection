using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SpeakerVisionInspection.Models;
using SpeakerVisionInspection.Services;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// YOLO 目标检测节点（Ultralytics yolo11 标准导出 ONNX）。
/// 模型目录契约：best.onnx + classes.txt（每行一个类别名，u训练 训练产物）。
/// 判定模式：检出即NG（关注类别检出数>0 → NG，缺陷检测）；缺失即NG（关注类别一个都没检出 → NG，缺料/漏装检测）。
/// 切图：crop_dir 非空时，每个检测框裁剪保存到 crop_dir\OK|NG\时间戳_类别_置信度.jpg。
/// </summary>
public sealed class YoloNode : IModelNode
{
    public const string DecisionDetectNg = "检出即NG";
    public const string DecisionMissingNg = "缺失即NG";
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "model_dir", Label = "模型目录(best.onnx+classes.txt)", Kind = "folder" },
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "iou", Label = "NMS IoU阈值", Kind = "double", Default = "0.45" },
        new ParamDef { Key = "positive_classes", Label = "关注类别(逗号分隔,空=全部)", Kind = "string", Default = "" },
        new ParamDef { Key = "decision_mode", Label = "判定模式", Kind = "choice", Default = DecisionDetectNg, Choices = [DecisionDetectNg, DecisionMissingNg] },
        new ParamDef { Key = "crop_dir", Label = "检测框切图保存目录(可选·按OK/NG分目录)", Kind = "folder", Default = "" },
        new ParamDef { Key = "scope_index", Label = "总范围ROI索引(检测项管理设置)", Kind = "hidden", Default = "-1" },
    ];

    /// <summary>检测项默认置信度阈值（节点级总阈值已移除，检测项阈值缺省时回退此值）。</summary>
    public const string DefaultConf = "0.5";

    private InferenceSession? _session;
    private List<string> _classNames = new();
    private int _inputW = 640;
    private int _inputH = 640;
    private int _channels; // 模型输出通道数 4+nc
    private readonly Dictionary<string, string> _params = new()
    {
        ["model_dir"] = "",
        ["source"] = "@input",
        ["iou"] = "0.45",
        ["positive_classes"] = "",
        ["decision_mode"] = DecisionDetectNg,
        ["crop_dir"] = "",
        ["scope_index"] = "-1",
    };

    /// <summary>失配/切图日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public YoloNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "YOLO";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    /// <summary>运行时模型目录（已解析绝对路径）。</summary>
    public string ResolvedModelDir { get; private set; } = "";

    public void SetParam(string key, string value)
    {
        var oldModelDir = _params.GetValueOrDefault("model_dir");
        _params[key] = value;
        // 只有模型目录变化才需要重载会话；conf/iou/类别/ROI/切图目录热生效
        if (key == "model_dir" && value != oldModelDir)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>确保推理会话已加载（模型目录解析为绝对路径后加载 best.onnx + classes.txt）。</summary>
    public void EnsureLoaded(string baseDir)
    {
        if (_session != null) return;
        ResolvedModelDir = RecipeStore.Resolve(new Recipe { BaseDir = baseDir }, _params["model_dir"]);
        if (!Directory.Exists(ResolvedModelDir))
        {
            throw new DirectoryNotFoundException($"模型目录不存在: {ResolvedModelDir}");
        }

        var missing = new[] { "best.onnx", "classes.txt" }
            .Where(f => !File.Exists(Path.Combine(ResolvedModelDir, f)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException($"模型目录缺少文件: {string.Join(", ", missing)}（目录: {ResolvedModelDir}）");
        }

        // 类别表：UTF-8 每行一个类别；行数不足时回退「类别{i}」
        _classNames = File.ReadAllLines(Path.Combine(ResolvedModelDir, "classes.txt"), Encoding.UTF8)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var onnxPath = Path.Combine(ResolvedModelDir, "best.onnx");
        _session = new InferenceSession(onnxPath, CreateSessionOptions());
        inputName = _session.InputMetadata.First().Key;

        // 输入尺寸从模型元数据读取（默认 640）
        var input = _session.InputMetadata.First();
        var dims = input.Value.Dimensions;
        if (dims.Length >= 4 && dims[^1] > 0 && dims[^2] > 0)
        {
            _inputW = dims[^1];
            _inputH = dims[^2];
        }
    }

    private string inputName = "images";

    private static SessionOptions CreateSessionOptions()
    {
        var opt = new SessionOptions();
        opt.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        return opt;
    }

    private string ClassName(int index) =>
        index >= 0 && index < _classNames.Count ? _classNames[index] : $"类别{index}";

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        if (_session == null)
        {
            throw new InvalidOperationException(
                $"节点 {Name}: 模型未加载（构建时加载失败，请检查「模型目录」: {ResolvedModelDir}）");
        }

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

        // ROI 解析：节点私有 own_rois（含检测项元数据）+ 位置修正；空 = 全图检测
        var (rois, metas) = ResolveRoisFull(img, ctx);
        var positives = ParsePositiveClasses();
        var detections = new List<YoloDetection>();
        var perRoiCounts = new List<(string Name, int Triggered)>();
        var detRoiIdx = new List<int>(); // 与 detections 平行：每个检出来自哪个 ROI（总范围过滤/观察/存图判定用）
        using var normalized = Normalize3ch(img);

        if (rois.Count == 0)
        {
            detections.AddRange(Detect(normalized, float.Parse(DefaultConf, System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            for (var ri = 0; ri < rois.Count; ri++)
            {
                var (name, rect) = rois[ri];
                var meta = ri < metas.Count ? metas[ri] : new RoiMeta();
                if (!meta.Enabled)
                {
                    perRoiCounts.Add((name, -1)); // 停用：不检测，输出 roi_名称=停用
                    continue;
                }
                // 裁剪矩形只算一次，Detect 与坐标偏移共用，保证框映射一致；conf=检测项级阈值
                var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
                var cropRect = ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, img.Width, img.Height);
                using var cropView = new Mat(normalized, cropRect);
                var roiConf = (float)(meta.ThresholdValue ?? double.Parse(DefaultConf, System.Globalization.CultureInfo.InvariantCulture));
                var dets = Detect(cropView, roiConf).Select(d => d with { Cx = d.Cx + cropRect.X, Cy = d.Cy + cropRect.Y }).ToList();
                detections.AddRange(dets);
                detRoiIdx.AddRange(Enumerable.Repeat(ri, dets.Count));
                perRoiCounts.Add((name, dets.Count(d => positives.Count == 0 || positives.Contains(d.Class, StringComparer.OrdinalIgnoreCase))));
            }
        }

        // 总范围约束：scope_index 指向的 ROI 为总范围，其余 ROI 的检出只保留中心落在总范围内的
        var scopeIndex = NodeRois.ParseScopeIndex(_params);
        if (scopeIndex >= 0 && scopeIndex < rois.Count && scopeIndex < perRoiCounts.Count)
        {
            var beforeCount = detections.Count;
            (detections, detRoiIdx, perRoiCounts) = ApplyScopeFilter(
                detections, detRoiIdx, perRoiCounts, rois, scopeIndex,
                img.Width, img.Height, positives);
            Log?.Invoke($"[总范围] {Name}: 按总范围 ROI「{rois[scopeIndex].Name}」过滤，移除范围外检出 {beforeCount - detections.Count} 个");
        }

        // 触发类别过滤 + 判定（判定模式：检出即NG / 缺失即NG）；仅观察/停用项的检出不参与判定
        var triggered = detections
            .Where(d => positives.Count == 0 || positives.Contains(d.Class, StringComparer.OrdinalIgnoreCase))
            .Where((d, k) => detRoiIdx.Count != detections.Count ||
                             detRoiIdx[k] < 0 || detRoiIdx[k] >= metas.Count ||
                             NodeRois.Judges(metas[detRoiIdx[k]]))
            .ToList();
        var decision = ComputeDecision(
            _params.GetValueOrDefault("decision_mode"), triggered.Count, positives.Count > 0);

        // 输出图：3 通道底图（框/文字由 UI 矢量叠加层渲染，屏幕常量大小）
        var output = BuildBaseImage(img);

        // 切图：每个检测框裁剪保存（按判定分目录；检测项级「是否存图」过滤）
        if (!string.IsNullOrWhiteSpace(_params.GetValueOrDefault("crop_dir")))
        {
            SaveDetections(img, detections, triggered, decision, detRoiIdx, metas);
        }

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = output,
        };
        foreach (var shape in BuildAnnotations(img.Width, img.Height, rois, detections, triggered))
        {
            nodeResult.Annotations.Add(shape);
        }
        nodeResult.Values["count"] = triggered.Count.ToString();
        nodeResult.Values["det_all"] = detections.Count.ToString();
        nodeResult.Values["classes"] = string.Join(",", triggered.Select(d => d.Class).Distinct());
        nodeResult.Values["max_conf"] = triggered.Count > 0 ? triggered.Max(d => d.Conf).ToString("F3") : "0.000";
        foreach (var (name, n) in perRoiCounts)
        {
            nodeResult.Values[$"roi_{name}"] = n < 0 ? "停用" : n.ToString();
        }

        return nodeResult;
    }

    private HashSet<string> ParsePositiveClasses() =>
        (_params.GetValueOrDefault("positive_classes") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 判定（纯逻辑，可单测）：
    /// 检出即NG（默认，缺陷检测）：关注类别检出数 &gt; 0 → NG；
    /// 缺失即NG（缺料/漏装检测）：关注类别一个都没检出 → NG。
    /// hasPositiveFilter=false 表示未配置关注类别（全部检测都算"关注"）。
    /// </summary>
    internal static string ComputeDecision(string? mode, int triggeredCount, bool hasPositiveFilter)
    {
        var missingNg = string.Equals((mode ?? "").Trim(), DecisionMissingNg, StringComparison.Ordinal);
        if (missingNg)
        {
            // 缺失即NG：要求至少检出 1 个关注类别；未配置关注类别时要求有任何检测
            return triggeredCount > 0 ? "OK" : "NG";
        }
        return triggeredCount > 0 ? "NG" : "OK";
    }

    /// <summary>
    /// 解析本节点的检测区域：节点私有 own_rois（含检测项元数据）+ 位置修正（若本节点在修正目标列表中）。
    /// 返回的 rois 与 metas 按索引一一对应（ApplyPoseCorrection 1:1 保持数量）。
    /// </summary>
    private (List<(string Name, RoiRect Rect)> Rois, List<RoiMeta> Metas) ResolveRoisFull(Mat img, PipelineRunContext ctx)
    {
        var items = NodeRois.ParseOwnFull(_params.GetValueOrDefault("own_rois"));
        var rois = NodeRois.ApplyPoseCorrection(
            items.Select(i => (i.Name, i.Rect)).ToList(), Name, img.Width, img.Height, ctx.PoseCorrection);
        return (rois, items.Select(i => i.Meta).ToList());
    }

    /// <summary>
    /// 总范围过滤（纯逻辑，可单测）：scopeIndex 指向的 ROI 为总范围——
    /// 该 ROI 自身检出全部保留；其余 ROI 的检出只保留中心落在总范围内的。
    /// 返回过滤后的检出/ROI 归属/每 ROI 计数（scope ROI 计数不变，其余按过滤后重算）。
    /// </summary>
    internal static (List<YoloDetection> Dets, List<int> RoiIdx, List<(string Name, int Triggered)> Counts)
        ApplyScopeFilter(
            List<YoloDetection> detections, List<int> detRoiIdx, List<(string Name, int Triggered)> counts,
            List<(string Name, RoiRect Rect)> rois, int scopeIndex, int imgW, int imgH,
            HashSet<string> positives)
    {
        if (scopeIndex < 0 || scopeIndex >= rois.Count || detections.Count != detRoiIdx.Count)
        {
            return (detections, detRoiIdx, counts);
        }
        var scope = rois[scopeIndex].Rect;
        var kept = new List<YoloDetection>();
        var keptIdx = new List<int>();
        for (var k = 0; k < detections.Count; k++)
        {
            if (detRoiIdx[k] != scopeIndex &&
                !NodeRois.ContainsPixel(scope, detections[k].Cx, detections[k].Cy, imgW, imgH))
            {
                continue;
            }
            kept.Add(detections[k]);
            keptIdx.Add(detRoiIdx[k]);
        }
        var result = new List<(string, int)>(counts.Count);
        for (var i = 0; i < counts.Count; i++)
        {
            if (i == scopeIndex)
            {
                result.Add(counts[i]);
                continue;
            }
            var cnt = 0;
            for (var k = 0; k < kept.Count; k++)
            {
                if (keptIdx[k] != i) continue;
                if (positives.Count == 0 || positives.Contains(kept[k].Class, StringComparer.OrdinalIgnoreCase))
                {
                    cnt++;
                }
            }
            result.Add((counts[i].Name, cnt));
        }
        return (kept, keptIdx, result);
    }

    /// <summary>单通道/四通道 → 3 通道 BGR（每次运行新 Mat，调用方负责释放）。SegNode 复用。</summary>
    internal static Mat Normalize3ch(Mat img)
    {
        if (img.Channels() == 3) return img.Clone();
        var dst = new Mat();
        Cv2.CvtColor(img, dst, img.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return dst;
    }

    /// <summary>ROI 像素裁剪矩形（轴对齐外接框，钳制到图像内）——Detect 与坐标偏移共用同一矩形。SegNode 复用。</summary>
    internal static Rect ComputeRoiCropRect(int cx, int cy, double w, double h, double angle, int imgW, int imgH)
    {
        var a = angle * Math.PI / 180.0;
        var extW = (int)Math.Ceiling(Math.Abs(w * Math.Cos(a)) + Math.Abs(h * Math.Sin(a)));
        var extH = (int)Math.Ceiling(Math.Abs(w * Math.Sin(a)) + Math.Abs(h * Math.Cos(a)));
        var x = Math.Clamp(cx - extW / 2, 0, Math.Max(0, imgW - 2));
        var y = Math.Clamp(cy - extH / 2, 0, Math.Max(0, imgH - 2));
        extW = Math.Clamp(extW, 2, imgW - x);
        extH = Math.Clamp(extH, 2, imgH - y);
        return new Rect(x, y, extW, extH);
    }

    /// <summary>单张 3 通道 BGR 图推理（letterbox → ONNX → 后处理 → 源图坐标）；conf 由调用方传（检测项级阈值）。</summary>
    private List<YoloDetection> Detect(Mat bgr3, float conf)
    {
        var (scale, dx, dy) = YoloPostprocess.LetterboxFit(bgr3.Width, bgr3.Height, _inputW, _inputH);

        using var letter = new Mat(_inputH, _inputW, MatType.CV_8UC3, Scalar.All(114));
        var newW = (int)Math.Round(bgr3.Width * scale);
        var newH = (int)Math.Round(bgr3.Height * scale);
        var roi = new Rect((int)dx, (int)dy, newW, newH);
        using var resized = new Mat();
        Cv2.Resize(bgr3, resized, new Size(newW, newH));
        using var roiView = new Mat(letter, roi);
        resized.CopyTo(roiView);

        using var rgb = new Mat();
        Cv2.CvtColor(letter, rgb, ColorConversionCodes.BGR2RGB);
        using var floatMat = new Mat();
        rgb.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);

        // CHW
        Cv2.Split(floatMat, out var channels);
        var tensor = new DenseTensor<float>([1, 3, _inputH, _inputW]);
        for (var c = 0; c < 3; c++)
        {
            if (channels[c].GetArray(out float[] plane))
            {
                for (var i = 0; i < plane.Length; i++)
                {
                    tensor[0, c, i / _inputW, i % _inputW] = plane[i];
                }
            }
            channels[c].Dispose();
        }

        using var results = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var first = results.First();
        var output = first.AsTensor<float>();
        var ch = output.Dimensions[1];
        var n = output.Dimensions[2];
        var data = new float[ch * n];
        for (var c = 0; c < ch; c++)
        {
            for (var i = 0; i < n; i++)
            {
                data[c * n + i] = output[0, c, i];
            }
        }

        var iou = ParseFloat(_params.GetValueOrDefault("iou"), 0.45f);
        var boxes = YoloPostprocess.ParseAndNms(data, ch, n, conf, iou);

        return boxes.Select(b =>
        {
            var (scx, scy, sw, sh) = YoloPostprocess.MapToSource(b.Cx, b.Cy, b.W, b.H, scale, dx, dy);
            return new YoloDetection(b.ClassIndex, ClassName(b.ClassIndex), b.Conf, scx, scy, sw, sh);
        }).ToList();
    }

    private static float ParseFloat(string? raw, float fallback) =>
        float.TryParse(raw, out var v) ? v : fallback;

    /// <summary>输出底图：3 通道 BGR 克隆（框/文字改由 UI 矢量叠加层渲染，不进位图）。</summary>
    private static Mat BuildBaseImage(Mat img)
    {
        if (img.Channels() == 3) return img.Clone();
        var mat = new Mat();
        Cv2.CvtColor(img, mat, ColorConversionCodes.GRAY2BGR);
        return mat;
    }

    /// <summary>矢量标注：实例框+类别置信度（触发=红/未触发=绿）+ ROI 四边形+名称（黄），源图像素坐标。</summary>
    internal static List<NodeShape> BuildAnnotations(
        int imgW, int imgH, List<(string Name, RoiRect Rect)> rois, List<YoloDetection> all, List<YoloDetection> triggered)
    {
        var shapes = new List<NodeShape>();
        var trigSet = new HashSet<YoloDetection>(triggered);
        foreach (var d in all)
        {
            if (d.W <= 0 || d.H <= 0) continue;
            shapes.Add(new NodeShape
            {
                Box = new Rect(
                    (int)Math.Round(d.Cx - d.W / 2), (int)Math.Round(d.Cy - d.H / 2),
                    Math.Max(1, (int)Math.Round(d.W)), Math.Max(1, (int)Math.Round(d.H))),
                Label = $"{d.Class} {d.Conf:F2}",
                Kind = trigSet.Contains(d) ? NodeShapeKind.Defect : NodeShapeKind.Ok,
            });
        }

        foreach (var (name, rect) in rois)
        {
            var (cx, cy, w, h, angle) = rect.ToPixels(imgW, imgH);
            shapes.Add(new NodeShape
            {
                Polys = [PatchCoreNode.RoiQuad(cx, cy, w, h, angle)],
                Label = name,
                Kind = NodeShapeKind.Info,
            });
        }
        return shapes;
    }

    /// <summary>检测框切图：crop_dir\OK|NG\时间戳_类别_置信度.jpg + sidecar（类别/框/时间）。</summary>
    private void SaveDetections(
        Mat img, List<YoloDetection> detections, List<YoloDetection> triggered, string decision,
        List<int> detRoiIdx, List<RoiMeta> metas)
    {
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var trigSet = new HashSet<YoloDetection>(triggered);
        var sub = decision == "NG" ? "NG" : "OK";
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var byRoi = detRoiIdx.Count == detections.Count; // 全图检测无 ROI 归属

        for (var di = 0; di < detections.Count; di++)
        {
            var d = detections[di];
            if (byRoi)
            {
                var idx = detRoiIdx[di];
                if (idx >= 0 && idx < metas.Count && !metas[idx].SaveImage) continue; // 检测项级不存图
            }
            try
            {
                var x = Math.Clamp((int)Math.Round(d.Cx - d.W / 2), 0, img.Width - 2);
                var y = Math.Clamp((int)Math.Round(d.Cy - d.H / 2), 0, img.Height - 2);
                var w = Math.Clamp((int)Math.Round(d.W), 2, img.Width - x);
                var h = Math.Clamp((int)Math.Round(d.H), 2, img.Height - y);
                using var crop = new Mat(img, new Rect(x, y, w, h)).Clone();

                var full = Path.GetFullPath(Path.Combine(dir, sub));
                Directory.CreateDirectory(full);
                var outPath = Path.Combine(full, $"{ts}_{d.Class}_{d.Conf:F2}.jpg");
                Cv2.ImWrite(outPath, crop);
                File.WriteAllText(outPath + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    d.Class,
                    conf = d.Conf,
                    box = new[] { x, y, w, h },
                    triggered = trigSet.Contains(d),
                    created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                }));
                Log?.Invoke($"[切图] {Name}: 已保存 {sub} 检测框切图 {d.Class}({d.Conf:F2}) -> {outPath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[切图] {Name}: 保存失败: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
