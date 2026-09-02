using System.IO;
using System.Text.Json;

namespace SpeakerVisionInspection.Services;

public static class VisionMasterConfigResolver
{
    public const string VisionMasterRootEnv = "VISIONMASTER_SDK_ROOT";
    public const string MvdSdkRootEnv = "MVDALGO_DEV_ENV";
    public const string MvsRuntimeEnv = "MVS_RUNTIME_DIR";
    public const string MvsSdkDevRootEnv = "MVS_SDK_DEV_ROOT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static VisionMasterConfig ResolveFromProcessEnvironment(string? configFilePath = null)
    {
        return Resolve(new Dictionary<string, string?>
        {
            [VisionMasterRootEnv] = Environment.GetEnvironmentVariable(VisionMasterRootEnv),
            [MvdSdkRootEnv] = Environment.GetEnvironmentVariable(MvdSdkRootEnv),
            [MvsRuntimeEnv] = Environment.GetEnvironmentVariable(MvsRuntimeEnv),
            [MvsSdkDevRootEnv] = Environment.GetEnvironmentVariable(MvsSdkDevRootEnv),
        }, configFilePath);
    }

    public static VisionMasterConfig Resolve(IReadOnlyDictionary<string, string?> env, string? configFilePath = null)
    {
        var file = ReadConfigFile(configFilePath);

        var visionMasterRoot = FileOrEnv(file?.VisionMasterRoot, env.GetValueOrDefault(VisionMasterRootEnv));
        var mvdSdkRoot = FileOrEnv(file?.SdkRoot, env.GetValueOrDefault(MvdSdkRootEnv));
        var mvsRuntimeDir = FileOrEnv(file?.MvsRuntimeDir, env.GetValueOrDefault(MvsRuntimeEnv));
        var mvsSdkDevRoot = FileOrEnv(file?.SdkDevRoot, env.GetValueOrDefault(MvsSdkDevRootEnv));
        var saveImageDir = file?.SaveImageDir;

        // 由任一已知根推导另一个：SDK 根 = VisionMaster 根\MVDAlgorithmSDK，反之取父目录
        if (mvdSdkRoot is null && visionMasterRoot is not null)
        {
            mvdSdkRoot = Path.Combine(visionMasterRoot, "MVDAlgorithmSDK");
        }
        else if (visionMasterRoot is null && mvdSdkRoot is not null)
        {
            visionMasterRoot = Path.GetDirectoryName(mvdSdkRoot.TrimEnd(Path.DirectorySeparatorChar));
        }

        return new VisionMasterConfig
        {
            VisionMasterRoot = visionMasterRoot,
            MvdSdkRoot = mvdSdkRoot,
            MvsRuntimeDir = mvsRuntimeDir,
            MvsSdkDevRoot = mvsSdkDevRoot,
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
        public string? VisionMasterRoot { get; set; }
        public string? SdkRoot { get; set; }
        public string? MvsRuntimeDir { get; set; }
        public string? SdkDevRoot { get; set; }
        public string? SaveImageDir { get; set; }
    }
}
