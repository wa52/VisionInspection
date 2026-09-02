using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>Pipeline 构建期"输入输出对不上"校验：只记日志不阻断。</summary>
public class PipelineValidationTests
{
    private static RecipeNode Binarize(string name, string source) => new()
    {
        Name = name,
        Type = "Binarize",
        Enabled = true,
        Params = new Dictionary<string, string> { ["source"] = source, ["threshold"] = "128" },
    };

    private static List<string> BuildAndCollectLogs(Recipe recipe, out Pipeline pipeline)
    {
        var logs = new List<string>();
        pipeline = new Pipeline(recipe, logs.Add);
        return logs;
    }

    [Fact]
    public void Pipeline_MissingSourceNode_LogsNotExist()
    {
        var recipe = new Recipe { Name = "t", Nodes = { Binarize("01 二值化", "不存在") } };
        var logs = BuildAndCollectLogs(recipe, out var pipeline);
        try
        {
            Assert.Contains(logs, l => l.Contains("[校验]") && l.Contains("01 二值化") && l.Contains("不存在"));
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public void Pipeline_SourceIsDownstream_LogsOrderError()
    {
        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                Binarize("01 二值化", "02 二值化"),
                Binarize("02 二值化", "@input"),
            },
        };
        var logs = BuildAndCollectLogs(recipe, out var pipeline);
        try
        {
            Assert.Contains(logs, l => l.Contains("[校验]") && l.Contains("01 二值化") && l.Contains("顺序错误"));
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public void Pipeline_UpstreamSource_DoesNotLogValidation()
    {
        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                new RecipeNode { Name = "01 检测", Type = "PatchCore", Enabled = false },
                Binarize("02 二值化", "01 检测"),
            },
        };
        var logs = BuildAndCollectLogs(recipe, out var pipeline);
        try
        {
            Assert.DoesNotContain(logs, l => l.Contains("[校验]"));
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public void SaveImage_MissingSource_LogsAtRun()
    {
        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                new RecipeNode
                {
                    Name = "01 保存",
                    Type = "SaveImage",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["source"] = "没有这个节点" },
                },
            },
        };
        var logs = new List<string>();
        using var pipeline = new Pipeline(recipe, logs.Add);
        using var bgr = new OpenCvSharp.Mat(4, 4, OpenCvSharp.MatType.CV_8UC3);
        pipeline.Run(bgr, "test");
        Assert.Contains(logs, l => l.Contains("[SaveImage]") && l.Contains("图像来源未找到"));
    }

    [Fact]
    public void Run_AutoDisplay_LastNodeImageExposed()
    {
        var recipe = new Recipe
        {
            Name = "t",
            Nodes =
            {
                Binarize("01 二值化", "@input"),
            },
        };
        using var pipeline = new Pipeline(recipe, _ => { });
        using var bgr = new OpenCvSharp.Mat(4, 4, OpenCvSharp.MatType.CV_8UC3);
        var result = pipeline.Run(bgr, "test");
        try
        {
            Assert.NotNull(result.DisplayImage);
            Assert.Equal(4, result.DisplayImage!.Width);
            Assert.Equal("OK", result.Decision);
        }
        finally
        {
            foreach (var m in result.NodeImages.Values) m.Dispose();
        }
    }
}
