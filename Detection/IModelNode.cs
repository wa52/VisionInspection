using OpenCvSharp;
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>节点标注形状类别：UI 矢量叠加层按类别着色（Defect=红、Ok=绿、Info=黄）。</summary>
public enum NodeShapeKind
{
    Defect,
    Ok,
    Info,
}

/// <summary>
/// 节点标注形状（源图像素坐标）。由 UI 以矢量叠加层渲染：线宽/字号是屏幕常量，
/// 不随图像分辨率与显示缩放变化——不要把框线/文字画进位图（缩放后会变粗/发糊）。
/// 位图只保留像素级内容（掩码染色、热图）。
/// </summary>
public sealed class NodeShape
{
    /// <summary>矩形框（设置后忽略 Polys）。</summary>
    public Rect? Box { get; init; }

    /// <summary>多边形（缺陷轮廓/旋转四边形），Box 未设置时渲染。</summary>
    public IReadOnlyList<Point[]>? Polys { get; init; }

    /// <summary>点集渲染：Polys 每个坐标画成屏幕常量小实心点（与线宽/字号同为屏幕常量，不随图像分辨率与显示缩放变化）。</summary>
    public bool AsPoints { get; init; }

    /// <summary>文字标注（矩形上缘/多边形首点处，可空）。</summary>
    public string? Label { get; init; }

    public NodeShapeKind Kind { get; init; } = NodeShapeKind.Info;
}

/// <summary>单节点检测结果：值字典（供判断模块引用）+ 判定 + 可视化。</summary>
public sealed class NodeResult
{
    /// <summary>输出值，如 {"score": 4.22} / {"count": 3} / {"text": "OK"}。判断模块通过 node.field 引用。</summary>
    public Dictionary<string, string> Values { get; } = new();

    /// <summary>节点自判：OK | NG | ERROR（最终判定由判断模块决定，此处仅参考）。</summary>
    public string Decision { get; set; } = "OK";

    /// <summary>异常信息（ERROR 时非空）。</summary>
    public string? Error { get; set; }

    /// <summary>PatchCore 热图等可视化（可空）。</summary>
    public float[,]? HeatMap { get; set; }

    /// <summary>节点输出图像（供 SaveImage 等下游节点选用；为空则下游不可选）。</summary>
    public Mat? OutputImage { get; set; }

    /// <summary>矢量标注形状（框/多边形/文字，源图像素坐标）：UI 屏幕常量渲染，不进位图。</summary>
    public List<NodeShape> Annotations { get; } = new();

    public double? Threshold { get; set; }
}

/// <summary>流水线运行上下文：传递原图 + 已跑节点结果/输出图 + 当前判定。节点间图像传递的通道。</summary>
public sealed class PipelineRunContext
{
    public Mat Input { get; }
    /// <summary>已跑节点名 → 结果（含 Decision 节点输出）。</summary>
    public Dictionary<string, NodeResult> Results { get; } = new();
    /// <summary>已跑节点名 → 输出图（仅 OutputImage 非空的节点）。</summary>
    public Dictionary<string, Mat> Images { get; } = new();
    /// <summary>最近一个条件检测节点的结果（可为 null）。</summary>
    public NodeResult? DecisionResult { get; set; }
    /// <summary>相机 IO 输出执行器（相机未连接时为 null → CameraIo 节点 ERROR 停线）。生产模式下由 CameraInspectionService 注入。</summary>
    public Action<IoCommunicationSettings>? CameraIoOutput { get; }

    /// <summary>
    /// 位置修正（由 PositionCorrectionNode 写入）：下游节点解析完自己的 ROI 后应用此变换，
    /// 使检测区域跟随工件平移+旋转。定位来源节点须输出 loc_x/loc_y/loc_angle/loc_valid 标准契约键。
    /// </summary>
    public PoseCorrection? PoseCorrection { get; set; }

    public PipelineRunContext(
        Mat input,
        Action<IoCommunicationSettings>? cameraIoOutput = null)
    {
        Input = input;
        CameraIoOutput = cameraIoOutput;
    }

    public bool EmitCameraIo(IoCommunicationSettings settings)
    {
        if (CameraIoOutput is null)
        {
            return false;
        }

        CameraIoOutput(settings);
        return true;
    }
    /// <summary>当前判定：有决策节点用它；否则按已跑节点有无 NG 粗判。</summary>
    public string CurrentDecision => DecisionResult?.Decision ?? (HasNg ? "NG" : "OK");

    /// <summary>已跑启用节点中是否任一自判 NG。</summary>
    public bool HasNg => Results.Values.Any(r => r.Decision == "NG");
}

/// <summary>
/// 位置修正位姿描述：基准位姿（画 ROI 时的定位位姿 P0）→ 当前定位位姿（P1）。
/// ROI 点变换：p' = R(Δθ)·S·(p − P0) + P1，Δθ = CurAngle − RefAngle（度，图像坐标系 y 向下），
/// S = diag(ScaleX, ScaleY)（海康"X/Y 方向尺度"，默认 1=纯刚体）。
/// TargetNodes = 要应用修正的节点名列表（由参数面板以引用方式勾选）。
/// </summary>
public sealed record PoseCorrection(
    string SourceNode,
    IReadOnlyList<string> TargetNodes,
    double RefX, double RefY, double RefAngle,
    double CurX, double CurY, double CurAngle,
    double ScaleX = 1.0, double ScaleY = 1.0);

/// <summary>模型节点统一接口。实现类由 NodeFactory 注册。</summary>
public interface IModelNode : IDisposable
{
    string Name { get; set; }
    string Type { get; }
    bool Enabled { get; set; }
    /// <summary>参数定义，驱动通用 Inspector。</summary>
    IReadOnlyList<ParamDef> ParamDefs { get; }
    /// <summary>当前参数值。</summary>
    IReadOnlyDictionary<string, string> Params { get; }
    /// <summary>设置参数（非法值抛异常或记录）。</summary>
    void SetParam(string key, string value);
    /// <summary>运行节点。ctx 提供原图、上游节点结果/输出图、当前判定。</summary>
    NodeResult Run(Mat bgr, PipelineRunContext ctx);
}
