using System.IO;
using OpenCvSharp;
using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>整图留存门槛（save_mode = all/ok/ng）语义 + SaveImageNode 落盘行为；YOLO/Seg 节点复用同一门槛。</summary>
public class SaveWholeImageTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "svi_save_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Theory]
    [InlineData("all", "OK", true)]
    [InlineData("all", "NG", true)]
    [InlineData("ok", "OK", true)]
    [InlineData("ok", "NG", false)]
    [InlineData("ng", "OK", false)]
    [InlineData("ng", "NG", true)]
    [InlineData(null, "NG", true)]
    [InlineData("", "NG", true)]
    [InlineData("垃圾值", "NG", true)]
    public void ShouldSave_TruthTable(string? mode, string decision, bool expected)
        => Assert.Equal(expected, SaveImageNode.ShouldSave(mode, decision));

    [Fact]
    public void SaveImageNode_ModeOk_SavesOnlyOkDecision()
    {
        var node = new SaveImageNode("存图", new Dictionary<string, string> { ["save_mode"] = "ok", ["dir"] = _tempDir });
        using var img = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(64));

        var ctxOk = new PipelineRunContext(img) { DecisionResult = new NodeResult { Decision = "OK" } };
        var okResult = node.Run(img, ctxOk);
        Assert.Equal("1", okResult.Values["saved"]);
        var okDir = Path.Combine(_tempDir, "OK");
        Assert.True(Directory.Exists(okDir));
        Assert.Single(Directory.GetFiles(okDir, "*.jpg"));

        var ctxNg = new PipelineRunContext(img) { DecisionResult = new NodeResult { Decision = "NG" } };
        Assert.Equal("0", node.Run(img, ctxNg).Values["saved"]);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "NG")));
    }

    [Fact]
    public void SaveImageNode_ModeNg_SavesNgDecisionOnly()
    {
        var node = new SaveImageNode("存图", new Dictionary<string, string> { ["save_mode"] = "ng", ["dir"] = _tempDir });
        using var img = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(64));

        var ctxOk = new PipelineRunContext(img) { DecisionResult = new NodeResult { Decision = "OK" } };
        Assert.Equal("0", node.Run(img, ctxOk).Values["saved"]);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "OK")));

        var ctxNg = new PipelineRunContext(img) { DecisionResult = new NodeResult { Decision = "NG" } };
        Assert.Equal("1", node.Run(img, ctxNg).Values["saved"]);
        Assert.Single(Directory.GetFiles(Path.Combine(_tempDir, "NG"), "*.jpg"));
    }

    [Fact]
    public void SaveImageNode_ModeNone_DoesNotSave()
    {
        using var img = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(64));
        using var node = new SaveImageNode("存图", new Dictionary<string, string>
        {
            ["save_mode"] = "不保存",
            ["dir"] = _tempDir,
        });

        var ctx = new PipelineRunContext(img) { DecisionResult = new NodeResult { Decision = "NG" } };
        var result = node.Run(img, ctx);

        Assert.Equal("0", result.Values["saved"]);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "OK")));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "NG")));
    }
}
