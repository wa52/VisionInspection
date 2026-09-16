using System.Globalization;
using System.IO;
using OpenCvSharp;
using VisionInspection.Models;
using VisionInspection.Services;

namespace VisionInspection.Detection;

/// <summary>
/// 字符识别节点（传统视觉路线，VisionMaster 字符识别同类原理）：
/// 检测项 ROI 内 二值化/形态学预处理 → 连通域字符分割 → 与字模库逐样本匹配 → 识别文本。
/// 字模库契约：目录/字符/n.png（归一化 32×48 二值样本，参数面板「字符训练」弹窗生成；
/// 目录留空 = 方案目录/模板/节点名，海康式随方案管理）。
/// 判定：识别置信度 < 检测项阈值（缺省 0.7）→ NG；目标字符（检测项元数据 x=）非空按判定方式
/// （等于目标字符[默认]/包含目标字符）比对，不匹配或未识别「?」→ NG；目标为空仅按置信度判定；
/// 启用且参与判定项任一 NG → 节点 NG；「仅识别不判定」恒 OK。
/// </summary>
public sealed class CharRecNode : IModelNode
{
    public const string DecisionEqualNg = "等于目标字符";
    public const string DecisionContainNg = "包含目标字符";
    public const string DecisionObserveOnly = "仅识别不判定";
    public const string PolarityBrightOnDark = "亮字暗底";
    public const string PolarityDarkOnBright = "暗字亮底";

    /// <summary>未匹配字符占位符（低分/字模库缺该字符 → 等于/包含比对必失败 → NG）。</summary>
    public const char UnknownChar = '?';

