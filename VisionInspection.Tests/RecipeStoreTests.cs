using VisionInspection.Detection;
using VisionInspection.Models;
using VisionInspection.Services;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>方案持久化功能测试：保存 → 加载往返，参数（含模型路径）不丢失。</summary>
public class RecipeStoreTests : IDisposable
{
    private readonly string _dir;

    public RecipeStoreTests()
    {
        // 环境变量覆盖会改写 Load 结果；本类测试一律在无覆盖状态下运行（覆盖行为单独测试）
        Environment.SetEnvironmentVariable("DEPLOY_MODEL_DIR", null);
        _dir = Path.Combine(Path.GetTempPath(), $"recipe_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEPLOY_MODEL_DIR", null);
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响测试 */ }
    }

    [Fact]
    public void CreateDefault_IsBlankRecipe()
    {
        var recipe = Recipe.CreateDefault();

        Assert.Equal("新建方案", recipe.Name);
        Assert.Empty(recipe.Nodes);
    }

    [Fact]
    public void SaveAsDefault_Load_RoundTrip_PreservesParams()
    {
        var imageDir = Path.Combine(Path.GetTempPath(), "VisionInspection", "some-image-dir");
        var modelDir = Path.Combine(Path.GetTempPath(), "VisionInspection", "production-model");
        var recipe = new Recipe
        {
            Name = "功能测试方案",
            BaseDir = _dir,
            Nodes =
            [
                new RecipeNode
                {
                    Name = "01 图像源",
                    Type = "ImageSource",
                    Enabled = true,
                    Params = new Dictionary<string, string> { ["dir"] = imageDir },
                },
                new RecipeNode
                {
                    Name = "02 PatchCore 检测",
                    Type = "PatchCore",
                    Enabled = true,
                    Params = new Dictionary<string, string>
                    {
                        ["model_dir"] = modelDir,
                        ["source"] = "01 图像源",
                        ["roi"] = "0.5492,0.51,0.4098,0.52,0",
                        ["threshold"] = "",
                    },
                },
            ],
        };

        RecipeStore.SaveAsDefault(recipe, _dir);
        Assert.True(File.Exists(Path.Combine(_dir, "recipe.json")));

        var loaded = RecipeStore.Load(_dir);
        Assert.NotNull(loaded);
        var pc = loaded!.Nodes.FirstOrDefault(n => n.Type == "PatchCore");
        Assert.NotNull(pc);
        Assert.Equal(modelDir, pc!.Params["model_dir"]);
        Assert.Equal("0.5492,0.51,0.4098,0.52,0", pc.Params["roi"]);
        Assert.Equal(imageDir, loaded.Nodes[0].Params["dir"]);
    }

    [Fact]
    public void Load_MissingRecipe_ReturnsNull()
    {
        Assert.Null(RecipeStore.Load(_dir));
    }

    [Fact]
    public void Load_EnvOverride_WinsOverSavedModelDir()
    {
        // 产线脚本兼容：DEPLOY_MODEL_DIR 设置时覆盖第一个 PatchCore 节点的模型路径（保存值失效）
        var overrideDir = Path.Combine(Path.GetTempPath(), "VisionInspection", "override-model");
        var savedDir = Path.Combine(Path.GetTempPath(), "VisionInspection", "saved-model");
        Environment.SetEnvironmentVariable("DEPLOY_MODEL_DIR", overrideDir);
        try
        {
            var recipe = new Recipe
            {
                Name = "覆盖测试",
                BaseDir = _dir,
                Nodes =
                [
                    new RecipeNode
                    {
                        Name = "01 PatchCore 检测",
                        Type = "PatchCore",
                        Enabled = true,
                        Params = new Dictionary<string, string> { ["model_dir"] = savedDir },
                    },
                ],
            };
            RecipeStore.SaveAsDefault(recipe, _dir);

            var loaded = RecipeStore.Load(_dir);
            Assert.NotNull(loaded);
            Assert.Equal(overrideDir, loaded!.Nodes[0].Params["model_dir"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEPLOY_MODEL_DIR", null);
        }
    }

    [Fact]
    public void SavedRecipe_PipelineBuild_LoadsResolvedModelDir()
    {
        // 模型不存在 → EnsureLoaded 抛错被 Pipeline 捕获记日志，节点保留 → 不崩且节点名可查
        var recipe = new Recipe
        {
            Name = "构建测试",
            BaseDir = _dir,
            Nodes =
            [
                new RecipeNode
                {
                    Name = "01 图像源",
                    Type = "ImageSource",
                    Enabled = true,
                    Params = new Dictionary<string, string>(),
                },
                new RecipeNode
                {
                    Name = "02 PatchCore 检测",
                    Type = "PatchCore",
                    Enabled = true,
                    Params = new Dictionary<string, string>
                    {
                        ["model_dir"] = Path.Combine(_dir, "不存在模型"),
                        ["source"] = "01 图像源",
                    },
                },
            ],
        };
        RecipeStore.SaveAsDefault(recipe, _dir);

        var logs = new List<string>();
        using var pipeline = new Pipeline(RecipeStore.Load(_dir)!, logs.Add);
        // 模型目录不存在：加载失败记校验日志而非抛异常，流水线可构建
        Assert.Contains(pipeline.Nodes, n => n.Type == "PatchCore");
        Assert.Contains(logs, l => l.Contains("模型加载失败"));
    }
}
