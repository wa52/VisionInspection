using OpenCvSharp;
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

public sealed class CameraIoNodeTests
{
    [Fact]
    public void Run_WhenDecisionIsNg_EmitsCameraIoPulse()
    {
        NodeFactory.Register("FakeNgForIo", (name, _) => new FakeNode(name, _ => new NodeResult { Decision = "NG", Values = { ["decision"] = "NG" } }));
        var emitted = new List<IoCommunicationSettings>();
        using var pipeline = new Pipeline(CreateRecipe("FakeNgForIo"));
        using var img = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(1));

        var result = pipeline.Run(
            img,
            "t.bmp",
            cameraIoSettings: new IoCommunicationSettings { NgOutputLine = "Line2", PulseMs = 500 },
            cameraIoOutput: settings => emitted.Add(settings));

        Assert.Equal("NG", result.Decision);
        Assert.Single(emitted);
        Assert.Equal("Line2", emitted[0].NgOutputLine);
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
            cameraIoSettings: new IoCommunicationSettings { NgOutputLine = "Line2", PulseMs = 500 },
            cameraIoOutput: settings => emitted.Add(settings));

        Assert.Equal("OK", result.Decision);
        Assert.Empty(emitted);
        Assert.Equal("0", result.NodeValues["03 相机IO通信"]["emitted"]);
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
                    ["duration_ms"] = "123",
                },
            },
        ],
    };
}
