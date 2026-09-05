using OpenCvSharp;
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

public sealed class CameraIoNodeTests
{
    [Fact]
    public void Run_WhenDecisionIsNg_EmitsCameraIoPulse_FromNodeParams()
    {
        NodeFactory.Register("FakeNgForIo", (name, _) => new FakeNode(name, _ => new NodeResult { Decision = "NG", Values = { ["decision"] = "NG" } }));
        var emitted = new List<IoCommunicationSettings>();
        using var pipeline = new Pipeline(CreateRecipe("FakeNgForIo"));
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));

        var result = pipeline.Run(
            img,
            "t.bmp",
            cameraIoOutput: settings => emitted.Add(settings));

        Assert.Equal("NG", result.Decision);
        Assert.Single(emitted);
        Assert.Equal("Line2", emitted[0].NgOutputLine);
        Assert.Equal("FrameEndActive", emitted[0].StrobeSource);
        Assert.Equal("High", emitted[0].ActiveLevel);
        Assert.True(emitted[0].Enabled);
        Assert.Equal("NgOnly", emitted[0].OutputMode);
        Assert.Equal(123, emitted[0].PulseMs);
        Assert.Equal("1", result.NodeValues["03 相机IO通信"]["emitted"]);
    }

    [Fact]
    public void Run_WhenDecisionIsOk_DoesNotEmitCameraIoPulse()
    {
        NodeFactory.Register("FakeOkForIo", (name, _) => new FakeNode(name, _ => new NodeResult { Decision = "OK", Values = { ["decision"] = "OK" } }));
        var emitted = new List<IoCommunicationSettings>();
        using var pipeline = new Pipeline(CreateRecipe("FakeOkForIo"));
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));

        var result = pipeline.Run(
            img,
            "t.bmp",
            cameraIoOutput: settings => emitted.Add(settings));

        Assert.Equal("OK", result.Decision);
        Assert.Empty(emitted);
        Assert.Equal("0", result.NodeValues["03 相机IO通信"]["emitted"]);
    }

    [Fact]
    public void Run_WithoutCameraIoOutput_ErrorsInsteadOfSilentSkip()
    {
        // 相机未连接（执行器为 null）时，命中输出条件的 CameraIo 节点必须自判 ERROR（节点值含 error），不允许静默放行
        NodeFactory.Register("FakeNgForIoNoExec", (name, _) => new FakeNode(name, _ => new NodeResult { Decision = "NG", Values = { ["decision"] = "NG" } }));
        using var pipeline = new Pipeline(CreateRecipe("FakeNgForIoNoExec"));
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));

        var result = pipeline.Run(img, "t.bmp");

        var nodeValues = result.NodeValues["03 相机IO通信"];
        Assert.Equal("ERROR", nodeValues["decision"]);
        Assert.Equal("0", nodeValues["emitted"]);
        Assert.False(string.IsNullOrWhiteSpace(nodeValues["error"]));
    }

    [Fact]
    public void BuildOutputSettings_AppliesDefaults_ForLegacyRecipe()
    {
        // 旧配方 CameraIo 节点只有 source/output_when/duration_ms：缺省输出配置回退出厂默认（与原共享配置默认一致）
        var settings = CameraIoNode.BuildOutputSettings(new Dictionary<string, string>
        {
            ["source"] = "03 条件检测",
            ["output_when"] = "NG",
            ["duration_ms"] = "500",
        });

        Assert.Equal("Line1", settings.NgOutputLine);
        Assert.Equal("FrameTriggerWait", settings.StrobeSource);
        Assert.Equal("Low", settings.ActiveLevel);
        Assert.Equal(500, settings.PulseMs);
        Assert.True(settings.Enabled);
        Assert.Equal("NgOnly", settings.OutputMode);
    }

    private static Recipe CreateRecipe(string fakeType) => new()
    {
        Nodes =
        [
            new RecipeNode { Name = "01 检测", Type = fakeType, Enabled = true },
            new RecipeNode
            {
                Name = "02 条件检测",
                Type = "Decision",
                Enabled = true,
                Rules =
                [
                    new DecisionRule
                    {
                        MatchMode = "all",
                        Conditions = [new Condition { Node = "01 检测", Field = "decision", Op = "=", Value = "OK" }],
                        Result = "OK",
                        ElseResult = "NG",
                    },
                ],
            },
            new RecipeNode
            {
                Name = "03 相机IO通信",
                Type = "CameraIo",
                Enabled = true,
                Params = new Dictionary<string, string>
                {
                    ["source"] = "02 条件检测",
                    ["output_when"] = "NG",
                    ["output_line"] = "Line2",
                    ["strobe_source"] = "FrameEndActive",
                    ["active_level"] = "高电平",
                    ["duration_ms"] = "123",
                },
            },
        ],
    };
}
