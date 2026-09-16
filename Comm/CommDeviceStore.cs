using System.IO;
using System.Text.Json;

namespace VisionInspection.Comm;

/// <summary>通信设备列表持久化（comm.json，与 camera.json 同目录）。</summary>
public sealed class CommDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;

    public CommDeviceStore(string path)
    {
        _path = path;
    }

    public List<CommDevice> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<CommDevice>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Services.AppLog.Warn($"读取通信设备配置失败: {_path}", ex);
            return [];
        }
    }

    public void Save(IReadOnlyList<CommDevice> devices)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(devices, JsonOptions));
        }
        catch (Exception ex)
        {
            Services.AppLog.Error($"保存通信设备配置失败: {_path}", ex);
        }
    }
}
