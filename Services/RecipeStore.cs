using System.IO;
using System.Text.Json;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Services;

/// <summary>
/// 方案（Recipe）持久化：New/Open/Save/SaveAs + 自动加载上次方案。
/// 兼容旧 config.json（首次运行自动迁移）；DEPLOY_* 环境变量覆盖保留（产线脚本依赖）。
/// </summary>
public static class RecipeStore
{
    public const string DefaultRecipeName = "recipe.json";

    /// <summary>加载方案：优先 recipe.json，其次迁移旧 config.json。返回 null 表示都不可用。</summary>
    public static Recipe? Load(string baseDir, string? recipePath = null)
    {
        var dir = string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir;
        var path = string.IsNullOrEmpty(recipePath)
            ? Path.Combine(dir, DefaultRecipeName)
            : Path.GetFullPath(recipePath);

        if (File.Exists(path))
        {
            var recipe = LoadFile(path);
            recipe.BaseDir = Path.GetDirectoryName(path) ?? dir;
            ApplyEnvOverrides(recipe);
            return recipe;
        }

        var legacy = Path.Combine(dir, "config.json");
        if (File.Exists(legacy))
        {
            var migrated = MigrateFromLegacy(legacy);
            migrated.BaseDir = dir;
            ApplyEnvOverrides(migrated);
            TrySave(migrated, path);
            return migrated;
        }

        return null;
    }

    /// <summary>加载指定文件（不迁移、不 env 覆盖）。</summary>
    public static Recipe LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Recipe>(json, Recipe.JsonOptions)
            ?? throw new InvalidDataException($"方案文件解析失败: {path}");
    }

    /// <summary>保存到指定路径；BaseDir 随文件位置更新。</summary>
    public static void Save(Recipe recipe, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        var json = JsonSerializer.Serialize(recipe, Recipe.JsonOptions);
        File.WriteAllText(full, json, System.Text.Encoding.UTF8);
        recipe.BaseDir = Path.GetDirectoryName(full) ?? recipe.BaseDir;
    }

    /// <summary>保存为默认 recipe.json（自动加载用）。</summary>
    public static void SaveAsDefault(Recipe recipe, string baseDir) => Save(recipe, Path.Combine(baseDir, DefaultRecipeName));

    private static void TrySave(Recipe recipe, string path)
    {
        try
        {
            Save(recipe, path);
        }
        catch
        {
            // 迁移失败不阻断运行
        }
    }

    /// <summary>迁移旧 config.json → Recipe（单模型 → 单节点 + 默认判断规则）。</summary>
    public static Recipe MigrateFromLegacy(string legacyPath)
    {
        var json = File.ReadAllText(legacyPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var dir = Path.GetDirectoryName(legacyPath) ?? ".";

        string? Str(string k) => root.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        string Num(string k, string d) => root.TryGetProperty(k, out var e) ? e.ToString() : d;
        bool Bool(string k, bool d) => root.TryGetProperty(k, out var e) && e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(e.GetString(), out var b) && b,
            _ => d,
        };

        return new Recipe
        {
            Name = "泡棉检测",
            Nodes =
            [
                new RecipeNode
                {
                    Name = "01 PatchCore_表面",
                    Type = "PatchCore",
                    Enabled = true,
                    Params = new Dictionary<string, string>
                    {
                        ["model_dir"] = Str("model_dir") ?? "",
                        ["threshold"] = "",
                    },
                },
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
                            Conditions =
                            [
                                new Condition { Node = "01 PatchCore_表面", Field = "decision", Op = "=", Value = "OK" },
                            ],
                            Result = "OK",
                            ElseResult = "NG",
                        },
                    ],
                },
            ],
            Decision = new RecipeDecision
            {
                Rules =
                [
                    new DecisionRule
                    {
                        Conditions =
                        [
                            new Condition { Node = "01 PatchCore_表面", Field = "score", Op = ">", Value = "@threshold" },
                        ],
                        Result = "NG",
                    },
                ],
            },
            Io = new RecipeIo
            {
                InputDir = Str("input_dir") ?? @"D:\vision\vm_input",
                ResultDir = Str("result_dir") ?? @"D:\vision\vm_results",
                PollIntervalMs = int.TryParse(Num("poll_interval_ms", "500"), out var pi) ? pi : 500,
                MoveProcessed = Bool("move_processed", false),
                ProcessedDir = Str("processed_dir") ?? "",
                VmHost = Str("vm_host") ?? "127.0.0.1",
                VmPort = int.TryParse(Num("vm_port", "7930"), out var vp) ? vp : 7930,
                VmEnabled = Bool("vm_enabled", true),
                VmReconnectIntervalS = double.TryParse(Num("vm_reconnect_interval_s", "3.0"), out var ri) ? ri : 3.0,
                ResultFormat = Str("result_format") ?? "plain",
                Separator = Str("separator") ?? ",",
                VmResponseTerminator = Str("vm_response_terminator") ?? "",
            },
            BaseDir = dir,
        };
    }

    /// <summary>应用 DEPLOY_* 环境变量覆盖（产线脚本兼容）。MODEL_DIR 覆盖第一个 PatchCore 节点。</summary>
    public static void ApplyEnvOverrides(Recipe recipe)
    {
        var env = (string name) => Environment.GetEnvironmentVariable(name);

        var input = env("DEPLOY_INPUT_DIR");
        if (!string.IsNullOrWhiteSpace(input)) recipe.Io.InputDir = input;

        var result = env("DEPLOY_RESULT_DIR");
        if (!string.IsNullOrWhiteSpace(result)) recipe.Io.ResultDir = result;

        var host = env("DEPLOY_VM_HOST");
        if (!string.IsNullOrWhiteSpace(host)) recipe.Io.VmHost = host;

        var port = env("DEPLOY_VM_PORT");
        if (int.TryParse(port, out var vmPort)) recipe.Io.VmPort = vmPort;

        var model = env("DEPLOY_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(model))
        {
            var pc = recipe.Nodes.FirstOrDefault(n => n.Type == "PatchCore");
            if (pc != null) pc.Params["model_dir"] = model;
        }
    }

    /// <summary>把 Recipe 中的相对路径解析为绝对路径（基于 BaseDir）。</summary>
    public static string Resolve(Recipe recipe, string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return p ?? "";
        if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
        return string.IsNullOrEmpty(recipe.BaseDir) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(recipe.BaseDir, p));
    }
}