    /// <summary>检测项默认置信度阈值（检测项阈值缺省时回退此值）。</summary>
    public const string DefaultConf = "0.7";

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "model_dir", Label = "字模库目录(空=方案目录/模板/节点名)", Kind = "folder", Default = "" },
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "decision_mode", Label = "判定模式", Kind = "choice", Default = DecisionEqualNg, Choices = [DecisionEqualNg, DecisionContainNg, DecisionObserveOnly] },
        new ParamDef { Key = "bin_method", Label = "二值化方式", Kind = "choice", Default = "固定阈值", Choices = ["固定阈值", "Otsu自动"] },
        new ParamDef { Key = "threshold", Label = "二值化阈值(0-255)", Kind = "double", Default = "128" },
        new ParamDef { Key = "polarity", Label = "字符极性", Kind = "choice", Default = PolarityBrightOnDark, Choices = [PolarityBrightOnDark, PolarityDarkOnBright] },
        new ParamDef { Key = "median", Label = "中值滤波核(0=关,3/5/7·抹背景网点)", Kind = "double", Default = "0" },
        new ParamDef { Key = "dilate", Label = "膨胀次数(连接断裂笔画)", Kind = "double", Default = "0" },
        new ParamDef { Key = "erode", Label = "腐蚀次数(分离粘连字符)", Kind = "double", Default = "0" },
        new ParamDef { Key = "min_char_h", Label = "最小字符高度(px)", Kind = "double", Default = "8" },
        new ParamDef { Key = "max_char_h", Label = "最大字符高度(px,0=不限)", Kind = "double", Default = "0" },
        new ParamDef { Key = "crop_dir", Label = "识别区切图保存目录(可选·按OK/NG分目录)", Kind = "folder", Default = "" },
        new ParamDef { Key = "save_mode", Label = "整图保存", Kind = "choice", Default = "全部", Choices = ["全部", "仅OK", "仅NG"] },
        new ParamDef { Key = "show_binary", Label = "显示二值预处理图(调参辅助)", Kind = "bool", Default = "false" },
        new ParamDef { Key = "scope_index", Label = "总范围ROI索引(检测项管理设置)", Kind = "hidden", Default = "-1" },
    ];

    /// <summary>单检测项识别结果（与 rois 按索引一一对应；停用项 Chars 为空且 Enabled=false）。</summary>
    internal sealed record RoiResult(string Name, bool Enabled, List<SegChar> Chars, RoiMeta Meta, Rect CropRect)
    {
        /// <summary>识别文本（按 X 序拼接字符标签）。</summary>
        public string Text => string.Concat(Chars.Select(c => c.Label));

        /// <summary>置信度 = 最弱字符得分（漏识别一个低分字符即应 NG）；无字符 = 0。</summary>
        public double Conf => Chars.Count == 0 ? 0.0 : Chars.Min(c => c.Score);

        public static RoiResult Disabled(string name) => new(name, false, [], new RoiMeta(), default);
    }

    private List<CharSample>? _templates;
    private bool _noRoiWarned;
    private readonly Dictionary<string, string> _params = new()
    {
        ["model_dir"] = "",
        ["source"] = "@input",
        ["decision_mode"] = DecisionEqualNg,
        ["bin_method"] = "固定阈值",
        ["threshold"] = "128",
        ["polarity"] = PolarityBrightOnDark,
        ["median"] = "0",
        ["dilate"] = "0",
        ["erode"] = "0",
        ["min_char_h"] = "8",
        ["max_char_h"] = "0",
        ["crop_dir"] = "",
        ["save_mode"] = "全部",
        ["show_binary"] = "false",
        ["scope_index"] = "-1",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public CharRecNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "CharRec";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    /// <summary>运行时字模库目录（已解析绝对路径）。</summary>
    public string ResolvedModelDir { get; private set; } = "";

    /// <summary>已加载字模样本数（UI/校验日志用）。</summary>
    public int TemplateCount => _templates?.Count ?? 0;

    public void SetParam(string key, string value)
    {
        var oldModelDir = _params.GetValueOrDefault("model_dir");
        _params[key] = value;
        // 只有字模库目录变化才需要重载；预处理/判定/ROI/切图目录热生效
        if (key == "model_dir" && value != oldModelDir)
        {
            _templates = null;
        }
    }

    /// <summary>
    /// 确保字模库已加载：目录留空 = 方案目录/模板/节点名（海康式随方案管理，用户不必选目录）。
    /// 目录不存在或库为空抛异常（构建期由 Pipeline 记校验日志，运行期该节点 ERROR）。
    /// </summary>
    public void EnsureLoaded(string baseDir)
    {
        if (_templates != null) return;
        var raw = _params.GetValueOrDefault("model_dir");
        ResolvedModelDir = string.IsNullOrWhiteSpace(raw)
            ? Path.GetFullPath(Path.Combine(baseDir ?? "", "模板", SafeName()))
            : RecipeStore.Resolve(new Recipe { BaseDir = baseDir }, raw);
        _templates = CharTemplateLibrary.Load(ResolvedModelDir);
        if (_templates.Count == 0)
        {
            _templates = null;
            throw new InvalidDataException($"字模库为空或目录不存在: {ResolvedModelDir}（请打开参数面板「字符训练」建字模）");
        }
    }

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        if (_templates == null)
        {
            throw new InvalidOperationException(
                $"节点 {Name}: 字模库未加载（构建时加载失败，请检查「字模库目录」: {ResolvedModelDir}）");
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

        // 预处理参数（识别与「字符训练」弹窗共用同一套，保证训练/识别一致）
        var threshold = ParseDouble(_params.GetValueOrDefault("threshold"), 128);
        var otsu = string.Equals(_params.GetValueOrDefault("bin_method"), "Otsu自动", StringComparison.Ordinal);
        var brightOnDark = !string.Equals(_params.GetValueOrDefault("polarity"), PolarityDarkOnBright, StringComparison.Ordinal);
        var dilate = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("dilate"), 0));
        var erode = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("erode"), 0));
        var minCharH = Math.Max(1, (int)Math.Round(ParseDouble(_params.GetValueOrDefault("min_char_h"), 8)));
        var maxCharH = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("max_char_h"), 0));
        var mode = _params.GetValueOrDefault("decision_mode") ?? DecisionEqualNg;
        var defaultThr = double.Parse(DefaultConf, CultureInfo.InvariantCulture);

        // ROI 解析：节点私有 own_rois（含检测项元数据）+ 位置修正；空 = 整图当单一识别区（不判定，恒 OK）
        var (rois, metas) = ResolveRoisFull(img, ctx);
        using var grayRaw = ToGray(img);
        using var grayFiltered = ApplyMedian(grayRaw, (int)Math.Round(ParseDouble(_params.GetValueOrDefault("median"), 0)));
        var gray = grayFiltered ?? grayRaw;
        var perRoi = new List<RoiResult>(Math.Max(1, rois.Count));

        if (rois.Count == 0)
        {
            if (!_noRoiWarned)
            {
                _noRoiWarned = true;
                Log?.Invoke($"[字符识别] {Name}: 未画检测项（整图识别不判定；请在参数面板「检测区域 (ROI)」画文本区域后参与判定）");
            }
            using var bin = CharRecognizer.Binarize(gray, threshold, otsu, brightOnDark);
            CharRecognizer.MorphApply(bin, dilate, erode);
            var chars = CharRecognizer.Recognize(bin, minCharH, maxCharH, _templates);
            perRoi.Add(new RoiResult("", true, chars, new RoiMeta(), new Rect(0, 0, img.Width, img.Height)));
        }
        else
        {
            for (var ri = 0; ri < rois.Count; ri++)
            {
                var (name, rect) = rois[ri];
                var meta = ri < metas.Count ? metas[ri] : new RoiMeta();
                if (!meta.Enabled)
                {
                    perRoi.Add(RoiResult.Disabled(name)); // 停用：不识别，输出 roi_名称=停用
                    continue;
                }
                var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
                var cropRect = YoloNode.ComputeRoiCropRect((int)cx, (int)cy, w, h, angle, img.Width, img.Height);
                List<SegChar> chars;
                using (var cropView = new Mat(gray, cropRect))
                using (var bin = CharRecognizer.Binarize(cropView, threshold, otsu, brightOnDark))
                using (var preciseMask = NodeRois.CreateMask(rect, img.Width, img.Height))
                using (var preciseCrop = new Mat(preciseMask, cropRect))
                {
                    CharRecognizer.MorphApply(bin, dilate, erode);
                    Cv2.BitwiseAnd(bin, preciseCrop, bin);
                    chars = CharRecognizer.Recognize(bin, minCharH, maxCharH, _templates);
                }
                // 字符坐标从 ROI 裁剪区映射回源图
                for (var ci = 0; ci < chars.Count; ci++)
                {
                    var c = chars[ci];
                    chars[ci] = c with { Box = new Rect(c.Box.X + cropRect.X, c.Box.Y + cropRect.Y, c.Box.Width, c.Box.Height) };
                }
                perRoi.Add(new RoiResult(name, true, chars, meta, cropRect));
            }

            // 总范围约束：scope_index 指向的 ROI 为总范围，其余 ROI 的字符只保留中心落在总范围内的
            var scopeIndex = NodeRois.ParseScopeIndex(_params);
            if (scopeIndex >= 0 && scopeIndex < rois.Count)
            {
                var scope = rois[scopeIndex].Rect;
                var removed = 0;
                for (var ri = 0; ri < perRoi.Count; ri++)
                {
                    if (ri == scopeIndex || !perRoi[ri].Enabled) continue;
                    var kept = perRoi[ri].Chars
                        .Where(c => NodeRois.ContainsPixel(
                            scope, c.Box.X + c.Box.Width / 2.0, c.Box.Y + c.Box.Height / 2.0, img.Width, img.Height))
                        .ToList();
                    removed += perRoi[ri].Chars.Count - kept.Count;
                    perRoi[ri] = perRoi[ri] with { Chars = kept };
                }
                if (removed > 0)
                {
                    Log?.Invoke($"[总范围] {Name}: 按总范围 ROI「{rois[scopeIndex].Name}」过滤，移除范围外字符 {removed} 个");
                }
            }
        }

        // 判定：无检测项 = 不判定恒 OK；否则启用且参与判定项任一不过 → NG
        var decision = rois.Count == 0 ? "OK" : ComputeDecision(mode, perRoi);

        // 识别区切图 + 整图留存（按节点判定分 OK/NG 目录）
        SaveRoiCrops(img, rois, perRoi, decision);
        SaveWholeImage(img, decision);

        // 输出图：调参辅助开关打开时显示 二值化+形态学 后的预处理图（看到分割器看到什么，
        // 点阵字符调 阈值/膨胀/极性 时每次执行立见效果）；否则输出原图克隆
        Mat output;
        if (ParseFlag(_params.GetValueOrDefault("show_binary")))
        {
            using var binPreview = CharRecognizer.Binarize(gray, threshold, otsu, brightOnDark);
            CharRecognizer.MorphApply(binPreview, dilate, erode);
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

        // 矢量标注：检测项四边形（NG 红/其余黄）+ 逐字符框（达标绿/低分红），屏幕常量渲染不进位图
        var observing = string.Equals(mode, DecisionObserveOnly, StringComparison.Ordinal); // 仅识别不判定：节点恒 OK，标注不染红
        for (var ri = 0; ri < rois.Count && ri < perRoi.Count; ri++)
        {
            var (name, rect) = rois[ri];
            var (cx, cy, w, h, angle) = rect.ToPixels(img.Width, img.Height);
            var item = perRoi[ri];
            var thr = item.Meta.ThresholdValue ?? defaultThr;
            var itemNg = !observing && item.Enabled && NodeRois.Judges(item.Meta)
                && !CheckPass(mode, item.Text, item.Conf, item.Meta.Target, thr);
            nodeResult.Annotations.Add(new NodeShape
            {
                Polys = [PatchCoreNode.RoiQuad(cx, cy, w, h, angle)],
                Label = item.Enabled ? $"{name}:{item.Text} {item.Conf:F2}" : $"{name}(停用)",
                Kind = itemNg ? NodeShapeKind.Defect : NodeShapeKind.Info,
            });
            if (!item.Enabled) continue;
            foreach (var c in item.Chars)
            {
                nodeResult.Annotations.Add(new NodeShape
                {
                    Box = c.Box,
                    Label = $"{c.Label} {c.Score:F2}",
                    Kind = c.Score >= thr ? NodeShapeKind.Ok : NodeShapeKind.Defect,
                });
            }
        }

        // 输出值：整体文本/字符数/最低置信度 + 逐检测项 text/conf/match
        var enabledItems = perRoi.Where(r => r.Enabled).ToList();
        nodeResult.Values["text"] = string.Join(" ", enabledItems.Select(i => i.Text));
        nodeResult.Values["count"] = enabledItems.Sum(i => i.Chars.Count).ToString();
        nodeResult.Values["conf"] = (enabledItems.Count == 0 ? 0.0 : enabledItems.Min(i => i.Conf))
            .ToString("F3", CultureInfo.InvariantCulture);
        foreach (var item in perRoi)
        {
            var key = $"roi_{item.Name}";
            if (!item.Enabled)
            {
                nodeResult.Values[key] = "停用";
                continue;
            }
            nodeResult.Values[$"{key}_text"] = item.Text;
            nodeResult.Values[$"{key}_conf"] = item.Conf.ToString("F3", CultureInfo.InvariantCulture);
            nodeResult.Values[$"{key}_match"] = !NodeRois.Judges(item.Meta)
                ? "仅观察"
                : CheckPass(mode, item.Text, item.Conf, item.Meta.Target, item.Meta.ThresholdValue ?? defaultThr) ? "1" : "0";
        }

        return nodeResult;
    }

    /// <summary>
    /// 判定（纯逻辑，可单测）：仅识别不判定 → 恒 OK；
    /// 否则逐启用且参与判定项 CheckPass，任一不过 → NG。
    /// </summary>
    internal static string ComputeDecision(string mode, IReadOnlyList<RoiResult> items)
    {
        if (string.Equals(mode, DecisionObserveOnly, StringComparison.Ordinal)) return "OK";
        foreach (var item in items)
        {
            if (!item.Enabled || !NodeRois.Judges(item.Meta)) continue;
            if (!CheckPass(mode, item.Text, item.Conf, item.Meta.Target,
                    item.Meta.ThresholdValue ?? double.Parse(DefaultConf, CultureInfo.InvariantCulture)))
            {
                return "NG";
            }
        }
        return "OK";
    }

    /// <summary>
    /// 单检测项判定（纯逻辑）：置信度 ≥ 该项阈值；目标字符非空再按判定方式比对
    /// （等于：全文一致；包含：含目标子串；未识别「?」必不匹配）。目标空 → 仅按置信度。
    /// </summary>
    internal static bool CheckPass(string mode, string text, double conf, string? target, double threshold)
    {
        if (conf < threshold) return false;
        if (string.IsNullOrEmpty(target)) return true;
        return string.Equals(mode, DecisionContainNg, StringComparison.Ordinal)
            ? text.Contains(target, StringComparison.Ordinal)
            : string.Equals(text, target, StringComparison.Ordinal);
    }

    /// <summary>解析本节点的检测区域：节点私有 own_rois（含检测项元数据）+ 位置修正（若本节点在修正目标列表中）。</summary>
    private (List<(string Name, RoiRect Rect)> Rois, List<RoiMeta> Metas) ResolveRoisFull(Mat img, PipelineRunContext ctx)
    {
        var items = NodeRois.ParseOwnFull(_params.GetValueOrDefault("own_rois"));
        var rois = NodeRois.ApplyPoseCorrection(
            items.Select(i => (i.Name, i.Rect)).ToList(), Name, img.Width, img.Height, ctx.PoseCorrection);
        return (rois, items.Select(i => i.Meta).ToList());
    }

    /// <summary>单通道/四通道 → 灰度（每次运行新 Mat，调用方负责释放）。</summary>
    internal static Mat ToGray(Mat img)
    {
        if (img.Channels() == 1) return img.Clone();
        var dst = new Mat();
        Cv2.CvtColor(img, dst, img.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return dst;
    }

    /// <summary>中值滤波（点阵喷码背景网点降噪：抹掉孤立小点、保留字符点团；k=3/5/7 奇数，k&lt;3 返回 null=不滤波）。</summary>
    internal static Mat? ApplyMedian(Mat gray, int k)
    {
        if (k < 3) return null;
        if (k % 2 == 0) k += 1;
        var dst = new Mat();
        Cv2.MedianBlur(gray, dst, k);
        return dst;
    }

    /// <summary>输出底图：3 通道 BGR 克隆（框/文字改由 UI 矢量叠加层渲染，不进位图）。</summary>
    private static Mat BuildBaseImage(Mat img)
    {
        if (img.Channels() == 3) return img.Clone();
        var mat = new Mat();
        Cv2.CvtColor(img, mat, img.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return mat;
    }

    /// <summary>识别区切图：crop_dir\OK|NG\时间戳_ROI名_文本_序号.jpg + sidecar（roi_name/文本/置信度/目标字符）；检测项级「是否存图」过滤；目录未配置记日志。</summary>
    private void SaveRoiCrops(
        Mat img, List<(string Name, RoiRect Rect)> rois, IReadOnlyList<RoiResult> items, string decision)
    {
        var dir = _params.GetValueOrDefault("crop_dir");
        if (string.IsNullOrWhiteSpace(dir))
        {
            if (items.Any(i => i.Enabled && i.Chars.Count > 0))
            {
                Log?.Invoke($"[切图] {Name}: 未配置「识别区切图保存目录」(crop_dir)，本次识别结果未保存切图");
            }
            return;
        }
        var sub = decision == "NG" ? "NG" : "OK";
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var idx = 0; // 跨检测项全局编号：重名检测项（RoiManagerDialog 允许）文件名不互覆
        for (var ri = 0; ri < rois.Count && ri < items.Count; ri++)
        {
            var item = items[ri];
            if (!item.Enabled || !item.Meta.SaveImage) continue; // 停用/检测项级不存图跳过
            try
            {
                var cropRect = item.CropRect.Width > 0 && item.CropRect.Height > 0
                    ? item.CropRect
                    : new Rect(0, 0, img.Width, img.Height);
                using var crop = new Mat(img, cropRect).Clone();
                var full = Path.GetFullPath(Path.Combine(dir, sub));
                Directory.CreateDirectory(full);
                var outPath = Path.Combine(full, $"{ts}_{SafeFileText(item.Name)}_{SafeFileText(item.Text)}_{idx++}.jpg");
                Cv2.ImWrite(outPath, crop);
                File.WriteAllText(outPath + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    roi_name = item.Name,
                    text = item.Text,
                    conf = item.Conf,
                    target = item.Meta.Target,
                    count = item.Chars.Count,
                    created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                }));
                Log?.Invoke($"[切图] {Name}: 已保存 {sub} 识别区切图 {item.Name}({item.Text}) -> {outPath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[切图] {Name}: 保存失败: {ex.Message}");
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
            Log?.Invoke($"[整图] {Name}: 整图保存已启用(保存类型={_params.GetValueOrDefault("save_mode")})但未配置「识别区切图保存目录」(crop_dir)，本次不保存");
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

    /// <summary>文本 → 文件名安全段（剔除路径非法字符；识别文本含 ?/: 等时兜底）。</summary>
    private static string SafeFileText(string text) =>
        string.Join("_", text.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private string SafeName()
    {
        var safe = string.Join("_", Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? "未命名" : safe;
    }

    private static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>布尔参数解析（1/true/是 = 开；与位置修正 show_* 同约定）。</summary>
    private static bool ParseFlag(string? value) =>
        value is null || (double.TryParse(value, out var v) ? v != 0 : value is "是" or "true" or "True");

    public void Dispose()
    {
        _templates = null; // 字模样本为托管字节，无原生资源
    }
}
