using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 图像二值化节点：取原图或上游输出图，转灰度后做可调阈值二值化。
/// source = @input（原图）或上游节点名；threshold/maxval/type/otsu 全部参数可调。
/// 输出单通道二值图（下游显示/保存均可渲染，MatToBitmap 自带灰度兼容）。
/// 不参与判定（恒 OK）。
/// </summary>
public sealed class BinarizeNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "threshold", Label = "阈值 (0-255)", Kind = "double", Default = "128" },
        new ParamDef { Key = "maxval", Label = "最大值", Kind = "double", Default = "255" },
        new ParamDef
        {
            Key = "type",
            Label = "二值化类型",
            Kind = "choice",
            Default = "二值化",
            Choices = ["二值化", "反二值化", "截断", "低于阈值归零", "高于阈值归零"],
        },
        new ParamDef { Key = "otsu", Label = "Otsu自动阈值", Kind = "bool", Default = "false" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["threshold"] = "128",
        ["maxval"] = "255",
        ["type"] = "二值化",
        ["otsu"] = "false",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public BinarizeNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "Binarize";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var source = _params.GetValueOrDefault("source") ?? "@input";
        var img = source == "@input" ? ctx.Input : (ctx.Images.TryGetValue(source, out var im) ? im : null);
        if (img == null)
        {
            Log?.Invoke($"[Binarize] {Name}: 图像来源未找到: {source}（来源节点不存在或不在本节点上游）");
            return new NodeResult { Decision = "OK" };
        }
        if (img.Empty())
        {
            // Threshold 对空 Mat 不报错、会静默产出 0x0 输出，必须在这里拦住
            Log?.Invoke($"[Binarize] {Name}: 图像来源为空: {source}");
            return new NodeResult { Decision = "OK" };
        }

        var threshold = ParseDouble(_params.GetValueOrDefault("threshold"), 128);
        var maxval = ParseDouble(_params.GetValueOrDefault("maxval"), 255);
        var type = ParseType(_params.GetValueOrDefault("type"));
        var useOtsu = string.Equals(_params.GetValueOrDefault("otsu"), "true", StringComparison.OrdinalIgnoreCase);
        if (useOtsu)
        {
            type |= ThresholdTypes.Otsu;
        }

        Mat? gray = null;
        try
        {
            if (img.Channels() == 1)
            {
                gray = img;
            }
            else
            {
                gray = new Mat();
                Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            }

            var dst = new Mat();
            Cv2.Threshold(gray, dst, threshold, maxval, type);

            Log?.Invoke($"[Binarize] {Name}: type={_params.GetValueOrDefault("type")} threshold={threshold:0.#} maxval={maxval:0.#} otsu={useOtsu} -> {dst.Width}x{dst.Height}");
            return new NodeResult
            {
                Decision = "OK",
                OutputImage = dst,
                Values =
                {
                    ["out_w"] = dst.Width.ToString(),
                    ["out_h"] = dst.Height.ToString(),
                    ["threshold"] = threshold.ToString("0.#"),
                },
            };
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Binarize] {Name}: 二值化失败: {ex.Message}");
            return new NodeResult { Decision = "OK", Error = ex.Message };
        }
        finally
        {
            if (!ReferenceEquals(gray, img)) gray?.Dispose();
        }
    }

    private static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, out var v) ? v : fallback;

    /// <summary>归一化二值化类型：新配方存中文，旧配方存 OpenCV 英文名，两者都接受。</summary>
    private static ThresholdTypes ParseType(string? raw) => raw?.Trim() switch
    {
        "反二值化" or "BinaryInv" => ThresholdTypes.BinaryInv,
        "截断" or "Truncate" => ThresholdTypes.Trunc,
        "低于阈值归零" or "Tozero" => ThresholdTypes.Tozero,
        "高于阈值归零" or "TozeroInv" => ThresholdTypes.TozeroInv,
        "二值化" or "Binary" => ThresholdTypes.Binary,
        _ => ThresholdTypes.Binary,
    };

    public void Dispose() { }
}
