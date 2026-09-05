using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>真实模型集成测试：模型文件缺失时静默跳过（绿色测试不证明数值一致性）。</summary>
public class RealModelTests
{
    private static readonly string ModelDir =
        @"D:\AiProjects\speaker-inspection\patchcore train\models\foam_patchcore";

    private static bool ModelExists =>
        File.Exists(Path.Combine(ModelDir, "model.onnx"))
        && File.Exists(Path.Combine(ModelDir, "memory_bank.bin"))
        && File.Exists(Path.Combine(ModelDir, "backbone_config.json"));

    [Fact]
    public void PatchCoreRuntime_LoadsAndScores_RealModel()
    {
        if (!ModelExists) return; // 集成测试：模型缺失时跳过

        using var rt = new PatchCoreRuntime(ModelDir);
        Assert.NotNull(rt.Threshold);

        using var img = new Mat(300, 300, MatType.CV_8UC3, Scalar.All(128));
        var (score, patchMap, decision) = rt.Detect(img);
        Assert.True(score >= 0);
        Assert.NotNull(patchMap);
        Assert.Contains(decision, new[] { "OK", "NG" });
        Assert.True(patchMap.GetLength(0) > 0 && patchMap.GetLength(1) > 0);
    }

    [Fact]
    public void Pipeline_WithRealModel_RunsToDecision()
    {
        if (!ModelExists) return;

        var recipe = new Models.Recipe
        {
            Nodes =
            [
                new Models.RecipeNode
                {
                    Name = "01 PatchCore",
                    Type = "PatchCore",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["model_dir"] = ModelDir, ["threshold"] = "" },
                },
                new Models.RecipeNode
                {
                    Name = "02 条件检测",
                    Type = "Decision",
                    Enabled = true,
                    Rules =
                    [
                        new Models.DecisionRule
                        {
                            MatchMode = "all",
                            Conditions =
                            [
                                new Models.Condition { Node = "01 PatchCore", Field = "decision", Op = "=", Value = "OK" },
                            ],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
            ],
        };
        recipe.BaseDir = AppContext.BaseDirectory;

        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(300, 300, MatType.CV_8UC3, Scalar.All(128));
        var result = pipeline.Run(img, "real.bmp");
        Assert.Contains(result.Decision, new[] { "OK", "NG" });
        Assert.True(result.NodeValues.ContainsKey("01 PatchCore"));
    }

    private static readonly string SemanticModelDir =
        @"D:\AiProjects\speaker-inspection\u训练\outputs\_test\e2e_semantic_run\weights";

    private static bool SemanticModelExists =>
        File.Exists(Path.Combine(SemanticModelDir, "best.onnx"))
        && File.Exists(Path.Combine(SemanticModelDir, "classes.txt"));

    [Fact]
    public void SemanticSegRuntime_LoadsAndDecides_RealModel()
    {
        if (!SemanticModelExists) return; // 集成测试：模型缺失时跳过

        var recipe = new Models.Recipe
        {
            Nodes =
            [
                new Models.RecipeNode
                {
                    Name = "01 语义分割",
                    Type = "SemanticSeg",
                    Enabled = true,
                    Params = new Dictionary<string, string>
                    {
                        ["model_dir"] = SemanticModelDir,
                        ["decision_mode"] = SegNode.DecisionRatioNg,
                        ["own_rois"] = NodeRois.SerializeOwn(new List<(string, RoiRect)>
                        {
                            ("ROI", new RoiRect(0.5, 0.5, 0.5, 0.5, 0)),
                        }),
                    },
                },
                new Models.RecipeNode
                {
                    Name = "02 条件检测",
                    Type = "Decision",
                    Enabled = true,
                    Rules =
                    [
                        new Models.DecisionRule
                        {
                            MatchMode = "all",
                            Conditions =
                            [
                                new Models.Condition { Node = "01 语义分割", Field = "decision", Op = "=", Value = "OK" },
                            ],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
            ],
        };
        recipe.BaseDir = AppContext.BaseDirectory;

        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(90));
        var result = pipeline.Run(img, "semantic_real.bmp");
        Assert.Contains(result.Decision, new[] { "OK", "NG" });
        Assert.True(result.NodeValues.ContainsKey("01 语义分割"));
        var vals = result.NodeValues["01 语义分割"];
        Assert.True(vals.ContainsKey("max_ratio"));
        Assert.True(vals.ContainsKey("roi_ROI"));
        Assert.NotNull(result.DisplayImage);
    }
}
