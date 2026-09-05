using System.IO;
using OpenCvSharp;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 图像源节点：流程的图像来源，支持两种模式（source_kind）。
/// - 图像文件：dir 非空走序列模式（按文件名排序，每次运行取下一张，loop=true 循环；false 停在最后一张）；
///   否则走单图模式（path，每次运行读同一张）。中文路径用字节流 ImDecode 兜底。
/// - 相机：输入帧非空（硬触发生产）直接透传；输入为空（单次/连续执行）通过 FrameProvider 同步抓一帧；
///   取不到帧（未连接/硬触发阻断/超时/异常）→ 节点 ERROR 停线（无图不能判定，不静默漏输出）。
/// 不参与判定（取到图恒 OK）；文件模式取不到图输出空并记日志。
/// </summary>
public sealed class ImageSourceNode : IModelNode
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public static readonly IReadOnlyList<ParamDef> StaticParamDefs =
    [
        new ParamDef { Key = "source_kind", Label = "来源类型", Kind = "choice", Default = "图像文件", Choices = ["图像文件", "相机"] },
        new ParamDef { Key = "dir", Label = "图片目录(文件模式·序列)", Kind = "folder", Default = "" },
        new ParamDef { Key = "path", Label = "单图文件(文件模式·目录为空时)", Kind = "path", Default = "" },
        new ParamDef { Key = "loop", Label = "循环读取(文件模式)", Kind = "bool", Default = "true" },
        new ParamDef { Key = "cam_wait_ms", Label = "取帧超时ms(相机模式)", Kind = "string", Default = "3000" },
    ];

    private readonly Dictionary<string, string> _params = new()
    {
        ["source_kind"] = "图像文件",
        ["dir"] = "",
        ["path"] = "",
        ["loop"] = "true",
        ["cam_wait_ms"] = "3000",
    };

    /// <summary>失配/运行日志输出（由 Pipeline 注入 UI 日志）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// 相机取帧接缝（相机模式且输入为空时调用）：timeoutMs → BGR Mat（可阻塞至超时，null=取不到）。
    /// 由 MainWindow 注入（包 CameraFrameGrabber）；返回的 Mat 归节点所有。
    /// </summary>
    public Func<int, Mat?>? FrameProvider { get; set; }

    private readonly List<string> _sequence = new();
    private bool _sequenceScanned;
    private int _cursor;

    public ImageSourceNode(string name, Dictionary<string, string>? init = null)
    {
        Name = name;
        if (init != null)
        {
            foreach (var (k, v) in init) _params[k] = v;
        }
    }

    public string Name { get; set; }
    public string Type => "ImageSource";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<ParamDef> ParamDefs => StaticParamDefs;
    public IReadOnlyDictionary<string, string> Params => _params;

    public void SetParam(string key, string value)
    {
        _params[key] = value;
        // 目录变更后需要重新扫描序列
        if (key == "dir")
        {
            _sequenceScanned = false;
            _sequence.Clear();
            _cursor = 0;
        }
    }

    public NodeResult Run(Mat bgr, PipelineRunContext ctx)
    {
        var kind = _params.GetValueOrDefault("source_kind") ?? "图像文件";
        return string.Equals(kind, "相机", StringComparison.Ordinal) ? RunCamera(bgr) : RunFile();
    }

    /// <summary>
    /// 相机模式：输入帧非空 → 透传（生产/已有真实帧）；为空 → FrameProvider 同步抓一帧。
    /// 取不到帧（未连接/硬触发阻断/超时/异常）→ 节点 ERROR 停线，不静默漏输出（对齐相机IO节点语义）。
    /// </summary>
    private NodeResult RunCamera(Mat bgr)
    {
        if (bgr is not null && !bgr.Empty())
        {
            return ImageResult(bgr, "CAMERA");
        }

        var provider = FrameProvider;
        if (provider == null)
        {
            return Fail("相机未连接：请打开「相机管理」连接相机后再执行");
        }

        var waitMs = int.TryParse(_params.GetValueOrDefault("cam_wait_ms"), out var w) && w > 0 ? w : 3000;
        Mat? img;
        try
        {
            img = provider(waitMs);
        }
        catch (Exception ex)
        {
            return Fail($"相机取帧失败: {ex.Message}");
        }

        if (img == null || img.Empty())
        {
            img?.Dispose();
            return Fail($"相机取帧失败（详见日志）：相机可能未连接、处于硬触发模式或取帧超时（{waitMs}ms）。"
                + "硬触发模式下单次/连续执行不抓帧（防干扰生产采集）——请在「相机管理」把触发模式改为连续/软触发，或把图像源切回图像文件");
        }

        return ImageResult(img, "CAMERA");
    }

    /// <summary>相机取帧失败 → 节点 ERROR（结果表格/模块结果可见，整线停线）。</summary>
    private NodeResult Fail(string error)
    {
        Log?.Invoke($"[ImageSource] {Name}: {error}");
        return new NodeResult { Decision = "ERROR", Error = error };
    }

    private NodeResult RunFile()
    {
        var dir = _params.GetValueOrDefault("dir") ?? "";
        var path = _params.GetValueOrDefault("path") ?? "";

        Mat? img;
        string currentFile;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            img = LoadNextFromSequence(dir);
            currentFile = _lastLoadedFile;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            img = LoadMat(path);
            currentFile = Path.GetFileName(path);
        }
        else
        {
            Log?.Invoke($"[ImageSource] {Name}: 未配置图片目录或文件路径，输出为空");
            return new NodeResult { Decision = "OK" };
        }

        if (img == null)
        {
            return new NodeResult { Decision = "OK" };
        }

        return ImageResult(img, currentFile);
    }

    private static NodeResult ImageResult(Mat img, string currentFile) => new()
    {
        Decision = "OK",
        OutputImage = img,
        Values =
        {
            ["out_w"] = img.Width.ToString(),
            ["out_h"] = img.Height.ToString(),
            ["current_file"] = currentFile,
        },
    };

    private string _lastLoadedFile = "";

    private Mat? LoadNextFromSequence(string dir)
    {
        if (!_sequenceScanned)
        {
            ScanSequence(dir);
        }

        if (_sequence.Count == 0)
        {
            return null; // ScanSequence 已记日志
        }

        var file = _sequence[_cursor];
        var img = LoadMat(file);
        if (img == null)
        {
            return null;
        }

        _lastLoadedFile = Path.GetFileName(file);
        var loopOn = !string.Equals(_params.GetValueOrDefault("loop"), "false", StringComparison.OrdinalIgnoreCase);
        if (_cursor < _sequence.Count - 1)
        {
            _cursor++;
        }
        else if (loopOn)
        {
            _cursor = 0;
        }
        else
        {
            Log?.Invoke($"[ImageSource] {Name}: 序列已到最后一张（loop=false），保持输出最后一张");
        }

        return img;
    }

    private void ScanSequence(string dir)
    {
        _sequenceScanned = true;
        try
        {
            if (!Directory.Exists(dir))
            {
                Log?.Invoke($"[ImageSource] {Name}: 图片目录不存在: {dir}");
                return;
            }

            _sequence.AddRange(Directory.EnumerateFiles(dir)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            if (_sequence.Count == 0)
            {
                Log?.Invoke($"[ImageSource] {Name}: 目录中没有图片文件: {dir}");
                return;
            }

            Log?.Invoke($"[ImageSource] {Name}: 扫描到 {_sequence.Count} 张图片，从第 1 张开始");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[ImageSource] {Name}: 扫描目录失败: {ex.Message}");
        }
    }

    private Mat? LoadMat(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                Log?.Invoke($"[ImageSource] {Name}: 图像文件不存在: {file}");
                return null;
            }

            // 字节流解码：兼容中文/Unicode 路径（与 ImagePreprocessService.LoadBgr 同思路）
            var bytes = File.ReadAllBytes(file);
            var img = Cv2.ImDecode(bytes, ImreadModes.Color);
            if (img.Empty())
            {
                img.Dispose();
                Log?.Invoke($"[ImageSource] {Name}: 图像解码失败: {file}");
                return null;
            }

            return img;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[ImageSource] {Name}: 读取图像失败: {file}（{ex.Message}）");
            return null;
        }
    }

    public void Dispose() { }
}
