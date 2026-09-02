using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SpeakerVisionInspection.Models;
using SpeakerVisionInspection.Services;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 实例分割节点（Ultralytics yolo11-seg 标准导出 ONNX）。
/// 模型目录契约：best.onnx + classes.txt（u训练 yolo11*-seg 训练产物；
/// output0 = [1, 4+nc+32, N] 检测+掩码系数，output1 = [1, 32, PH, PW] proto）。
/// 判定：关注类别实例掩码像素占比（整图或 ROI 裁剪区内）≥ percent 阈值 → NG（缺陷检测）。
/// 切图：crop_dir 非空时，缺陷连通域裁剪保存到 crop_dir\OK|NG\时间戳_类别_序号.jpg。
/// </summary>
public sealed class SegNode : IModelNode
{
    public const string DecisionRatioNg = "缺陷占比≥阈值即NG";
    public const string DecisionDetectNg = "检出即NG";
    public const string DecisionMissingNg = "缺失即NG";

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "model_dir", Label = "模型目录(best.onnx+classes.txt)", Kind = "folder" },
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "conf", Label = "置信度阈值", Kind = "double", Default = "0.25" },
        new ParamDef { Key = "iou", Label = "NMS IoU阈值", Kind = "double", Default = "0.45" },
        new ParamDef { Key = "positive_classes", Label = "关注类别(逗号分隔,空=全部)", Kind = "string", Default = "" },
        new ParamDef { Key = "decision_mode", Label = "判定模式", Kind = "choice", Default = DecisionRatioNg, Choices = [DecisionRatioNg, DecisionDetectNg, DecisionMissingNg] },
        new ParamDef { Key = "crop_dir", Label = "缺陷区切图保存目录(可选·按OK/NG分目录)", Kind = "folder", Default = "" },
        new ParamDef { Key = "scope_index", Label = "总范围ROI索引(检测项管理设置)", Kind = "hidden", Default = "-1" },
    ];

    /// <summary>检测项默认缺陷占比阈值(%)（节点级总阈值已移除，检测项阈值缺省时回退此值）。</summary>
    public const string DefaultPercent = "0.5";

    /// <summary>单实例结果（全图像素坐标框，供标注/切图；RoiIndex=所属检测项索引，SaveImage=检测项级存图开关）。</summary>
    internal sealed record SegInstance(SegDetection Det, string Class, Rect Box, bool Triggered, string RoiName, int RoiIndex, bool SaveImage);

    private InferenceSession? _session;
    private string _inputName = "images";
    private List<string> _classNames = new();
    private int _inputW = 640;
    private int _inputH = 640;
    private readonly Dictionary<string, string> _params = new()
    {
        ["model_dir"] = "",
        ["source"] = "@input",
        ["conf"] = "0.25",
        ["iou"] = "0.45",
        ["positive_classes"] = "",
        ["decision_mode"] = DecisionRatioNg,
        ["crop_dir"] = "",
        ["scope_index"] = "-1",
    };

    /// <summary>失配/切图日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public SegNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "Seg";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    /// <summary>运行时模型目录（已解析绝对路径）。</summary>
    public string ResolvedModelDir { get; private set; } = "";

    public void SetParam(string key, string value)
    {
        var oldModelDir = _params.GetValueOrDefault("model_dir");
        _params[key] = value;
        // 只有模型目录变化才需要重载会话；conf/iou/percent/类别/ROI/切图目录热生效
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

        _session = new InferenceSession(Path.Combine(ResolvedModelDir, "best.onnx"), CreateSessionOptions());
        _inputName = _session.InputMetadata.First().Key;

        // 输入尺寸从模型元数据读取（默认 640）
        var input = _session.InputMetadata.First();
        var dims = input.Value.Dimensions;
        if (dims.Length >= 4 && dims[^1] > 0 && dims[^2] > 0)
        {
            _inputW = dims[^1];
            _inputH = dims[^2];
        }
    }

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

        using var normalized = YoloNode.Normalize3ch(img);
        using var defectMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));

        var instances = new List<SegInstance>();
        var regionRatios = new List<(string Name, double Percent)>();
        long defectPixels = 0;
        var roiActive = new List<bool>(); // 与 rois 平行：停用项 false（scope 重算/判定跳过）

        // 总范围 ROI（scope_index 指向；其余 ROI 的结果只保留落在总范围内的部分）
        var scopeIndex = NodeRois.ParseScopeIndex(_params);
        var scopeActive = scopeIndex >= 0 && scopeIndex < rois.Count;
        using var scopeMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));
        if (scopeActive)
        {
            var (scx, scy, sw, sh, sang) = rois[scopeIndex].Rect.ToPixels(img.Width, img.Height);
            var radS = sang * Math.PI / 180.0;
            var cS = Math.Cos(radS);
            var sS = Math.Sin(radS);
            var hw = sw / 2.0;
            var hh = sh / 2.0;
            Point[] poly =
            {
                new((int)Math.Round(scx + (-hw) * cS - (-hh) * sS), (int)Math.Round(scy + (-hw) * sS + (-hh) * cS)),
                new((int)Math.Round(scx + hw * cS - (-hh) * sS), (int)Math.Round(scy + hw * sS + (-hh) * cS)),
                new((int)Math.Round(scx + hw * cS - hh * sS), (int)Math.Round(scy + hw * sS + hh * cS)),
                new((int)Math.Round(scx + (-hw) * cS - hh * sS), (int)Math.Round(scy + (-hw) * sS + hh * cS)),
            };
            Cv2.FillPoly(scopeMask, new[] { poly }, Scalar.All(255));
        }

        var cropRects = new List<Rect>();
        if (rois.Count == 0)
        {
            roiActive.Add(true);
            defectPixels += SegmentRegion(normalized, new Rect(0, 0, img.Width, img.Height), positives, "", defectMask, instances, regionRatios, new RoiMeta(), 0);
            cropRects.Add(new Rect(0, 0, img.Width, img.Height));
        }
        else
        {
            for (var ri = 0; ri < rois.Count; ri++)
            {
                var (name, rect) = rois[ri];
                var meta = ri < metas.Count ? metas[ri] : new RoiMeta();
                var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
                var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, img.Width, img.Height);
                cropRects.Add(cropRect);
                if (!meta.Enabled)
                {
                    roiActive.Add(false);
                    regionRatios.Add((name, -1)); // 停用：不检测
                    continue;
                }
                roiActive.Add(true);
                defectPixels += SegmentRegion(normalized, cropRect, positives, name, defectMask, instances, regionRatios, meta, ri);
            }
        }

        // 总范围过滤：缺陷掩码 AND 总范围；实例按中心点保留；范围外 ROI 占比按过滤后掩码重算
        if (scopeActive)
        {
            var scopeName = rois[scopeIndex].Name;
            Cv2.BitwiseAnd(defectMask, scopeMask, defectMask);
            var before = instances.Count;
            instances.RemoveAll(i => !NodeRois.ContainsPixel(
                rois[scopeIndex].Rect, i.Box.X + i.Box.Width / 2.0, i.Box.Y + i.Box.Height / 2.0, img.Width, img.Height));
            defectPixels = Cv2.CountNonZero(defectMask);
            for (var i = 0; i < regionRatios.Count && i < cropRects.Count; i++)
            {
                if (i == scopeIndex || !roiActive[i]) continue; // 范围 ROI 与停用项不重算
                using var regionView = new Mat(defectMask, cropRects[i]);
                var area = (long)cropRects[i].Width * cropRects[i].Height;
                regionRatios[i] = (regionRatios[i].Name, area > 0 ? Cv2.CountNonZero(regionView) * 100.0 / area : 0.0);
            }
            Log?.Invoke($"[总范围] {Name}: 按总范围 ROI「{scopeName}」过滤，移除范围外实例 {before - instances.Count} 个");
        }

        var maxPercent = regionRatios.Count > 0 ? regionRatios.Where(r => r.Percent >= 0).Select(r => r.Percent).DefaultIfEmpty(0.0).Max() : 0.0;
        // 检测项级判定：占比模式 = 任一启用且参与判定的检测项 缺陷占比 ≥ 该项阈值 → NG；观察/停用项不参与
        var roiChecks = new List<(double Ratio, double Threshold)>();
        for (var i = 0; i < rois.Count && i < metas.Count; i++)
        {
            if (!NodeRois.Judges(metas[i])) continue;
            roiChecks.Add((regionRatios[i].Percent, metas[i].ThresholdValue ?? double.Parse(DefaultPercent)));
        }
        var triggeredCount = instances.Count(i => i.Triggered && i.RoiIndex >= 0 && i.RoiIndex < metas.Count && NodeRois.Judges(metas[i.RoiIndex]));
        var decision = ComputeDecision(_params.GetValueOrDefault("decision_mode"), roiChecks, triggeredCount);

        // 输出图：缺陷掩码半透明染色（像素内容保留位图）；框/轮廓/文字由 UI 矢量叠加层渲染
        var output = BuildTintedImage(img, defectMask);

        if (!string.IsNullOrWhiteSpace(_params.GetValueOrDefault("crop_dir")))
        {
            SaveDefectCrops(img, defectMask, instances, decision);
        }

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = output,
        };
        foreach (var shape in BuildAnnotations(img.Width, img.Height, defectMask, instances, rois))
        {
            nodeResult.Annotations.Add(shape);
        }
        nodeResult.Values["max_ratio"] = maxPercent.ToString("F3");
        nodeResult.Values["defect_pixels"] = defectPixels.ToString();
        nodeResult.Values["count"] = instances.Count(i => i.Triggered).ToString();
        nodeResult.Values["instances"] = instances.Count.ToString();
        nodeResult.Values["classes"] = string.Join(",", instances.Where(i => i.Triggered).Select(i => i.Class).Distinct());
        foreach (var (name, percent) in regionRatios)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                nodeResult.Values[$"roi_{name}"] = percent < 0 ? "停用" : percent.ToString("F3");
            }
        }

        return nodeResult;
    }

    /// <summary>
    /// 判定（纯逻辑，可单测）——阈值全部为检测项级（节点不再有总阈值）：
    /// 缺陷占比≥阈值即NG（默认，缺陷检测）：任一启用且参与判定的检测项 缺陷占比 ≥ 该项阈值 → NG；
    /// 检出即NG：关注类别任一实例检出 → NG；
    /// 缺失即NG（缺料/漏装检测）：关注类别一个都没检出 → NG。
    /// </summary>
    internal static string ComputeDecision(string? mode, List<(double Ratio, double Threshold)> roiChecks, int triggeredCount)
    {
        if (string.Equals((mode ?? "").Trim(), DecisionMissingNg, StringComparison.Ordinal))
        {
            return triggeredCount > 0 ? "OK" : "NG";
        }
        if (string.Equals((mode ?? "").Trim(), DecisionDetectNg, StringComparison.Ordinal))
        {
            return triggeredCount > 0 ? "NG" : "OK";
        }
        return roiChecks.Any(r => r.Ratio >= r.Threshold) ? "NG" : "OK";
    }

    private HashSet<string> ParsePositiveClasses() =>
        (_params.GetValueOrDefault("positive_classes") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解析本节点的检测区域：节点私有 own_rois（含检测项元数据）+ 位置修正（与 YoloNode 同语义）。
    /// 返回的 rois 与 metas 按索引一一对应。
    /// </summary>
    private (List<(string Name, RoiRect Rect)> Rois, List<RoiMeta> Metas) ResolveRoisFull(Mat img, PipelineRunContext ctx)
    {
        var items = NodeRois.ParseOwnFull(_params.GetValueOrDefault("own_rois"));
        var rois = NodeRois.ApplyPoseCorrection(
            items.Select(i => (i.Name, i.Rect)).ToList(), Name, img.Width, img.Height, ctx.PoseCorrection);
        return (rois, items.Select(i => i.Meta).ToList());
    }

    /// <summary>
    /// 对一个区域（全图或 ROI 裁剪矩形）跑实例分割：
    /// letterbox → ONNX（output0 检测+系数 / output1 proto）→ 解析+NMS → 逐实例掩码（系数·proto → logits&gt;0 →
    /// 上采样 → 内容区 → 源图尺寸 → AND 检测框）→ 触发实例掩码并集为区域缺陷掩码。
    /// 返回区域缺陷像素数；缺陷掩码 OR 进 defectMaskFull，实例（全图坐标）追加进 instances，占比(%) 记入 regionRatios。
    /// </summary>
    private long SegmentRegion(
        Mat img, Rect region, HashSet<string> positives, string roiName,
        Mat defectMaskFull, List<SegInstance> instances, List<(string Name, double Percent)> regionRatios,
        RoiMeta meta, int roiIndex)
    {
        using var cropView = new Mat(img, region);
        var (scale, dx, dy) = YoloPostprocess.LetterboxFit(cropView.Width, cropView.Height, _inputW, _inputH);

        // letterbox 预处理（与 YoloNode.Detect 相同）
        using var letter = new Mat(_inputH, _inputW, MatType.CV_8UC3, Scalar.All(114));
        var contentW = (int)Math.Round(cropView.Width * scale);
        var contentH = (int)Math.Round(cropView.Height * scale);
        using var resized = new Mat();
        Cv2.Resize(cropView, resized, new Size(contentW, contentH));
        using var roiView = new Mat(letter, new Rect((int)dx, (int)dy, contentW, contentH));
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

        // 按张量秩识别输出：3 维 = 检测+系数，4 维 = proto
        using var results = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        float[]? detData = null;
        var detCh = 0;
        var detN = 0;
        float[]? protoData = null;
        var protoK = 0;
        var protoH = 0;
        var protoW = 0;
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            var dims = t.Dimensions;
            if (dims.Length == 3)
            {
                detCh = dims[1];
                detN = dims[2];
                detData = new float[(long)detCh * detN];
                for (var c = 0; c < detCh; c++)
                {
                    for (var i = 0; i < detN; i++)
                    {
                        detData[(long)c * detN + i] = t[0, c, i];
                    }
                }
            }
            else if (dims.Length == 4 && dims[0] == 1)
            {
                protoK = dims[1];
                protoH = dims[2];
                protoW = dims[3];
                protoData = new float[(long)protoK * protoH * protoW];
                for (var k = 0; k < protoK; k++)
                {
                    for (var y = 0; y < protoH; y++)
                    {
                        for (var x = 0; x < protoW; x++)
                        {
                            protoData[((long)k * protoH + y) * protoW + x] = t[0, k, y, x];
                        }
                    }
                }
            }
        }
        if (detData == null || protoData == null)
        {
            throw new InvalidOperationException(
                $"节点 {Name}: 模型输出不符合 yolo11-seg 标准导出（需 [1,X,N] 检测输出 + [1,K,PH,PW] proto 输出）");
        }
        var nc = detCh - 4 - protoK;
        if (nc <= 0)
        {
            throw new InvalidOperationException(
                $"节点 {Name}: 模型输出通道数异常（{detCh} = 4 + {nc} + {protoK}），请确认是 yolo11*-seg 分割模型导出");
        }

        var conf = ParseFloat(_params.GetValueOrDefault("conf"), 0.25f);
        var iou = ParseFloat(_params.GetValueOrDefault("iou"), 0.45f);
        var dets = YoloSegPostprocess.ParseSegAndNms(detData, detCh, detN, nc, protoK, conf, iou);

        // 区域局部缺陷掩码：触发实例掩码（AND 检测框）取并集
        using var defectCrop = new Mat(region.Height, region.Width, MatType.CV_8UC1, Scalar.All(0));
        foreach (var d in dets)
        {
            var cls = ClassName(d.ClassIndex);
            var triggered = positives.Count == 0 || positives.Contains(cls);
            var (mcx, mcy, mw, mh) = YoloPostprocess.MapToSource(d.Cx, d.Cy, d.W, d.H, scale, dx, dy);
            var box = ClampRect(
                (int)Math.Round(mcx - mw / 2), (int)Math.Round(mcy - mh / 2),
                (int)Math.Round(mw), (int)Math.Round(mh), region.Width, region.Height);
            if (triggered)
            {
                using var m = BuildInstanceMask(d.Coeffs, protoData, protoH, protoW, (int)dx, (int)dy, contentW, contentH, region.Size, box);
                if (m != null)
                {
                    Cv2.BitwiseOr(defectCrop, m, defectCrop);
                    m.Dispose();
                }
            }
            instances.Add(new SegInstance(
                d, cls,
                new Rect(box.X + region.X, box.Y + region.Y, box.Width, box.Height),
                triggered, roiName, roiIndex, meta.SaveImage));
        }

        // 并入全图缺陷掩码（就地 OR）
        using (var cropRoi = new Mat(defectMaskFull, region))
        {
            Cv2.BitwiseOr(cropRoi, defectCrop, cropRoi);
        }

        var pixels = Cv2.CountNonZero(defectCrop);
        var area = (long)region.Width * region.Height;
        var percent = area > 0 ? pixels * 100.0 / area : 0.0;
        regionRatios.Add((roiName, percent));
        return pixels;
    }

    /// <summary>
    /// 单实例掩码（区域局部坐标，CV_8UC1）：
    /// 系数·proto logits → 阈值 0 二值化 → 上采样到模型输入 → 裁 letterbox 内容区 → 缩放到区域尺寸 → AND 检测框。
    /// </summary>
    private Mat? BuildInstanceMask(float[] coeffs, float[] protoData, int protoH, int protoW,
        int dx, int dy, int contentW, int contentH, Size cropSize, Rect box)
    {
        if (box.Width <= 0 || box.Height <= 0) return null;
        var logits = YoloSegPostprocess.ComposeMaskLogits(coeffs, protoData, protoH, protoW);
        using var logitsMat = new Mat(protoH, protoW, MatType.CV_32FC1);
        if (!logitsMat.GetArray(out float[] buf)) return null;
        Array.Copy(logits, buf, Math.Min(logits.Length, buf.Length));

        using var bin = new Mat();
        Cv2.Threshold(logitsMat, bin, 0, 255, ThresholdTypes.Binary); // logits>0 ⇔ sigmoid>0.5（输出保持 32F，需转 8U）
        using var bin8 = new Mat();
        bin.ConvertTo(bin8, MatType.CV_8UC1);
        using var up = new Mat();
        Cv2.Resize(bin8, up, new Size(_inputW, _inputH), 0, 0, InterpolationFlags.Linear);
        var cx0 = Math.Clamp(dx, 0, Math.Max(0, _inputW - 1));
        var cy0 = Math.Clamp(dy, 0, Math.Max(0, _inputH - 1));
        var cw = Math.Clamp(Math.Min(contentW, _inputW - cx0), 1, Math.Max(1, _inputW - cx0));
        var ch = Math.Clamp(Math.Min(contentH, _inputH - cy0), 1, Math.Max(1, _inputH - cy0));
        using var content = new Mat(up, new Rect(cx0, cy0, cw, ch));

        var mask = new Mat();
        Cv2.Resize(content, mask, cropSize, 0, 0, InterpolationFlags.Nearest);
        using var boxMask = new Mat(cropSize.Height, cropSize.Width, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(boxMask, box, Scalar.All(255), -1);
        Cv2.BitwiseAnd(mask, boxMask, mask);
        return mask;
    }

    private static float ParseFloat(string? raw, float fallback) =>
        float.TryParse(raw, out var v) ? v : fallback;

    private static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, out var v) ? v : fallback;

    /// <summary>矩形钳制到图像内（永不抛异常；最小 1×1）。</summary>
    private static Rect ClampRect(int x, int y, int w, int h, int maxW, int maxH)
    {
        x = Math.Clamp(x, 0, Math.Max(0, maxW - 1));
        y = Math.Clamp(y, 0, Math.Max(0, maxH - 1));
        w = Math.Clamp(Math.Min(w, maxW - x), 1, Math.Max(1, maxW - x));
        h = Math.Clamp(Math.Min(h, maxH - y), 1, Math.Max(1, maxH - y));
        return new Rect(x, y, w, h);
    }

    private static double OverlapArea(Rect a, Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
    }

    /// <summary>输出底图：源图 + 缺陷掩码半透明红色染色（像素级内容，保留位图；掩码外不变）。</summary>
    private static Mat BuildTintedImage(Mat img, Mat defectMask)
    {
        Mat mat;
        if (img.Channels() == 3)
        {
            mat = img.Clone();
        }
        else
        {
            mat = new Mat();
            Cv2.CvtColor(img, mat, ColorConversionCodes.GRAY2BGR);
        }

        using var tinted = mat.Clone();
        tinted.SetTo(new Scalar(0, 64, 255), defectMask);
        using var blended = new Mat();
        Cv2.AddWeighted(mat, 0.6, tinted, 0.4, 0, blended);
        blended.CopyTo(mat);
        return mat;
    }

    /// <summary>矢量标注：缺陷轮廓+实例框+类别置信度（触发=红/未触发=绿）+ ROI 四边形+名称（黄），源图像素坐标。</summary>
    internal static List<NodeShape> BuildAnnotations(
        int imgW, int imgH, Mat defectMask, List<SegInstance> instances, List<(string Name, RoiRect Rect)> rois)
    {
        var shapes = new List<NodeShape>();

        // 缺陷连通域轮廓（红，无文字）
        Cv2.FindContours(defectMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in contours)
        {
            if (Cv2.ContourArea(contour) < 1) continue;
            shapes.Add(new NodeShape { Polys = [contour], Kind = NodeShapeKind.Defect });
        }

        // 实例框 + 类别/置信度（触发=红 / 未触发=绿）
        foreach (var inst in instances)
        {
            if (inst.Box.Width <= 0 || inst.Box.Height <= 0) continue;
            shapes.Add(new NodeShape
            {
                Box = inst.Box,
                Label = $"{inst.Class} {inst.Det.Conf:F2}",
                Kind = inst.Triggered ? NodeShapeKind.Defect : NodeShapeKind.Ok,
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

    /// <summary>缺陷区切图：crop_dir\OK|NG\时间戳_类别_序号.jpg + sidecar（类别/置信度/框/ROI 名/时间）。</summary>
    private void SaveDefectCrops(Mat img, Mat defectMask, List<SegInstance> instances, string decision)
    {
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var sub = decision == "NG" ? "NG" : "OK";
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var triggered = instances.Where(i => i.Triggered && i.SaveImage).ToList(); // 检测项级不存图跳过

        Cv2.FindContours(defectMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var idx = 0;
        foreach (var contour in contours)
        {
            if (Cv2.ContourArea(contour) < 1) continue;
            try
            {
                var raw = Cv2.BoundingRect(contour);
                var box = ClampRect(raw.X, raw.Y, raw.Width, raw.Height, img.Width, img.Height);
                using var crop = new Mat(img, box).Clone();

                // 归属类别：与缺陷框重叠面积最大的触发实例
                var best = triggered
                    .Select(i => (Inst: i, Over: OverlapArea(i.Box, box)))
                    .Where(t => t.Over > 0)
                    .OrderByDescending(t => t.Over)
                    .ToList();
                var cls = best.Count > 0 ? best[0].Inst.Class : "缺陷";
                var conf = best.Count > 0 ? best[0].Inst.Det.Conf : 0f;
                var roiName = best.Count > 0 ? best[0].Inst.RoiName : "";

                var full = Path.GetFullPath(Path.Combine(dir, sub));
                Directory.CreateDirectory(full);
                var outPath = Path.Combine(full, $"{ts}_{cls}_{idx++}.jpg");
                Cv2.ImWrite(outPath, crop);
                File.WriteAllText(outPath + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    @class = cls,
                    conf,
                    box = new[] { box.X, box.Y, box.Width, box.Height },
                    roi_name = roiName,
                    triggered = true,
                    created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                }));
                Log?.Invoke($"[切图] {Name}: 已保存 {sub} 缺陷区切图 {cls}({conf:F2}) -> {outPath}");
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
