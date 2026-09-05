using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SpeakerVisionInspection.Models;
using SpeakerVisionInspection.Services;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 语义分割节点（u训练 task=semantic，yolo26*-sem 导出 ONNX）。
/// 模型目录契约：best.onnx + classes.txt（与 YOLO/Seg 同款 u训练产物契约）；
/// 输出 = [1,H,W] 像素级类别索引图（uint8，值 0..nc-1，与 classes.txt 行序对应；mask 约定 0 是类别、255 为忽略区）。
/// 判定：检测项 ROI 内「关注类别」像素占比 ≥ 该项阈值 → NG（判定模式与实例分割一致：占比/检出即NG/缺失即NG）。
/// 与实例分割的区别：无实例框/置信度/NMS，按像素类别占比判定；classes.txt 中名为「背景/background」的类别永远不算缺陷。
/// </summary>
public sealed class SemanticSegNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "model_dir", Label = "模型目录(best.onnx+classes.txt)", Kind = "folder" },
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "decision_mode", Label = "判定模式", Kind = "choice", Default = SegNode.DecisionRatioNg, Choices = [SegNode.DecisionRatioNg, SegNode.DecisionDetectNg, SegNode.DecisionMissingNg] },
        new ParamDef { Key = "crop_dir", Label = "缺陷区切图保存目录(可选·按OK/NG分目录)", Kind = "folder", Default = "" },
        new ParamDef { Key = "save_mode", Label = "整图保存", Kind = "choice", Default = "全部", Choices = ["全部", "仅OK", "仅NG"] },
        new ParamDef { Key = "scope_index", Label = "总范围ROI索引(检测项管理设置)", Kind = "hidden", Default = "-1" },
    ];

    /// <summary>检测项默认缺陷占比阈值(%)（与实例分割同款，检测项阈值缺省时回退此值）。</summary>
    public const string DefaultPercent = SegNode.DefaultPercent;

    private InferenceSession? _session;
    private string _inputName = "images";
    private string _outputName = "output0";
    private List<string> _classNames = new();
    private int _inputW = 320;
    private int _inputH = 320;
    private readonly Dictionary<string, string> _params = new()
    {
        ["model_dir"] = "",
        ["source"] = "@input",
        ["decision_mode"] = SegNode.DecisionRatioNg,
        ["crop_dir"] = "",
        ["save_mode"] = "全部",
        ["scope_index"] = "-1",
    };

    /// <summary>失配/切图日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    private bool _positiveWarned;

    private void WarnOnce(string msg)
    {
        if (_positiveWarned) return;
        _positiveWarned = true;
        Log?.Invoke(msg);
    }

    public SemanticSegNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "SemanticSeg";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    /// <summary>运行时模型目录（已解析绝对路径）。</summary>
    public string ResolvedModelDir { get; private set; } = "";

    public void SetParam(string key, string value)
    {
        var oldModelDir = _params.GetValueOrDefault("model_dir");
        _params[key] = value;
        // 只有模型目录变化才需要重载会话；关注类别/判定模式/ROI/切图目录热生效
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

        // 类别表：UTF-8 每行一个类别（行序 = 像素类别索引）；行数不足时回退「类别{i}」
        _classNames = File.ReadAllLines(Path.Combine(ResolvedModelDir, "classes.txt"), Encoding.UTF8)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        _session = new InferenceSession(Path.Combine(ResolvedModelDir, "best.onnx"), CreateSessionOptions());
        _inputName = _session.InputMetadata.First().Key;
        var output = _session.OutputMetadata.FirstOrDefault();
        _outputName = output.Key;

        // 输入尺寸从模型元数据读取（默认 320）
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

    /// <summary>背景类判定：名为 背景/background（不分大小写）的类别不算缺陷（语义数据集可能显式标注背景）。</summary>
    internal static bool IsBackgroundClass(string name) =>
        string.Equals(name, "背景", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "background", StringComparison.OrdinalIgnoreCase);

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

        // ROI 解析：节点私有 own_rois（含检测项元数据）+ 位置修正；空 = 全图检测。
        // 检测项名 ↔ 模型类名匹配：名字命中的检测项只判该类；未命中/无检测项按全部非背景类别判定（不再使用「关注类别」参数）
        var (rois, metas) = ResolveRoisFull(img, ctx);
        var roiClassMap = new List<IReadOnlyList<string>>(rois.Count);
        foreach (var (name, _) in rois)
        {
            var matched = PositiveClasses.MatchClassesForRoi(name, _classNames);
            if (matched.Count == 0 && !string.IsNullOrWhiteSpace(name))
            {
                WarnOnce($"[检测项] {Name}: 检测项「{name}」名称未匹配任何模型类别({string.Join("、", _classNames)})，按全部非背景类别判定");
            }
            roiClassMap.Add(matched);
        }

        using var normalized = YoloNode.Normalize3ch(img);
        using var defectMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));
        using var classMap = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0)); // 0=无缺陷，1+=类别索引+1

        var regionRatios = new List<(string Name, double Percent)>();
        var roiActive = new List<bool>(); // 与 rois 平行：停用项 false（scope 重算/判定跳过）
        var roiHasDefect = new List<bool>(); // 与 rois 平行：该启用项区域是否有缺陷像素（检出/缺失模式用）
        long defectPixels = 0;

        // 总范围 ROI（scope_index 指向；其余 ROI 的结果只保留落在总范围内的部分）
        var scopeIndex = NodeRois.ParseScopeIndex(_params);
        var scopeActive = scopeIndex >= 0 && scopeIndex < rois.Count;
        using var scopeMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));
        if (scopeActive)
        {
            FillRotatedRect(scopeMask, rois[scopeIndex].Rect, img.Width, img.Height);
        }

        var cropRects = new List<Rect>();
        if (rois.Count == 0)
        {
            roiActive.Add(true);
            roiHasDefect.Add(false);
            regionRatios.Add(("", 0));
            cropRects.Add(new Rect(0, 0, img.Width, img.Height));
            defectPixels += SegmentRegion(normalized, cropRects[0], Array.Empty<string>(), defectMask, classMap);
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
                    roiHasDefect.Add(false);
                    regionRatios.Add((name, -1)); // 停用：不检测
                    continue;
                }
                roiActive.Add(true);
                var regionDefect = SegmentRegion(normalized, cropRect, roiClassMap[ri], defectMask, classMap);
                roiHasDefect.Add(regionDefect > 0);
                defectPixels += regionDefect;
                var area = (long)cropRect.Width * cropRect.Height;
                regionRatios.Add((name, area > 0 ? regionDefect * 100.0 / area : 0.0));
            }
        }

        // 总范围过滤：缺陷掩码与类别图 AND 总范围；范围外 ROI 占比按过滤后掩码重算
        if (scopeActive)
        {
            var scopeName = rois[scopeIndex].Name;
            Cv2.BitwiseAnd(defectMask, scopeMask, defectMask);
            Cv2.BitwiseAnd(classMap, scopeMask, classMap);
            defectPixels = Cv2.CountNonZero(defectMask);
            for (var i = 0; i < regionRatios.Count && i < cropRects.Count; i++)
            {
                if (i == scopeIndex || !roiActive[i]) continue; // 范围 ROI 与停用项不重算
                using var regionView = new Mat(defectMask, cropRects[i]);
                using var regionClass = new Mat(classMap, cropRects[i]);
                var pixels = Cv2.CountNonZero(regionView);
                var area = (long)cropRects[i].Width * cropRects[i].Height;
                regionRatios[i] = (regionRatios[i].Name, area > 0 ? pixels * 100.0 / area : 0.0);
                roiHasDefect[i] = pixels > 0;
            }
            Log?.Invoke($"[总范围] {Name}: 按总范围 ROI「{scopeName}」过滤，缺陷像素重算为 {defectPixels}");
        }

        var maxPercent = regionRatios.Count > 0 ? regionRatios.Where(r => r.Percent >= 0).Select(r => r.Percent).DefaultIfEmpty(0.0).Max() : 0.0;
        // 检测项级判定（与实例分割同逻辑）：触发数 = 有缺陷像素的「启用且参与判定」检测项数
        var roiChecks = new List<(double Ratio, double Threshold)>();
        var triggeredCount = 0;
        for (var i = 0; i < rois.Count && i < metas.Count; i++)
        {
            if (!NodeRois.Judges(metas[i])) continue;
            roiChecks.Add((regionRatios[i].Percent, metas[i].ThresholdValue ?? double.Parse(DefaultPercent)));
            if (roiHasDefect[i]) triggeredCount++;
        }
        var decision = SegNode.ComputeDecision(_params.GetValueOrDefault("decision_mode"), roiChecks, triggeredCount);

        // 输出图：缺陷掩码半透明红色染色（像素内容保留位图）；轮廓/ROI 框由 UI 矢量叠加层渲染
        var output = BuildTintedImage(img, defectMask);

        // 切图：缺陷区连通域裁剪保存（检测项级「是否存图」过滤）；目录未配置时记日志
        SaveDefectCrops(img, defectMask, classMap, rois, metas, decision);

        // 整图留存：按本节点判定把原图存到 crop_dir\OK|NG（save_mode = all/ok/ng）
        SaveWholeImage(img, decision);

        var nodeResult = new NodeResult
        {
            Decision = decision,
            OutputImage = output,
        };
        foreach (var shape in BuildAnnotations(img.Width, img.Height, defectMask, classMap, rois))
        {
            nodeResult.Annotations.Add(shape);
        }
        nodeResult.Values["max_ratio"] = maxPercent.ToString("F3");
        nodeResult.Values["defect_pixels"] = defectPixels.ToString();
        nodeResult.Values["count"] = triggeredCount.ToString();
        nodeResult.Values["classes"] = string.Join(",", DistinctDefectClasses(classMap));
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
    /// 对一个区域（全图或 ROI 裁剪矩形）跑语义分割：
    /// letterbox → ONNX（[1,H,W] 像素类别索引图）→ 裁 letterbox 内容区 → NEAREST 缩放回区域尺寸 →
    /// 关注类别像素（背景类/未关注类除外）形成区域缺陷掩码。
    /// 返回区域缺陷像素数；缺陷掩码 OR 进 defectMaskFull，类别图（类别索引+1）写进 classMapFull。
    /// </summary>
    private long SegmentRegion(Mat img, Rect region, IReadOnlyList<string> roiClasses, Mat defectMaskFull, Mat classMapFull)
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

        using var results = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var classMapData = ParseClassMap(results);
        RemapSingleClass(classMapData, _classNames.Count);

        // 裁 letterbox 内容区 → NEAREST 缩放回区域尺寸（逐像素最近邻，保持类别索引离散）
        var cx0 = Math.Clamp((int)dx, 0, Math.Max(0, _inputW - 1));
        var cy0 = Math.Clamp((int)dy, 0, Math.Max(0, _inputH - 1));
        var cw = Math.Clamp(Math.Min(contentW, _inputW - cx0), 1, Math.Max(1, _inputW - cx0));
        var ch = Math.Clamp(Math.Min(contentH, _inputH - cy0), 1, Math.Max(1, _inputH - cy0));
        using var contentMat = new Mat(_inputH, _inputW, MatType.CV_8UC1);
        if (!contentMat.GetArray(out byte[] contentBuf))
        {
            throw new InvalidOperationException($"节点 {Name}: 类别图缓冲区不可写（内存不足）");
        }
        for (var y = 0; y < _inputH; y++)
        {
            for (var x = 0; x < _inputW; x++)
            {
                contentBuf[y * _inputW + x] = classMapData[y * _inputW + x];
            }
        }
        // OpenCvSharp 的 GetArray 返回托管拷贝：写完必须 SetArray 才会落回 Mat（否则全是无效写入）
        contentMat.SetArray(contentBuf);
        using var content = new Mat(contentMat, new Rect(cx0, cy0, cw, ch));
        using var regionMap = new Mat();
        Cv2.Resize(content, regionMap, region.Size, 0, 0, InterpolationFlags.Nearest);
        if (!regionMap.GetArray(out byte[] map))
        {
            throw new InvalidOperationException($"节点 {Name}: 类别图缓冲区不可写（内存不足）");
        }

        // 区域局部缺陷掩码 + 类别图写入
        using var defectCrop = new Mat(region.Height, region.Width, MatType.CV_8UC1, Scalar.All(0));
        if (!defectCrop.GetArray(out byte[] defectBuf))
        {
            throw new InvalidOperationException($"节点 {Name}: 缺陷掩码缓冲区不可写（内存不足）");
        }
        for (var i = 0; i < map.Length; i++)
        {
            var clsIdx = map[i];
            if (clsIdx >= _classNames.Count && _classNames.Count > 0 && clsIdx != 0) continue; // 越界类别（忽略区 255 等）不判定
            var cls = ClassName(clsIdx);
            if (IsBackgroundClass(cls)) continue;
            if (!PositiveClasses.MatchesRoi(roiClasses, cls)) continue;
            defectBuf[i] = 255;
        }
        // GetArray 是托管拷贝：写完必须 SetArray 落回 Mat
        defectCrop.SetArray(defectBuf);

        // 并入全图缺陷掩码（就地 OR）
        using (var cropRoi = new Mat(defectMaskFull, region))
        {
            Cv2.BitwiseOr(cropRoi, defectCrop, cropRoi);
        }

        // 并入全图类别图（就地 OR；缺陷像素写 类别索引+1，供轮廓归属/图例用）
        using (var shifted = new Mat(region.Height, region.Width, MatType.CV_8UC1, Scalar.All(0)))
        {
            if (shifted.GetArray(out byte[] sh))
            {
                for (var i = 0; i < map.Length; i++)
                {
                    if (defectBuf[i] != 0)
                    {
                        sh[i] = (byte)Math.Clamp(map[i] + 1, 1, 255);
                    }
                }
                shifted.SetArray(sh); // GetArray 是托管拷贝：写完必须 SetArray 落回 Mat
            }
            using var mapRoi = new Mat(classMapFull, region);
            Cv2.BitwiseOr(mapRoi, shifted, mapRoi);
        }

        return Cv2.CountNonZero(defectCrop);
    }

    /// <summary>
    /// 解析模型输出为 [inputH×inputW] 的 byte 类别索引图（行主序）。
    /// 兼容多种导出形态：byte/long/int 直接读（rank3 [1,H,W] 或 rank4 [1,1,H,W]）；
    /// float rank4 多通道 [1,nc,H,W] 按通道 argmax；float 索引图四舍五入。
    /// </summary>
    internal byte[] ParseClassMap(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var r in results)
        {
            Tensor<byte>? tb = null;
            Tensor<long>? tl = null;
            Tensor<int>? ti = null;
            Tensor<float>? tf = null;
            try { tb = r.AsTensor<byte>(); } catch { }
            if (tb != null) return TensorToClassMap(tb.Dimensions, (y, x) => tb[0, y, x]);
            try { tl = r.AsTensor<long>(); } catch { }
            if (tl != null) return TensorToClassMap(tl.Dimensions, (y, x) => (byte)Math.Clamp(tl[0, y, x], 0, 255));
            try { ti = r.AsTensor<int>(); } catch { }
            if (ti != null) return TensorToClassMap(ti.Dimensions, (y, x) => (byte)Math.Clamp(ti[0, y, x], 0, 255));
            try { tf = r.AsTensor<float>(); } catch { }
            if (tf != null)
            {
                var dims = tf.Dimensions;
                if (dims.Length == 4 && dims[1] > 1)
                {
                    // 多通道 logits/probs → 按通道 argmax
                    var ch = dims[1];
                    var h = dims[2];
                    var w = dims[3];
                    var map = new byte[h * w];
                    for (var y = 0; y < h; y++)
                    {
                        for (var x = 0; x < w; x++)
                        {
                            var bestC = 0;
                            var bestV = float.NegativeInfinity;
                            for (var c = 0; c < ch; c++)
                            {
                                var v = tf[0, c, y, x];
                                if (v > bestV)
                                {
                                    bestV = v;
                                    bestC = c;
                                }
                            }
                            map[y * w + x] = (byte)Math.Clamp(bestC, 0, 255);
                        }
                    }
                    return map;
                }
                if (dims.Length == 4 && dims[1] == 1)
                {
                    return TensorToClassMap(dims, (y, x) => (byte)Math.Clamp((int)Math.Round(tf[0, 0, y, x]), 0, 255));
                }
                return TensorToClassMap(dims, (y, x) => (byte)Math.Clamp((int)Math.Round(tf[0, y, x]), 0, 255));
            }
        }
        throw new InvalidOperationException(
            $"节点 {Name}: 模型输出不符合语义分割导出（需 [1,H,W] 像素类别索引图；实际无法解析为 byte/long/int/float 张量）");
    }

    /// <summary>把 [1,(1,)H,W] 索引张量展开为 inputH×inputW 行主序数组。</summary>
    private byte[] TensorToClassMap(ReadOnlySpan<int> dims, Func<int, int, byte> get)
    {
        if (dims.Length == 3 && dims[0] == 1)
        {
            var h = dims[1];
            var w = dims[2];
            var map = new byte[h * w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    map[y * w + x] = get(y, x);
                }
            }
            return map;
        }
        if (dims.Length == 4 && dims[0] == 1 && dims[1] == 1)
        {
            var h = dims[2];
            var w = dims[3];
            var map = new byte[h * w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    map[y * w + x] = get(y, x);
                }
            }
            return map;
        }
        throw new InvalidOperationException(
            $"节点 {Name}: 模型输出形状 [{string.Join(",", dims.ToArray())}] 不符合语义分割导出（需 [1,H,W] 或 [1,1,H,W]）");
    }

    /// <summary>
    /// 单类（nc==1）语义导出与多类的值约定不同，统一重映射为内部契约（类索引 0..nc-1，255=忽略/背景）：
    /// nc==1：ONNX 导出为二值图 {0=非该类(背景), 1=该类}（SemanticSegment head: y.squeeze(1) &gt; 0）——
    /// 1 映射为类别行 0，0 映射为 255（忽略）；不重映射会把 泡棉 像素当越界丢弃、把背景当类别记缺陷（判定颠倒）。
    /// nc&gt;1：argmax 索引 = classes.txt 行序，无需处理。
    /// </summary>
    internal static void RemapSingleClass(byte[] map, int classCount)
    {
        if (classCount != 1) return;
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = map[i] == 1 ? (byte)0 : byte.MaxValue;
        }
    }

    /// <summary>类别图中出现的缺陷类别名（去重，按索引序）。</summary>
    private IEnumerable<string> DistinctDefectClasses(Mat classMap)
    {
        if (!classMap.GetArray(out byte[] buf)) yield break;
        foreach (var v in buf.Distinct().OrderBy(v => v))
        {
            if (v > 0) yield return ClassName(v - 1);
        }
    }

    /// <summary>把旋转矩形填充到掩码（总范围用）。</summary>
    private static void FillRotatedRect(Mat mask, RoiRect rect, int imgW, int imgH)
    {
        var (cx, cy, w, h, angle) = rect.ToPixels(imgW, imgH);
        var rad = angle * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var hw = w / 2.0;
        var hh = h / 2.0;
        Point[] poly =
        {
            new((int)Math.Round(cx + (-hw) * c - (-hh) * s), (int)Math.Round(cy + (-hw) * s + (-hh) * c)),
            new((int)Math.Round(cx + hw * c - (-hh) * s), (int)Math.Round(cy + hw * s + (-hh) * c)),
            new((int)Math.Round(cx + hw * c - hh * s), (int)Math.Round(cy + hw * s + hh * c)),
            new((int)Math.Round(cx + (-hw) * c - hh * s), (int)Math.Round(cy + (-hw) * s + hh * c)),
        };
        Cv2.FillPoly(mask, new[] { poly }, Scalar.All(255));
    }

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

    /// <summary>矢量标注：缺陷连通域轮廓+类别名（红）+ ROI 四边形+名称（黄），源图像素坐标。</summary>
    internal static List<NodeShape> BuildAnnotations(
        int imgW, int imgH, Mat defectMask, Mat classMap, List<(string Name, RoiRect Rect)> rois)
    {
        var shapes = new List<NodeShape>();
        Cv2.FindContours(defectMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        classMap.GetArray(out byte[]? classBuf);
        foreach (var contour in contours)
        {
            if (Cv2.ContourArea(contour) < 1) continue;
            // 轮廓归属类别：轮廓内多数像素的类别（类别图值=索引+1）
            var cls = "";
            if (classBuf != null)
            {
                var box = Cv2.BoundingRect(contour);
                var counts = new Dictionary<byte, int>();
                for (var y = box.Y; y < box.Y + box.Height && y < classMap.Rows; y++)
                {
                    for (var x = box.X; x < box.X + box.Width && x < classMap.Cols; x++)
                    {
                        var v = classBuf[y * classMap.Cols + x];
                        if (v > 0) counts[v] = counts.GetValueOrDefault(v) + 1;
                    }
                }
                var best = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
                if (best.Key > 0) cls = $"类别{best.Key - 1}";
            }
            shapes.Add(new NodeShape { Polys = [contour], Label = cls, Kind = NodeShapeKind.Defect });
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

    /// <summary>缺陷区切图：crop_dir\OK|NG\时间戳_类别_序号.jpg + sidecar（类别/像素数/框/ROI 名/时间）。类别取连通域多数像素。</summary>
    private void SaveDefectCrops(
        Mat img, Mat defectMask, Mat classMap,
        List<(string Name, RoiRect Rect)> rois, List<RoiMeta> metas, string decision)
    {
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir))
        {
            if (Cv2.CountNonZero(defectMask) > 0)
            {
                Log?.Invoke($"[切图] {Name}: 未配置「缺陷区切图保存目录」(crop_dir)，本次缺陷区未保存切图");
            }
            return;
        }
        var sub = decision == "NG" ? "NG" : "OK";
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        classMap.GetArray(out byte[]? classBuf);

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

                // 类别：连通域内多数像素
                var cls = "缺陷";
                long pixels = 0;
                if (classBuf != null)
                {
                    var counts = new Dictionary<byte, int>();
                    for (var y = box.Y; y < box.Y + box.Height && y < classMap.Rows; y++)
                    {
                        for (var x = box.X; x < box.X + box.Width && x < classMap.Cols; x++)
                        {
                            var v = classBuf[y * classMap.Cols + x];
                            if (v > 0) counts[v] = counts.GetValueOrDefault(v) + 1;
                        }
                    }
                    var best = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
                    if (best.Key > 0)
                    {
                        cls = ClassName(best.Key - 1);
                        pixels = best.Value;
                    }
                }

                // 检测项级存图过滤：连通域中心落在哪个检测项内，按该项「是否存图」；不在任何项内默认存
                var save = true;
                var roiName = "";
                for (var ri = 0; ri < rois.Count && ri < metas.Count; ri++)
                {
                    if (NodeRois.ContainsPixel(rois[ri].Rect, box.X + box.Width / 2.0, box.Y + box.Height / 2.0, img.Width, img.Height))
                    {
                        roiName = rois[ri].Name;
                        save = metas[ri].SaveImage;
                        break;
                    }
                }
                if (!save) continue;

                var full = Path.GetFullPath(Path.Combine(dir, sub));
                Directory.CreateDirectory(full);
                var outPath = Path.Combine(full, $"{ts}_{cls}_{idx++}.jpg");
                Cv2.ImWrite(outPath, crop);
                File.WriteAllText(outPath + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    @class = cls,
                    pixels,
                    box = new[] { box.X, box.Y, box.Width, box.Height },
                    roi_name = roiName,
                    triggered = true,
                    created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                }));
                Log?.Invoke($"[切图] {Name}: 已保存 {sub} 缺陷区切图 {cls}({pixels}px) -> {outPath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[切图] {Name}: 保存失败: {ex.Message}");
            }
        }
    }

    /// <summary>整图留存：按本节点判定把原图存到 crop_dir\OK|NG（save_mode = all/ok/ng）。失败只记日志不中断。</summary>
    private void SaveWholeImage(Mat img, string decision)
    {
        if (!SaveImageNode.ShouldSave(_params.GetValueOrDefault("save_mode"), decision)) return;
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir))
        {
            Log?.Invoke($"[整图] {Name}: 整图保存已启用(保存类型={_params.GetValueOrDefault("save_mode")})但未配置「缺陷区切图保存目录」(crop_dir)，本次不保存");
            return;
        }
        try
        {
            var sub = decision == "NG" ? "NG" : "OK";
            var full = Path.GetFullPath(Path.Combine(dir, sub));
            Directory.CreateDirectory(full);
            var outPath = Path.Combine(full, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Name}.jpg");
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

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
