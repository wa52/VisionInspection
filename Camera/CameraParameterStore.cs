using System.IO;
using System.Text.Json;

namespace VisionInspection.Camera;

/// <summary>相机采集参数持久化（JSON 文件）。</summary>
public sealed class CameraParameterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;

    public CameraParameterStore(string path)
    {
        _path = path;
    }

    public CameraParameters Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new CameraParameters();
            }

            return JsonSerializer.Deserialize<CameraParameters>(File.ReadAllText(_path), JsonOptions) ?? new CameraParameters();
        }
        catch (Exception ex)
        {
            Services.AppLog.Warn($"读取相机参数配置失败: {_path}", ex);
            return new CameraParameters();
        }
    }

    public void Save(CameraParameters parameters)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(parameters, JsonOptions));
        }
        catch (Exception ex)
        {
            Services.AppLog.Error($"保存相机参数配置失败: {_path}", ex);
        }
    }

    /// <summary>触发配置文件路径（与相机参数同目录，独立文件）。</summary>
    public string TriggerPath => Path.Combine(Path.GetDirectoryName(_path) ?? ".", "trigger.json");

    /// <summary>IO 通信配置文件路径（与相机参数同目录，独立文件）。</summary>
    public string IoCommunicationPath => Path.Combine(Path.GetDirectoryName(_path) ?? ".", "io_communication.json");

    public TriggerSettings LoadTriggerSettings()
    {
        var triggerPath = TriggerPath;
        try
        {
            if (!File.Exists(triggerPath))
            {
                return new TriggerSettings();
            }

            return JsonSerializer.Deserialize<TriggerSettings>(File.ReadAllText(triggerPath), JsonOptions) ?? new TriggerSettings();
        }
        catch (Exception ex)
        {
            Services.AppLog.Warn($"读取触发配置失败: {triggerPath}", ex);
            return new TriggerSettings();
        }
    }

    public void SaveTriggerSettings(TriggerSettings settings)
    {
        var triggerPath = TriggerPath;
        try
        {
            var dir = Path.GetDirectoryName(triggerPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(triggerPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Services.AppLog.Error($"保存触发配置失败: {triggerPath}", ex);
        }
    }

    public IoCommunicationSettings LoadIoCommunicationSettings()
    {
        var ioPath = IoCommunicationPath;
        try
        {
            if (!File.Exists(ioPath))
            {
                return new IoCommunicationSettings();
            }

            return JsonSerializer.Deserialize<IoCommunicationSettings>(File.ReadAllText(ioPath), JsonOptions) ?? new IoCommunicationSettings();
        }
        catch (Exception ex)
        {
            Services.AppLog.Warn($"读取 IO 通信配置失败: {ioPath}", ex);
            return new IoCommunicationSettings();
        }
    }

    public void SaveIoCommunicationSettings(IoCommunicationSettings settings)
    {
        var ioPath = IoCommunicationPath;
        try
        {
            var dir = Path.GetDirectoryName(ioPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(ioPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Services.AppLog.Error($"保存 IO 通信配置失败: {ioPath}", ex);
        }
    }
}
