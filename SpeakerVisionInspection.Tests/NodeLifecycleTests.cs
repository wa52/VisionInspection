using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using SpeakerVisionInspection.Services;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>
/// 检测节点生命周期测试（目标检测 YOLO / 实例分割 Seg 共用同一契约）：
/// NodeFactory 创建 → 参数热更新 → 重命名 → Recipe 文件往返 → 未加载运行快速失败 →
/// Dispose 幂等 → 重建为干净实例。不依赖真实模型文件（真机数值一致性由 RealModelTests 覆盖）。
/// </summary>
public class NodeLifecycleTests
{
    public static TheoryData<string> LifecycleTypes => new() { "YOLO", "Seg" };

    [Theory]
    [MemberData(nameof(LifecycleTypes))]
    public void FullLifecycle_Create_ParamUpdate_Rename_RoundTrip_RunGuard_Dispose(string type)
    {
        // 1. 创建：类型/注册契约
        var node = NodeFactory.Create(type, "01 目标检测");
        Assert.NotNull(node);
        Assert.Equal(type, node.Type);
        Assert.True(node.Enabled);
        Assert.Contains(node.ParamDefs, d => d.Key == "model_dir");

        try
        {
            // 2. 参数热更新（含检测项私有 ROI，重名允许）
            node.SetParam("source", "00 图像源");
            node.SetParam("own_rois", NodeRois.SerializeOwn(new List<(string, RoiRect)>
            {
                ("螺钉", new RoiRect(0.2, 0.3, 0.1, 0.1, 0)),
                ("螺钉", new RoiRect(0.7, 0.6, 0.1, 0.1, 15)),
            }));
            Assert.Equal("00 图像源", node.Params["source"]);

            // 3. 重命名 → 参数保持
            node.Name = "09 目标检测";
            Assert.Equal("09 目标检测", node.Name);
            Assert.Equal("00 图像源", node.Params["source"]);

            // 4. Recipe 文件往返：节点名/类型/参数（含重名检测项）一致
            var recipe = new Recipe { Name = "生命周期" };
            recipe.Nodes.Add(new RecipeNode
            {
                Name = node.Name,
                Type = node.Type,
                Enabled = true,
                Params = new Dictionary<string, string>(node.Params),
            });
            var path = Path.Combine(Path.GetTempPath(), $"lifecycle_{type}_{Guid.NewGuid():N}.json");
            try
            {
                RecipeStore.Save(recipe, path);
                var loaded = RecipeStore.LoadFile(path);
                var back = Assert.Single(loaded.Nodes);
                Assert.Equal("09 目标检测", back.Name);
                Assert.Equal(type, back.Type);
                Assert.Equal("00 图像源", back.Params.GetValueOrDefault("source"));
                var rois = NodeRois.ParseOwn(back.Params.GetValueOrDefault("own_rois"));
                Assert.Equal(2, rois.Count);
                Assert.Equal("螺钉", rois[0].Name);
                Assert.Equal("螺钉", rois[1].Name); // 重名往返无损
                Assert.Equal(15, rois[1].Rect.AngleDeg, 4);
            }
            finally
            {
                File.Delete(path);
            }

            // 5. 未加载模型就运行 → 快速失败（Pipeline 捕获后转节点 ERROR，不静默出 OK）
            using var img = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(80));
            var ctx = new PipelineRunContext(img);
            Assert.Throws<InvalidOperationException>(() => node.Run(img, ctx));
        }
        finally
        {
            // 6. Dispose 幂等（二次 Dispose 不抛）
            node.Dispose();
            node.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(LifecycleTypes))]
    public void Recreate_SameName_FreshParams(string type)
    {
        var first = NodeFactory.Create(type, "N");
        Assert.NotNull(first);
        first.SetParam("source", "旧节点");
        first.Dispose();

        // 重建同名节点：干净实例，参数不残留
        var second = NodeFactory.Create(type, "N");
        Assert.NotNull(second);
        Assert.NotEqual("旧节点", second.Params.GetValueOrDefault("source"));
        second.Dispose();
    }
}
