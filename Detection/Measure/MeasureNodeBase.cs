using System.Globalization;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 几何测量节点公共基类（线线/线圆/圆圆/点圆测量共享）：
/// 参数存取、上游几何引用解析（ctx.Results 输出值契约，条件检测同款机制）、
/// 判定限管线、ERROR 停线语义、矢量标注助手。测量为纯几何计算，不依赖图像（底图为空不报错、不输出位图）。
/// </summary>
public abstract class MeasureNodeBase : IModelNode
{
    protected readonly Dictionary<string, string> _params;

    /// <summary>运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    protected MeasureNodeBase(string name, Dictionary<string, string>? init, Dictionary<string, string> defaults)
    {
        Name = name;
        _params = new Dictionary<string, string>(defaults);
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public abstract string Type { get; }
    public bool Enabled { get; set; } = true;
    public abstract IReadOnlyList<ParamDef> ParamDefs { get; }
    public IReadOnlyDictionary<string, string> Params => _params;
    public void SetParam(string key, string value) => _params[key] = value;
    public abstract NodeResult Run(Mat bgr, PipelineRunContext ctx);
    public virtual void Dispose() { }

    /// <summary>数值参数解析（空/非法回退默认）。</summary>
    protected double P(string key, double fallback) =>
        double.TryParse(_params.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>判定限开关：choice 取「判断」为启用（默认「不判断」恒过）。</summary>
    protected bool CheckOn(string key) => string.Equals(_params.GetValueOrDefault(key), "判断", StringComparison.Ordinal);

    /// <summary>输出角度范围（-90°~90° / -180°~180°，默认 Linear 同 VM 出厂值）。</summary>
    protected string AngleRange => _params.GetValueOrDefault("angle_range") ?? MeasureAngleRange.Linear;

    /// <summary>数值输出格式（两位小数，与直线/圆查找输出一致）。</summary>
    protected static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析上游引用参数（Kind=noderesult）：参数值为节点名，从 ctx.Results 取其输出 Values；
    /// 未选/不在本节点上游 → error（节点 ERROR 停线）。
    /// </summary>
    protected IReadOnlyDictionary<string, string>? ResolveRef(string paramKey, string what, PipelineRunContext ctx, out string? nodeName, out string? error)
    {
        error = null;
        nodeName = null;
        var name = (_params.GetValueOrDefault(paramKey) ?? "").Trim();
        if (name.Length == 0)
        {
            error = $"未选择{what}来源节点（请在参数面板选择上游节点）";
            return null;
        }
        if (!ctx.Results.TryGetValue(name, out var nr))
        {
            error = $"引用的{what}来源节点「{name}」不存在或不在本节点上游";
            return null;
        }
        nodeName = name;
        return nr.Values;
    }

    /// <summary>判定限组：启用项全部落在 [low, high] → 通过。</summary>
    protected bool PassLimit(string checkKey, string lowKey, string highKey, double value, string name, List<string> failed)
    {
        var ok = MeasureCore.CheckLimit(CheckOn(checkKey), P(lowKey, 0), P(highKey, 0), value);
        if (!ok)
        {
            failed.Add(name);
        }
        return ok;
    }

    /// <summary>
    /// 交点判定限（X/Y 一组，交点1/交点2 共用）：交点不存在（不相交/内含/外离）时启用即不过。
    /// </summary>
    protected void CheckIntersection(string xKey, string yKey, string name, List<(double X, double Y)> inters, int index, List<string> failed)
    {
        var xOn = CheckOn(xKey);
        var yOn = CheckOn(yKey);
        if (!xOn && !yOn) return;
        if (inters.Count <= index)
        {
            if (xOn) failed.Add(name + "X判断");
            if (yOn) failed.Add(name + "Y判断");
            return;
        }
        if (xOn) PassLimit(xKey, xKey.Replace("_check", "_low"), xKey.Replace("_check", "_high"), inters[index].X, name + "X判断", failed);
        if (yOn) PassLimit(yKey, yKey.Replace("_check", "_low"), yKey.Replace("_check", "_high"), inters[index].Y, name + "Y判断", failed);
    }

    // ===== 上游几何提取 =====

    /// <summary>从上游节点输出提取直线（x1/y1/x2/y2，直线查找输出契约）。</summary>
    protected static MeasureLine? ExtractLine(IReadOnlyDictionary<string, string> values, string nodeName, out string? error)
    {
        error = null;
        if (values.TryGetValue("x1", out var x1s) && values.TryGetValue("y1", out var y1s)
            && values.TryGetValue("x2", out var x2s) && values.TryGetValue("y2", out var y2s)
            && TryD(x1s, out var x1) && TryD(y1s, out var y1) && TryD(x2s, out var x2) && TryD(y2s, out var y2))
        {
            return new MeasureLine(x1, y1, x2, y2);
        }
        error = $"上游节点「{nodeName}」没有直线输出（未找到直线或未执行成功）";
        return null;
    }

    /// <summary>从上游节点输出提取圆（center_x/center_y/radius，圆查找输出契约）。</summary>
    protected static MeasureCircle? ExtractCircle(IReadOnlyDictionary<string, string> values, string nodeName, out string? error)
    {
        error = null;
        if (values.TryGetValue("center_x", out var cxs) && values.TryGetValue("center_y", out var cys)
            && values.TryGetValue("radius", out var rs)
            && TryD(cxs, out var cx) && TryD(cys, out var cy) && TryD(rs, out var r))
        {
            return new MeasureCircle(cx, cy, r);
        }
        error = $"上游节点「{nodeName}」没有圆输出（未找到圆或未执行成功）";
        return null;
    }

    /// <summary>从上游节点输出提取点（loc_x/loc_y + loc_valid=1 定位契约，轮廓匹配/快速匹配/直线/圆查找均有）。</summary>
    protected static (double X, double Y)? ExtractPoint(IReadOnlyDictionary<string, string> values, string nodeName, out string? error)
    {
        error = null;
        if (values.TryGetValue("loc_valid", out var valid) && !string.Equals(valid.Trim(), "1", StringComparison.Ordinal))
        {
            error = $"上游节点「{nodeName}」定位无效（未找到目标，loc_valid={valid}）";
            return null;
        }
        if (values.TryGetValue("loc_x", out var xs) && values.TryGetValue("loc_y", out var ys)
            && TryD(xs, out var x) && TryD(ys, out var y))
        {
            return (x, y);
        }
        error = $"上游节点「{nodeName}」没有定位点输出（缺 loc_x/loc_y）";
        return null;
    }

    private static bool TryD(string? raw, out double v) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // ===== 结果构造 =====

    /// <summary>OK 结果：值入 Values，判定 = 全部启用判定限通过 → OK，否则 NG（failChecks 记未过项）。</summary>
    protected NodeResult BuildResult(Dictionary<string, string> values, List<string> failedChecks, Mat? baseImage = null)
    {
        var nr = new NodeResult
        {
            Decision = failedChecks.Count == 0 ? "OK" : "NG",
            OutputImage = baseImage,
        };
        foreach (var kv in values) nr.Values[kv.Key] = kv.Value;
        if (failedChecks.Count > 0)
        {
            nr.Values["fail_checks"] = string.Join("、", failedChecks);
            Log?.Invoke($"[测量] {Name}: 未过判定限: {nr.Values["fail_checks"]}");
        }
        return nr;
    }

    /// <summary>ERROR 结果（停线）：Error 文案给出可操作提示，值为 ERROR 语义（引用缺失/上游无输出）。</summary>
    protected static NodeResult ErrorResult(Dictionary<string, string> values, string message, Mat? baseImage = null)
    {
        var nr = new NodeResult { Decision = "ERROR", Error = message, OutputImage = baseImage };
        foreach (var kv in values) nr.Values[kv.Key] = kv.Value;
        nr.Values["error"] = message;
        return nr;
    }

    /// <summary>
    /// 标注底图：测量为纯几何，但透传流水线底图作为输出图——否则本节点无缩略图、选中不了也显示不了，
    /// 矢量标注（被测线/圆/距离段/交点）永远渲染不出来。底图为空则不输出（保持「底图为空不报错」语义）。
    /// 注意：标注坐标是上游几何的源图坐标，底图与上游源图一致（同一图像源直供）时才对齐。
    /// </summary>
    protected static Mat? BuildBaseImage(Mat bgr)
    {
        if (bgr.Empty())
        {
            return null;
        }

        if (bgr.Channels() == 3)
        {
            return bgr.Clone();
        }

        var mat = new Mat();
        Cv2.CvtColor(bgr, mat, bgr.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return mat;
    }

    // ===== 矢量标注助手（源图像素坐标，UI 屏幕常量渲染） =====
    /// <summary>线段标注。</summary>
    protected static NodeShape LineShape(double x1, double y1, double x2, double y2, string? label, NodeShapeKind kind) => new()
    {
        Polys = [new[] { new Point((int)Math.Round(x1), (int)Math.Round(y1)), new Point((int)Math.Round(x2), (int)Math.Round(y2)) }],
        Label = label,
        Kind = kind,
    };

    /// <summary>点集标注（十字/交点/垂足等，屏幕常量小实心点）。</summary>
    protected static NodeShape PointsShape(NodeShapeKind kind, params (double X, double Y)[] pts) => new()
    {
        Polys = [pts.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray()],
        AsPoints = true,
        Kind = kind,
    };

    /// <summary>圆标注（72 段折线，复用圆查找的多边形生成）。</summary>
    protected static NodeShape CircleShape(double cx, double cy, double r, string? label, NodeShapeKind kind) => new()
    {
        Polys = [CircleFindNode.CirclePoly(cx, cy, r)],
        Label = label,
        Kind = kind,
    };
}
