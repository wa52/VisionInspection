using System.Windows.Input;
using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>按键控制节点：键位解析、透传语义、工厂注册。</summary>
public class KeyControlNodeTests
{
    [Theory]
    [InlineData("空格", Key.Space)]
    [InlineData("Enter", Key.Enter)]
    [InlineData("F5", Key.F5)]
    [InlineData("F8", Key.F8)]
    [InlineData("F12", Key.F12)]
    public void ParseKey_KnownNames(string name, Key expected)
    {
        Assert.Equal(expected, KeyControlNode.ParseKey(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("未知键")]
    [InlineData("f8")] // 大小写敏感：只认面板选项原文
    public void ParseKey_Unknown_ReturnsNull(string? name)
    {
        Assert.Null(KeyControlNode.ParseKey(name));
    }

    [Fact]
    public void Run_Passthrough_OkWithTriggerKey()
    {
        var node = new KeyControlNode("按键控制", new Dictionary<string, string> { ["trigger_key"] = "F8" });
        using var input = new OpenCvSharp.Mat(1, 1, OpenCvSharp.MatType.CV_8UC3);
        var ctx = new PipelineRunContext(input);
        var result = node.Run(input, ctx);

        Assert.Equal("OK", result.Decision);
        Assert.Null(result.OutputImage); // 不产出图像（非图像处理节点）
        Assert.Equal("F8", result.Values["trigger_key"]);
        Assert.True(node.Enabled);
    }

    [Fact]
    public void NodeFactory_RegistersKeyControl()
    {
        using var node = NodeFactory.Create("KeyControl", "01 按键控制");
        Assert.IsType<KeyControlNode>(node);
        Assert.Equal("KeyControl", node.Type);
    }

    [Fact]
    public void ParamDefs_ExposeTriggerKeyChoice()
    {
        var def = Assert.Single(KeyControlNode.StaticParamDefs);
        Assert.Equal("trigger_key", def.Key);
        Assert.Equal("choice", def.Kind);
        Assert.Contains("空格", def.Choices);
    }
}
