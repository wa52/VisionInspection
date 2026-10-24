using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using VisionInspection.Models;

namespace VisionInspection.Detection;

/// <summary>
/// 多目标跟踪 V1：按类 IoU 数据关联，保留 Track ID 和临时丢失状态。
/// 输入必须是上游 YOLO 的 detections_json，不重新推理，也不依赖 HALCON。
/// 专用于建立可复现的跟踪基线；不是 ByteTrack / ReID。
/// </summary>
public sealed class MultiObjectTrackingNode : IModelNode
{
    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new() { Key = "source", Label = "上游目标检测节点", Kind = "nodesource", Default = "" },
        new() { Key = "min_iou", Label = "目标关联 IoU 阈值", Kind = "double", Default = "0.15" },
        new() { Key = "min_conf", Label = "最低检测置信度", Kind = "double", Default = "0.25" },
        new() { Key = "max_missed", Label = "最大允许丢失帧数", Kind = "int", Default = "10" },
    ];

    private sealed class Track
    {
        public int Id { get; init; }
        public YoloDetection Detection { get; set; }
        public int Missed { get; set; }
        public int Hits { get; set; } = 1;
    }

    private readonly List<Track> _tracks = new();
    private readonly Dictionary<string, string> _params = new()
    {
        ["source"] = "", ["min_iou"] = "0.15", ["min_conf"] = "0.25", ["max_missed"] = "10",
    };
    private int _nextId = 1;
    private long _lastFrameIndex = -1;
    private string _streamId = "";
    public Action<string>? Log { get; set; }

    public MultiObjectTrackingNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
            foreach (var (key, value) in init) _params[key] = value;
    }

    public string Name { get; set; }
    public string Type => "MultiObjectTracking";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    public void SetParam(string key, string value)
    {
        if (key is "min_iou" or "min_conf") ReadFraction(key, value);
        if (key == "max_missed") ReadMissed(value);
        if (key == "source" && value != _params.GetValueOrDefault("source")) Reset();
        _params[key] = value;
    }

    private static double ReadFraction(string name, string? raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value > 1)
            throw new InvalidDataException(name + " 应为0到1之间的小数。");
        return value;
    }

    private static int ReadMissed(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 1000)
            throw new InvalidDataException("最大丢失帧数必须是0～1000的整数。");
        return n;
    }

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var source = _params.GetValueOrDefault("source")?.Trim();
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("请选择上游 YOLO 检测节点。");
        if (!ctx.Results.TryGetValue(source, out var result) ||
            !result.Values.TryGetValue("detections_json", out var json))
            throw new InvalidDataException("未收到上游 YOLO 检测框，请检查节点顺序和来源。");
        if (!ctx.Images.TryGetValue(source, out var image) || image.Empty())
            throw new InvalidDataException("上游 YOLO 输出图像不可用。");

        // 单视频重复播放/重新打开后，旧 Track ID 不跨比赛复用。
        foreach (var (name, upstream) in ctx.Results)
        {
            if (!upstream.Values.TryGetValue("frame_index", out var rawFrame) ||
                !upstream.Values.ContainsKey("eof") ||
                !long.TryParse(rawFrame, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameIndex))
                continue;
            var currentFile = upstream.Values.GetValueOrDefault("current_file") ?? "";
            var marker = currentFile.Split('#')[0];
            var stream = name + ":" + marker;
            if (_streamId.Length > 0 && (_streamId != stream || frameIndex <= _lastFrameIndex))
                Reset();
            _streamId = stream;
            _lastFrameIndex = frameIndex;
            break;
        }

        var minIou = ReadFraction("min_iou", _params.GetValueOrDefault("min_iou", "0.15"));
        var minConfidence = ReadFraction("min_conf", _params.GetValueOrDefault("min_conf", "0.25"));
        var maxMissed = ReadMissed(_params.GetValueOrDefault("max_missed", "10"));
        List<YoloDetection> all;
        try { all = JsonSerializer.Deserialize<List<YoloDetection>>(json) ?? []; }
        catch (JsonException ex) { throw new InvalidDataException("上游检测框格式不兼容。", ex); }
        if (all.Count > 1000) throw new InvalidDataException("单帧候选超过1000个，请检查模型输出。");
        if (all.Any(d => d.ClassIndex < 0 || !float.IsFinite(d.Conf) ||
            !double.IsFinite(d.Cx) || !double.IsFinite(d.Cy) || !double.IsFinite(d.W) ||
            !double.IsFinite(d.H) || d.W <= 0 || d.H <= 0))
            throw new InvalidDataException("上游目标框包含无效坐标或类别。");

        var detections = all.Where(d => d.Conf >= minConfidence && d.Conf <= 1).ToArray();
        var candidates = new List<(double Iou, int Track, int Detection)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            for (var d = 0; d < detections.Length; d++)
            {
                if (_tracks[t].Detection.ClassIndex != detections[d].ClassIndex) continue;
                var a = _tracks[t].Detection;
                var b = detections[d];
                var iou = YoloPostprocess.Iou(a.Cx, a.Cy, a.W, a.H, b.Cx, b.Cy, b.W, b.H);
                if (iou >= minIou && iou > 0)
                    candidates.Add((iou, t, d));
            }
        }

        var matchedTracks = new HashSet<int>();
        var matchedDetections = new HashSet<int>();
        var visible = new List<Track>();
        foreach (var pair in candidates.OrderByDescending(c => c.Iou)
                     .ThenBy(c => _tracks[c.Track].Id).ThenBy(c => c.Detection))
        {
            if (!matchedTracks.Add(pair.Track)) continue;
            if (!matchedDetections.Add(pair.Detection))
            {
                matchedTracks.Remove(pair.Track);
                continue;
            }
            var track = _tracks[pair.Track];
            track.Detection = detections[pair.Detection];
            track.Missed = 0;
            track.Hits++;
            visible.Add(track);
        }

        for (var i = 0; i < _tracks.Count; i++)
            if (!matchedTracks.Contains(i)) _tracks[i].Missed++;
        _tracks.RemoveAll(t => t.Missed > maxMissed);

        for (var i = 0; i < detections.Length; i++)
        {
            if (matchedDetections.Contains(i)) continue;
            var newTrack = new Track { Id = _nextId++, Detection = detections[i] };
            _tracks.Add(newTrack);
            visible.Add(newTrack);
        }

        var output = new NodeResult { Decision = "OK", OutputImage = image.Clone() };
        foreach (var t in visible.OrderBy(t => t.Id))
        {
            var d = t.Detection;
            output.Annotations.Add(new NodeShape
            {
                Box = new Rect(
                    (int)Math.Round(d.Cx - d.W / 2), (int)Math.Round(d.Cy - d.H / 2),
                    Math.Max(1, (int)Math.Round(d.W)), Math.Max(1, (int)Math.Round(d.H))),
                Label = $"#{t.Id} {d.Class}",
                Kind = NodeShapeKind.Info,
            });
        }
        output.Values["track_count"] = visible.Count.ToString(CultureInfo.InvariantCulture);
        output.Values["track_ids"] = string.Join(",", visible.OrderBy(t => t.Id).Select(t => t.Id));
        output.Values["tracks_json"] = JsonSerializer.Serialize(
            visible.OrderBy(t => t.Id).Select(t => new
            {
                track_id = t.Id, class_id = t.Detection.ClassIndex, class_name = t.Detection.Class,
                confidence = t.Detection.Conf, cx = t.Detection.Cx, cy = t.Detection.Cy,
                width = t.Detection.W, height = t.Detection.H, hits = t.Hits
            }));
        Log?.Invoke($"[多目标跟踪] {Name}: 本帧 {visible.Count} 个可见目标，维护 {_tracks.Count} 条轨迹");
        return output;
    }

    private void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
        _streamId = "";
        _lastFrameIndex = -1;
    }

    public void Dispose() => Reset();
}
