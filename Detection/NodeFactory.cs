using System.Collections.Concurrent;
using VisionInspection.Models;

namespace VisionInspection.Detection;

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
        // 叠加显示节点：显式选择上游节点，汇总 ROI/检测标注/判定摘要
        Register("OverlayDisplay", (name, init) => new OverlayDisplayNode(name, init));
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
        // 语义分割节点（u训练 task=semantic yolo26*-sem 导出 ONNX：best.onnx + classes.txt，[1,H,W] 像素类别索引图）
        Register("SemanticSeg", (name, init) => new SemanticSegNode(name, init));
        // 轮廓匹配节点（基于边缘方向的形状模板匹配：shape_template.json 建模 + 金字塔由粗到细搜索）
        Register("ContourMatch", (name, init) => new ContourMatchNode(name, init));
        // 快速匹配节点（粗定位、节拍优先：同一模板契约，搜索内核点集抽稀+单轮精修，速度 3~5 倍）
        Register("FastMatch", (name, init) => new FastMatchNode(name, init));
        // 位置修正节点（读取定位节点 loc_* 契约位姿，驱动指定下游节点的 ROI 跟随工件）
        Register("PositionCorrection", (name, init) => new PositionCorrectionNode(name, init));
        // 按键控制节点（绑定常用键位：程序空闲时按下 = 执行一次完整检测流程）
        Register("KeyControl", (name, init) => new KeyControlNode(name, init));
        // 字符识别节点（传统视觉路线：二值化/形态学预处理 → 连通域字符分割 → 字模库模板匹配）
        Register("CharRec", (name, init) => new CharRecNode(name, init));
        // Blob 分析节点（传统视觉路线：二值化 → 孔洞填充 → 连通域斑点 → 特征筛选/排序 → 检测项级判定）
        Register("Blob", (name, init) => new BlobNode(name, init));
        // 发送数据节点（VM 式：模板文本 {节点名.键名}/{decision} 经通信管理设备发出）
        Register("SendData", (name, init) => new SendDataNode(name, init));
        // 接收数据节点（VM 式：从通信管理设备取一条文本，超时可 ERROR 停线或按 OK 继续）
        Register("ReceiveData", (name, init) => new ReceiveDataNode(name, init));
        // 直线查找节点（VM 卡尺找线式：卡尺法线找亚像素边缘点 → 最小二乘拟合直线，输出 loc_* 定位契约）
        Register("LineFind", (name, init) => new LineFindNode(name, init));
        // 圆查找节点（VM 卡尺找圆式：卡尺径向找亚像素边缘点 → Kasa 最小二乘拟合圆，输出 loc_* 定位契约）
        Register("CircleFind", (name, init) => new CircleFindNode(name, init));
        // 几何测量四节点（VM 测量组订阅模式：线线/线圆/圆圆/点圆，纯几何计算 + 判定限内建）
        Register("LineLineMeasure", (name, init) => new LineLineMeasureNode(name, init));
        Register("LineCircleMeasure", (name, init) => new LineCircleMeasureNode(name, init));
        Register("CircleCircleMeasure", (name, init) => new CircleCircleMeasureNode(name, init));
        Register("PointCircleMeasure", (name, init) => new PointCircleMeasureNode(name, init));
    }

    public static void Register(string type, Func<string, Dictionary<string, string>?, IModelNode> factory)
        => _registry[type] = factory;

    public static bool IsRegistered(string type) => _registry.ContainsKey(type);

    public static IEnumerable<string> RegisteredTypes => _registry.Keys;

    /// <summary>创建节点；未知类型抛异常。旧类型映射：ImageLoad→ImageSource，OCR（占位）→CharRec（字符识别）。</summary>
    public static IModelNode Create(string type, string name, Dictionary<string, string>? init = null)
    {
        if (type == "ImageLoad")
        {
            type = "ImageSource";
        }
        else if (type == "OCR")
        {
            // 旧方案的 OCR 占位节点自动迁移为字符识别（未配字模库时同样 ERROR 停线，语义不回退）
            type = "CharRec";
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
