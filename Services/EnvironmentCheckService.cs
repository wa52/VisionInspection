using System.IO;

namespace VisionInspection.Services;

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

        // 相机控制托管程序集仅使用随程序发布的 MVS 封装，不回退到 VisionMaster 旧封装。
        var bundled = Path.Combine(bundledDir, "MvCameraControl.Net.dll");
        var cameraManaged = File.Exists(bundled) ? bundled : null;
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
