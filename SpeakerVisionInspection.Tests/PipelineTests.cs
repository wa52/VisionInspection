using OpenCvSharp;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

public class FakeNode : IModelNode
{
    private readonly Func<Mat, NodeResult> _run;
    public FakeNode(string name, Func<Mat, NodeResult> run)
    {
        Name = name;
        _run = run;
    }
    public string Name { get; set; }
    public string Type => "Fake";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => Array.Empty<ParamDef>();
    public IReadOnlyDictionary<string, string> Params { get; } = new Dictionary<string, string>();
    public void SetParam(string key, string value) { }
    public NodeResult Run(Mat bgr, PipelineRunContext ctx) => _run(bgr);
    public void Dispose() { }
}

public class PipelineTests
{
    [Fact]
    public void Run_SourceOverride_SkipsImageSourceAndFeedsDownstream()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ovr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir); // 空目录：图像源自身取不到图
        try
        {
            var recipe = new Recipe
            {
                BaseDir = dir,
                Nodes =
                [
                    new RecipeNode { Name = "01 图像源", Type = "ImageSource", Enabled = true, Params = new Dictionary<string, string> { ["dir"] = dir } },
                    new RecipeNode { Name = "02 二值化", Type = "Binarize", Enabled = true, Params = new Dictionary<string, string> { ["source"] = "01 图像源", ["threshold"] = "128" } },
                ],
            };
            using var pipeline = new Pipeline(recipe);

            using var img = new Mat(64, 80, MatType.CV_8UC3, Scalar.All(200));
            var result = pipeline.Run(img, "test", sourceOverrides: new Dictionary<string, Mat> { ["01 图像源"] = img });

            // 图像源节点被跳过（无自身输出值），下游二值化处理的是注入图（宽 80）
            Assert.False(result.NodeValues.ContainsKey("01 图像源"));
            Assert.True(result.NodeValues.TryGetValue("02 二值化", out var vals));
            Assert.Equal("80", vals["out_w"]);
            Assert.Equal("64", vals["out_h"]);
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败不影响测试 */ }
        }
    }

    [Fact]
    public void DecisionRunsAfterAllModels_AllOk_ReturnsOk()
    {
        NodeFactory.Register("FakeOk", (n, _) => new FakeNode(n, _ => new NodeResult { Decision = "OK", Values = { ["decision"] = "OK" } }));
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "02 M1", Type = "FakeOk", Enabled = true },
                new RecipeNode { Name = "03 M2", Type = "FakeOk", Enabled = true },
                new RecipeNode
                {
                    Name = "04 条件检测",
                    Type = "Decision",
                    Enabled = true,
                    Rules =
                    [
                        new DecisionRule
                        {
                            MatchMode = "all",
                            Conditions =
                            [
                                new Condition { Node = "02 M1", Field = "decision", Op = "=", Value = "OK" },
                                new Condition { Node = "03 M2", Field = "decision", Op = "=", Value = "OK" },
                            ],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));
        var result = pipeline.Run(img, "t.bmp");
        Assert.Equal("OK", result.Decision);
    }

    [Fact]
    public void DecisionRunsAfterAllModels_AnyNg_ReturnsNg()
    {
        NodeFactory.Register("FakeOk", (n, _) => new FakeNode(n, _ => new NodeResult { Decision = "OK", Values = { ["decision"] = "OK" } }));
        NodeFactory.Register("FakeNg", (n, _) => new FakeNode(n, _ => new NodeResult { Decision = "NG", Values = { ["decision"] = "NG" } }));
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "02 M1", Type = "FakeOk", Enabled = true },
                new RecipeNode { Name = "03 M2", Type = "FakeNg", Enabled = true },
                new RecipeNode
                {
                    Name = "04 条件检测",
                    Type = "Decision",
                    Enabled = true,
                    Rules =
                    [
                        new DecisionRule
                        {
                            MatchMode = "all",
                            Conditions =
                            [
                                new Condition { Node = "02 M1", Field = "decision", Op = "=", Value = "OK" },
                                new Condition { Node = "03 M2", Field = "decision", Op = "=", Value = "OK" },
                            ],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));
        var result = pipeline.Run(img, "t.bmp");
        Assert.Equal("NG", result.Decision);
    }

    [Fact]
    public void RunNodeInputs_RunsModelNodesInParallel()
    {
        NodeFactory.Register("FakeSlow", (n, _) => new FakeNode(n, _ =>
        {
            Thread.Sleep(300);
            return new NodeResult { Decision = "OK", Values = { ["decision"] = "OK" } };
        }));
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "02 M1", Type = "FakeSlow", Enabled = true },
                new RecipeNode { Name = "03 M2", Type = "FakeSlow", Enabled = true },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));
        var inputs = new Dictionary<string, Mat> { ["02 M1"] = img, ["03 M2"] = img };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = pipeline.RunNodeInputs(inputs, "t.bmp");
        sw.Stop();
        Assert.Equal("OK", result.Decision);
        // 并行两个300ms < 500ms；串行则约600ms
        Assert.True(sw.ElapsedMilliseconds < 500, $"并行未生效，实测 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void ApplyNodeParam_ForwardsToNode_AndUnknownReturnsFalse()
    {
        var received = new List<(string Key, string Value)>();
        NodeFactory.Register("FakeSetParam", (n, _) =>
        {
            var fake = new FakeNode(n, _ => new NodeResult { Decision = "OK", Values = { ["decision"] = "OK" } });
            return new SetParamTrackingFake(fake, received);
        });
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "02 M1", Type = "FakeSetParam", Enabled = true },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        Assert.True(pipeline.ApplyNodeParam("02 M1", "threshold", "3.14"));
        Assert.Contains(received, r => r.Key == "threshold" && r.Value == "3.14");
        Assert.False(pipeline.ApplyNodeParam("不存在的节点", "threshold", "1"));
    }

    private sealed class SetParamTrackingFake : IModelNode
    {
        private readonly IModelNode _inner;
        private readonly List<(string, string)> _received;
        public SetParamTrackingFake(IModelNode inner, List<(string, string)> received)
        {
            _inner = inner;
            _received = received;
        }
        public string Name { get => _inner.Name; set => _inner.Name = value; }
        public string Type => _inner.Type;
        public bool Enabled { get => _inner.Enabled; set => _inner.Enabled = value; }
        public IReadOnlyList<ParamDef> ParamDefs => _inner.ParamDefs;
        public IReadOnlyDictionary<string, string> Params => _inner.Params;
        public void SetParam(string key, string value) { _received.Add((key, value)); _inner.SetParam(key, value); }
        public NodeResult Run(Mat bgr, PipelineRunContext ctx) => _inner.Run(bgr, ctx);
        public void Dispose() => _inner.Dispose();
    }
}
