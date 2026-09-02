using OpenCvSharp;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 颜色变换节点（参照海康 VisionMaster 颜色变换）：任意来源图 → 灰度图。
/// 灰度化方式：加权平均(0.299R+0.587G+0.114B，OpenCV BGR2GRAY 同款)/算术平均/单分量提取(R/G/B)。
/// 输出单通道灰度图（下游节点与 UI 显示均兼容）；工具节点，不参与判定（恒 OK）。
/// </summary>
public sealed class ColorTransformNode : IModelNode
{
    public const string MethodWeighted = "加权平均";
    public const string MethodAverage = "算术平均";
    public const string MethodR = "R分量";
    public const string MethodG = "G分量";
    public const string MethodB = "B分量";

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef
        {
            Key = "method",
            Label = "灰度化方式",
            Kind = "choice",
            Default = MethodWeighted,
            Choices = [MethodWeighted, MethodAverage, MethodR, MethodG, MethodB],
        },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["method"] = MethodWeighted,
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public ColorTransformNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "ColorTransform";
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
            Log?.Invoke($"[颜色变换] {Name}: 图像来源未找到: {source}（来源节点不存在或不在本节点上游）");
            return new NodeResult { Decision = "OK" };
        }
        if (img.Empty())
        {
            Log?.Invoke($"[颜色变换] {Name}: 图像来源为空: {source}");
            return new NodeResult { Decision = "OK" };
        }

        var method = _params.GetValueOrDefault("method") ?? MethodWeighted;
        try
        {
            // BGRA 先转 BGR，统一按 BGR 处理
            using var bgrView = img.Channels() == 4 ? ToBgr(img) : null;
            var src = bgrView ?? img;
            var dst = Transform(src, method);
            Log?.Invoke($"[颜色变换] {Name}: 方式={method} {src.Width}x{src.Height}x{src.Channels()} -> 灰度 {dst.Width}x{dst.Height}");
            return new NodeResult
            {
                Decision = "OK",
                OutputImage = dst,
                Values =
                {
                    ["out_w"] = dst.Width.ToString(),
                    ["out_h"] = dst.Height.ToString(),
                    ["method"] = method,
                },
            };
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[颜色变换] {Name}: 变换失败: {ex.Message}");
            return new NodeResult { Decision = "OK", Error = ex.Message };
        }
    }

    /// <summary>按方式转灰度（返回新 Mat，所有权归 NodeResult）；已是单通道则透传克隆。</summary>
    private static Mat Transform(Mat src, string method)
    {
        if (src.Channels() == 1)
        {
            return src.Clone();
        }
        switch (method)
        {
            case MethodAverage:
            {
                // (B+G+R)/3：经 32F 核变换，避免 8U 逐通道求和饱和（核须 1×3 与通道数一致）
                using var kernel = Mat.FromArray(new[,] { { 1f / 3, 1f / 3, 1f / 3 } });
                using var sum = new Mat();
                Cv2.Transform(src, sum, kernel);
                var dst = new Mat();
                sum.ConvertTo(dst, MatType.CV_8UC1);
                return dst;
            }
            case MethodR:
                return Extract(src, 2);
            case MethodG:
                return Extract(src, 1);
            case MethodB:
                return Extract(src, 0);
            default:
            {
                // 加权平均 = OpenCV BGR2GRAY（0.299R + 0.587G + 0.114B）
                var dst = new Mat();
                Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY);
                return dst;
            }
        }
    }

    private static Mat Extract(Mat src, int index)
    {
        var dst = new Mat();
        Cv2.ExtractChannel(src, dst, index);
        return dst;
    }

    private static Mat ToBgr(Mat bgra)
    {
        var dst = new Mat();
        Cv2.CvtColor(bgra, dst, ColorConversionCodes.BGRA2BGR);
        return dst;
    }

    public void Dispose() { }
}
