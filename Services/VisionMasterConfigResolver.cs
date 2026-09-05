using System.IO;
using System.Text.Json;

namespace SpeakerVisionInspection.Services;

public static class VisionMasterConfigResolver
{
    public const string MvsRuntimeEnv = "MVS_RUNTIME_DIR";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static VisionMasterConfig ResolveFromProcessEnvironment(string? configFilePath = null)
    {
        return Resolve(new Dictionary<string, string?>
        {
            [MvsRuntimeEnv] = Environment.GetEnvironmentVariable(MvsRuntimeEnv),
        }, configFilePath);
    }

    public static VisionMasterConfig Resolve(IReadOnlyDictionary<string, string?> env, string? configFilePath = null)
    {
        var file = ReadConfigFile(configFilePath);

        var mvsRuntimeDir = FileOrEnv(file?.MvsRuntimeDir, env.GetValueOrDefault(MvsRuntimeEnv));
        var saveImageDir = file?.SaveImageDir;

        return new VisionMasterConfig
        {
            MvsRuntimeDir = mvsRuntimeDir,
            SaveImageDir = saveImageDir,
        };
    }

    private static string? FileOrEnv(string? fileValue, string? envValue) => fileValue ?? envValue;

    private static ConfigFile? ReadConfigFile(string? configFilePath)
    {
        if (configFilePath is null || !File.Exists(configFilePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(configFilePath), JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取配置文件失败: {configFilePath}", ex);
            return null;
        }
    }

    private sealed class ConfigFile
    {
        public string? MvsRuntimeDir { get; set; }
        public string? SaveImageDir { get; set; }
    }
}
