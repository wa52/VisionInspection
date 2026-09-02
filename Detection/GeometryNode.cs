using OpenCvSharp;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 几何变换节点：对原图或上游输出图依次应用 缩放 → 旋转 → 翻转（三个变换同时生效，填了就起作用）。
/// scale=1 不缩放；angle=0 不旋转（正=逆时针，expand=true 扩边到完整可见）；flip_code=不翻转 则不翻。
/// 旧方案 op 字段（resize/rotate/flip 单选）自动迁移为等价新格式。不参与判定（恒 OK）。
/// </summary>
public sealed class GeometryNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source", Label = "图像来源", Kind = "nodesource", Default = "@input" },
        new ParamDef { Key = "scale", Label = "缩放倍数(1=不缩放)", Kind = "double", Default = "1.0" },
        new ParamDef { Key = "angle", Label = "旋转角度(度，0=不旋转)", Kind = "double", Default = "0" },
        new ParamDef { Key = "expand", Label = "旋转扩边", Kind = "bool", Default = "true" },
        new ParamDef { Key = "flip_code", Label = "翻转方向", Kind = "choice", Default = "不翻转", Choices = ["不翻转", "左右翻转", "上下翻转", "两者都翻"] },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "@input",
        ["scale"] = "1.0",
        ["angle"] = "0",
        ["expand"] = "true",
        ["flip_code"] = "不翻转",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    public GeometryNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
        MigrateLegacyOp();
    }

    /// <summary>旧方案兼容：旧格式用 op 单选一个变换，迁移为「全部变换按序生效」的新格式。</summary>
    private void MigrateLegacyOp()
    {
        if (!_params.TryGetValue("op", out var op)) return;
        _params.Remove("op");
        switch (op)
        {
            case "rotate":
                _params["scale"] = "1.0";
                _params["flip_code"] = "不翻转";
                break;
            case "flip":
                _params["scale"] = "1.0";
                _params["angle"] = "0";
                break;
            default: // resize
                _params["angle"] = "0";
                _params["flip_code"] = "不翻转";
                break;
        }
    }

    public string Name { get; set; }
    public string Type => "Geometry";
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
            Log?.Invoke($"[Geometry] {Name}: 图像来源未找到: {source}（来源节点不存在或不在本节点上游）");
            return new NodeResult { Decision = "OK" };
        }
        if (img.Empty())
        {
            Log?.Invoke($"[Geometry] {Name}: 图像来源为空: {source}");
            return new NodeResult { Decision = "OK" };
        }

        Mat current = img;   // 当前处理图（引用）
        Mat? owned = null;   // 本节点创建的中间结果（负责释放）
        var applied = new List<string>();
        try
        {
            // 1) 缩放
            var scale = double.TryParse(_params.GetValueOrDefault("scale"), out var s) && s > 0 ? s : 1.0;
            if (Math.Abs(scale - 1.0) >= 1e-6)
            {
                var next = new Mat();
                var width = Math.Max(1, (int)Math.Round(current.Width * scale));
                var height = Math.Max(1, (int)Math.Round(current.Height * scale));
                Cv2.Resize(current, next, new Size(width, height), 0, 0, InterpolationFlags.Linear);
                owned?.Dispose();
                owned = next;
                current = next;
                applied.Add($"scale={scale:0.###}");
            }

            // 2) 旋转（正=逆时针，expand 扩边）
            var angle = double.TryParse(_params.GetValueOrDefault("angle"), out var a) ? a : 0;
            if (Math.Abs(angle) >= 1e-6)
            {
                var next = RotateMat(current, angle);
                owned?.Dispose();
                owned = next;
                current = next;
                applied.Add($"angle={angle:0.##}");
            }

            // 3) 翻转
            var flip = ParseFlip(_params.GetValueOrDefault("flip_code"));
            if (flip is { } mode)
            {
                var next = new Mat();
                Cv2.Flip(current, next, mode);
                owned?.Dispose();
                owned = next;
                current = next;
                applied.Add($"flip={_params.GetValueOrDefault("flip_code")}");
            }

            // 无任何变换 → 克隆输入，保持「有图像输出」语义
            if (owned == null)
            {
                owned = img.Clone();
                current = owned;
            }

            Log?.Invoke($"[Geometry] {Name}: {(applied.Count == 0 ? "无变换(原样输出)" : string.Join(" + ", applied))} -> {current.Width}x{current.Height}");
            return new NodeResult
            {
                Decision = "OK",
                OutputImage = owned,
                Values =
                {
                    ["out_w"] = current.Width.ToString(),
                    ["out_h"] = current.Height.ToString(),
                    ["transforms"] = applied.Count == 0 ? "none" : string.Join("+", applied),
                },
            };
        }
        catch (Exception ex)
        {
            owned?.Dispose();
            Log?.Invoke($"[Geometry] {Name}: 几何变换失败: {ex.Message}");
            return new NodeResult { Decision = "OK", Error = ex.Message };
        }
    }

    /// <summary>旋转矩阵与 Cv2.GetRotationMatrix2D 同式（正角=逆时针），expand 时平移到新外接框中心。</summary>
    private Mat RotateMat(Mat img, double angle)
    {
        var expand = !string.Equals(_params.GetValueOrDefault("expand"), "false", StringComparison.OrdinalIgnoreCase);
        var rad = angle * Math.PI / 180.0;
        var alpha = Math.Cos(rad);
        var beta = Math.Sin(rad);
        var cx = img.Width / 2.0;
        var cy = img.Height / 2.0;
        var tx = (1 - alpha) * cx - beta * cy;
        var ty = beta * cx + (1 - alpha) * cy;
        var dstW = img.Width;
        var dstH = img.Height;
        if (expand)
        {
            dstW = (int)Math.Round(Math.Abs(img.Width * alpha) + Math.Abs(img.Height * beta));
            dstH = (int)Math.Round(Math.Abs(img.Width * beta) + Math.Abs(img.Height * alpha));
            tx += dstW / 2.0 - cx;
            ty += dstH / 2.0 - cy;
        }

        // 2x3 CV_64FC1 旋转矩阵（FromArray 二维数组重载）
        using var mat = Mat.FromArray(new double[,] { { alpha, beta, tx }, { -beta, alpha, ty } });
        var dst = new Mat();
        Cv2.WarpAffine(img, dst, mat, new Size(dstW, dstH), InterpolationFlags.Linear);
        return dst;
    }

    /// <summary>翻转方向解析；不翻转/空 → null（跳过）。兼容旧英文值与新中文值。</summary>
    private static FlipMode? ParseFlip(string? raw) => raw switch
    {
        "Horizontal" or "左右翻转" => FlipMode.Y,     // 实测 FlipMode.Y=1 绕 y 轴=左右
        "Vertical" or "上下翻转" => FlipMode.X,       // FlipMode.X=0 绕 x 轴=上下
        "Both" or "两者都翻" => (FlipMode)(-1),
        _ => null,
    };

    public void Dispose() { }
}
