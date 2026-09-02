using System.IO;

namespace SpeakerVisionInspection.Services;

public sealed record EnvironmentCheckItem(string Name, bool Ok, string Detail);

public sealed record EnvironmentCheckResult(bool IsReady, IReadOnlyList<EnvironmentCheckItem> Items)
{
    public static EnvironmentCheckResult Create(IEnumerable<EnvironmentCheckItem> items)
    {
        var list = items.ToList();
        return new EnvironmentCheckResult(list.All(i => i.Ok), list);
    }
}

public static class EnvironmentCheckService
{
    public static EnvironmentCheckResult Check(VisionMasterConfig config, bool is64BitProcess)
        => Check(config, is64BitProcess, AppContext.BaseDirectory);

    /// <summary>bundledDir：应用目录（含 MvCameraControl.Net.dll 与 sdk\native\Win64_x64 的回退根），可注入以便测试。</summary>
    public static EnvironmentCheckResult Check(VisionMasterConfig config, bool is64BitProcess, string bundledDir)
    {
        var items = new List<EnvironmentCheckItem>();

        // 进程架构
        items.Add(is64BitProcess
            ? new EnvironmentCheckItem("进程架构 (x64)", true, "当前为 x64")
            : new EnvironmentCheckItem("进程架构 (x64)", false, "当前进程非 x64，需以 x64 运行"));

        // VisionMaster 根目录
        if (config.VisionMasterRoot is null)
        {
            items.Add(new EnvironmentCheckItem("VisionMaster 根目录", false, "未配置（VISIONMASTER_SDK_ROOT 或配置文件）"));
        }
        else
        {
            items.Add(CheckDirectory("VisionMaster 根目录", config.VisionMasterRoot));
        }

        // MVDAlgorithmSDK 根目录
        if (config.MvdSdkRoot is null)
        {
            items.Add(new EnvironmentCheckItem("MVDAlgorithmSDK 根目录", false, "未配置（MVDALGO_DEV_ENV 或配置文件）"));
        }
        else
        {
            items.Add(CheckDirectory("MVDAlgorithmSDK 根目录", config.MvdSdkRoot));
        }

        // MVS SDK Development 根目录（新版托管封装）
        if (config.MvsSdkDevRoot is null)
        {
            items.Add(new EnvironmentCheckItem("MVS SDK Development 根目录", false, "未配置（MVS_SDK_DEV_ROOT 或配置文件）"));
        }
        else
        {
            items.Add(CheckDirectory("MVS SDK Development 根目录", config.MvsSdkDevRoot));
        }

        // VisionMaster 服务 (C/S 架构依赖)
        var serverAppExe = config.VisionMasterRoot is null
            ? null
            : Path.Combine(config.VisionMasterRoot, "Applications", "ServerApp", "vServerApp.exe");
        items.Add(CheckFile("VisionMaster 服务 (vServerApp.exe)", serverAppExe));

        // 相机控制托管程序集：优先新版封装（x64，支持 GigE 枚举），回退 VisionMaster 旧封装；
        // 再回退应用目录内置封装（随构建复制，免安装包无需本机 MVS SDK 安装）
        var cameraManaged = config.MvsSdkDevRoot is null
            ? null
            : Path.Combine(config.MvsSdkDevRoot, "Development", "DotNet", "win64", "netstandard2.0", "MvCameraControl.Net.dll");
        if (cameraManaged is null || !File.Exists(cameraManaged))
        {
            cameraManaged = config.MvdSdkRoot is null
                ? null
                : Path.Combine(config.MvdSdkRoot, "ReferencedAssemblies", "Algorithms", "MvCameraControl.Net.dll");
        }
        if (cameraManaged is null || !File.Exists(cameraManaged))
        {
            var bundled = Path.Combine(bundledDir, "MvCameraControl.Net.dll");
            cameraManaged = File.Exists(bundled) ? bundled : null;
        }
        items.Add(CheckFile("相机控制托管程序集 (MvCameraControl.Net.dll)", cameraManaged));

        // 相机原生运行时：优先配置/环境变量指定的 MVS 运行时目录；
        // 未配置时回退到应用目录内置 native 运行时（相对路径，免安装包无需本机 MVS 安装）
        var nativeRuntimeDir = config.MvsRuntimeDir ?? Path.Combine(bundledDir, "sdk", "native", "Win64_x64");
        items.Add(CheckFile("相机原生运行时 (MvCameraControl.dll)", Path.Combine(nativeRuntimeDir, "MvCameraControl.dll")));

        return EnvironmentCheckResult.Create(items);
    }

    private static EnvironmentCheckItem CheckFile(string name, string? path)
    {
        if (path is null)
        {
            return new EnvironmentCheckItem(name, false, "未配置路径");
        }

        return File.Exists(path)
            ? new EnvironmentCheckItem(name, true, path)
            : new EnvironmentCheckItem(name, false, $"文件不存在: {path}");
    }

    private static EnvironmentCheckItem CheckDirectory(string name, string path)
    {
        return Directory.Exists(path)
            ? new EnvironmentCheckItem(name, true, path)
            : new EnvironmentCheckItem(name, false, $"目录不存在: {path}");
    }
}
