using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 快速匹配节点（粗定位、节拍优先）：继承轮廓匹配——同一模板契约（shape_template.json，可与轮廓匹配共用建模）
/// 与同一输出契约（x/y/angle/score + loc_x/loc_y/loc_angle/loc_valid，可直接接位置修正）。
/// 区别只在搜索内核：ShapeMatcher 快速模式 = 搜索金字塔向粗层多探层（模板层数不够时坐标减半合成，仅小半径模板放行）
/// + 每角度预旋转模板点 + 粗层步长 4 + 候选漏斗（16→8）+ 点集抽稀 ≤600 + 单轮抛物线精修。
/// 采样激进度随模板半径自适应（角度量化位移 ≈ 半径×sin(半步长)）：小模板全程加速，大模板自动退回保守采样。
/// 实测：泡棉真实场景 642×1071 ROI ~20-25ms（完整模式 ~300ms）；重复精度 ~0.2px，绝对精度 ≤1px 级。
/// 参数与轮廓匹配一致（含 max_workers 并行线程数，0=自动取一半核）；精度要求更高时改用/追加轮廓匹配节点。
/// </summary>
public sealed class FastMatchNode : ContourMatchNode
{
    public FastMatchNode(string name, Dictionary<string, string>? init = null) : base(name, init)
    {
    }

    public override string Type => "FastMatch";

    protected override List<ShapeMatchInstance> FindInRegion(
        OpenCvSharp.Mat gray, double minScore, double angleStart, double angleExtent, int numMatches,
        double minContrast, double maxOverlap, double sigma, bool usePolarity)
    {
        // ≤0 或非法值 = 自动（ShapeMatcher 内部取 ProcessorCount/2）
        var maxWorkers = (int)Math.Round(ParseDouble(_params.GetValueOrDefault("max_workers"), 0));
        var matcher = new ShapeMatcher(_template!, minScore, angleStart, angleExtent, numMatches, maxOverlap,
            minContrast, sigma, usePolarity, fastMode: true, maxWorkers: maxWorkers);
        return matcher.Find(gray);
    }
}
