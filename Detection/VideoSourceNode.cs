using System.Globalization;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 独立视频源：按原始帧序解码 MP4/AVI 等文件；每次 Run 产出一张真实视频帧。
/// 通过 frame_step 控制抽帧，缺省不落盘；EOS 由 PipelineRunner 正常停止。
/// 不负责检测、追踪或比赛事件。
/// </summary>
public sealed class VideoSourceNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new() { Key = "path", Label = "视频文件", Kind = "path", Default = "" },
        new() { Key = "start_frame", Label = "起始帧（从0开始）", Kind = "int", Default = "0" },
        new() { Key = "frame_step", Label = "每隔N帧取一帧", Kind = "int", Default = "1" },
        new() { Key = "save_dir", Label = "抽帧保存目录（可选）", Kind = "folder", Default = "" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["path"] = "", ["start_frame"] = "0", ["frame_step"] = "1", ["save_dir"] = "",
    };

    private VideoCapture? _capture;
    private string _baseDir = Directory.GetCurrentDirectory();
    private string _file = "";
    private long _nextIndex;
    private double _fps;
    private bool _endOfStream;
    private bool _endLogged;

    public VideoSourceNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
            foreach (var (key, value) in init) _params[key] = value;
    }

    public string Name { get; set; }
    public string Type => "VideoSource";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;
    public Action<string>? Log { get; set; }
    public string ResolvedPath => _file;

    public void SetParam(string key, string value)
    {
        if (key is "start_frame" or "frame_step")
            ValidatePositiveInt(key, value);
        _params[key] = value;
        if (key is "path" or "start_frame" or "frame_step")
            CloseCapture(); // 下一次运行从新起始帧开始，绝不沿用旧视频缓冲。
    }

    private static int ValidatePositiveInt(string key, string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < (key == "start_frame" ? 0 : 1))
            throw new InvalidDataException(key == "start_frame"
                ? "起始帧必须为非负整数。" : "抽帧间隔必须为大于0的整数。");
        return value;
    }

    public void EnsureOpened(string baseDir)
    {
        _baseDir = string.IsNullOrWhiteSpace(baseDir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(baseDir);
        if (_capture != null) return;
        var path = _params.GetValueOrDefault("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请在视频源节点选择视频文件。");

        var start = ValidatePositiveInt("start_frame", _params.GetValueOrDefault("start_frame", "0"));
        ValidatePositiveInt("frame_step", _params.GetValueOrDefault("frame_step", "1"));

        var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_baseDir, path));
        if (!File.Exists(resolved)) throw new FileNotFoundException("视频文件不存在。", resolved);
        var capture = new VideoCapture(resolved);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            throw new InvalidDataException("无法解码视频文件，请检查格式和 OpenCV 视频编解码支持。");
        }

        try
        {
            if (start > 0 && !capture.Set(VideoCaptureProperties.PosFrames, start))
                throw new InvalidDataException("视频不支持按帧定位起始位置。");
            _capture = capture;
            _file = resolved;
            _fps = capture.Get(VideoCaptureProperties.Fps);
            if (!double.IsFinite(_fps) || _fps <= 0) _fps = 0;
            _nextIndex = start;
            _endOfStream = false;
            _endLogged = false;
            Log?.Invoke($"[视频源] {Name}: 打开 {Path.GetFileName(_file)} · FPS={_fps:F3} · 起始帧={start}");
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        EnsureOpened(_baseDir);
        if (_endOfStream) return EndOfStream();
        var capture = _capture!;
        using var frame = new Mat();
        var index = _nextIndex;
        if (!capture.Read(frame) || frame.Empty())
        {
            _endOfStream = true;
            return EndOfStream();
        }
        _nextIndex++;
        var timestampMs = capture.Get(VideoCaptureProperties.PosMsec);
        var actualTimestamp = double.IsFinite(timestampMs) && timestampMs > 0;
        if (!actualTimestamp && _fps > 0) timestampMs = index * 1000.0 / _fps;
        if (!double.IsFinite(timestampMs) || timestampMs < 0) timestampMs = -1;

        var saveDir = _params.GetValueOrDefault("save_dir")?.Trim();
        if (!string.IsNullOrEmpty(saveDir))
        {
            var directory = Path.GetFullPath(Path.IsPathRooted(saveDir)
                ? saveDir : Path.Combine(_baseDir, saveDir));
            Directory.CreateDirectory(directory);
            var filename = $"{Path.GetFileNameWithoutExtension(_file)}_frame_{index:D8}.png";
            if (!Cv2.ImWrite(Path.Combine(directory, filename), frame))
                throw new IOException("视频抽帧图片保存失败。");
        }

        var step = ValidatePositiveInt("frame_step", _params.GetValueOrDefault("frame_step", "1"));
        for (var i = 1; i < step; i++)
        {
            if (!capture.Grab())
            {
                _endOfStream = true;
                break;
            }
            _nextIndex++;
        }

        var result = new NodeResult { Decision = "OK", OutputImage = frame.Clone() };
        result.Values["eof"] = "false";
        result.Values["frame_index"] = index.ToString(CultureInfo.InvariantCulture);
        result.Values["timestamp_ms"] = timestampMs.ToString("0.###", CultureInfo.InvariantCulture);
        result.Values["timestamp_source"] = actualTimestamp ? "container" : _fps > 0 ? "fps_estimate" : "unknown";
        result.Values["fps"] = _fps.ToString("0.###", CultureInfo.InvariantCulture);
        result.Values["out_w"] = frame.Width.ToString(CultureInfo.InvariantCulture);
        result.Values["out_h"] = frame.Height.ToString(CultureInfo.InvariantCulture);
        result.Values["current_file"] = $"{Path.GetFileName(_file)}#{index:D8}";
        return result;
    }

    private NodeResult EndOfStream()
    {
        if (!_endLogged)
        {
            Log?.Invoke($"[视频源] {Name}: 文件结束；不循环播放，不重复最后一帧。");
            _endLogged = true;
        }
        var result = new NodeResult { Decision = "OK" };
        result.Values["eof"] = "true";
        result.Values["frame_index"] = _nextIndex.ToString(CultureInfo.InvariantCulture);
        return result;
    }

    private void CloseCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _file = "";
        _fps = 0;
        _nextIndex = 0;
        _endOfStream = false;
        _endLogged = false;
    }

    public void Dispose() => CloseCapture();
}
