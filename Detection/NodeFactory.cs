using System.Collections.Concurrent;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>模型节点工厂：按类型创建 IModelNode。扩展新模型 = 实现 IModelNode + Register。</summary>
public static class NodeFactory
{
    private static readonly ConcurrentDictionary<string, Func<string, Dictionary<string, string>?, IModelNode>> _registry = new();

    static NodeFactory()
    {
        Register("PatchCore", (name, init) => new PatchCoreNode(name, init));
        // 条件检测节点：Rules 由 Pipeline 从 RecipeNode.Rules 注入
        Register("Decision", (name, init) => new DecisionNode(name, log: null));
        // 输出图像节点
        Register("SaveImage", (name, init) => new SaveImageNode(name, init));
        // 图像显示节点（把上游输出图提供给 UI）
        Register("Display", (name, init) => new DisplayNode(name, init));
        // 图像源节点（相机/图像文件，作为流程图像源）
        Register("ImageSource", (name, init) => new ImageSourceNode(name, init));
        // 图像二值化节点（可调阈值/类型/Otsu）
        Register("Binarize", (name, init) => new BinarizeNode(name, init));
        // 几何变换节点（缩放/旋转/翻转）
        Register("Geometry", (name, init) => new GeometryNode(name, init));
        // 颜色变换节点（转灰度：加权平均/算术平均/单分量提取）
        Register("ColorTransform", (name, init) => new ColorTransformNode(name, init));
        // 相机 IO 通信节点（根据上游判定触发 Strobe 光耦输出）
        Register("CameraIo", (name, init) => new CameraIoNode(name, init));
        // 扩展点占位：类型可被「添加模块」菜单看到，运行时该节点记 ERROR 不崩溃（UnsupportedNode）。
        // 实现新模型 = 新增 XxxNode : IModelNode 并改这里的 Register 指向真实实现。
        // YOLO 目标检测节点（u训练 yolo11 标准导出 ONNX：best.onnx + classes.txt）
        Register("YOLO", (name, init) => new YoloNode(name, init));
        // 实例分割节点（u训练 yolo11-seg 标准导出 ONNX：best.onnx + classes.txt，output0 检测+掩码系数 + output1 proto）
        Register("Seg", (name, init) => new SegNode(name, init));
        // 轮廓匹配节点（基于边缘方向的形状模板匹配：shape_template.json 建模 + 金字塔由粗到细搜索）
        Register("ContourMatch", (name, init) => new ContourMatchNode(name, init));
        // 位置修正节点（读取定位节点 loc_* 契约位姿，驱动指定下游节点的 ROI 跟随工件）
        Register("PositionCorrection", (name, init) => new PositionCorrectionNode(name, init));
        Register("OCR", (name, init) => new UnsupportedNode(name, "OCR"));
    }

    public static void Register(string type, Func<string, Dictionary<string, string>?, IModelNode> factory)
        => _registry[type] = factory;

    public static bool IsRegistered(string type) => _registry.ContainsKey(type);

    public static IEnumerable<string> RegisteredTypes => _registry.Keys;

    /// <summary>创建节点；未知类型抛异常。旧方案里的 ImageLoad 自动映射为 ImageSource。</summary>
    public static IModelNode Create(string type, string name, Dictionary<string, string>? init = null)
    {
        if (type == "ImageLoad")
        {
            type = "ImageSource";
        }

        if (!_registry.TryGetValue(type, out var factory))
        {
            throw new NotSupportedException($"未注册的模型类型: {type}（可用: {string.Join(", ", _registry.Keys)}）");
        }
        return factory(name, init);
    }

    /// <summary>从 Recipe 节点定义构建运行时节点。</summary>
    public static IModelNode FromRecipeNode(RecipeNode rn)
    {
        var node = Create(rn.Type, rn.Name, rn.Params);
        node.Enabled = rn.Enabled;
        return node;
    }
}
