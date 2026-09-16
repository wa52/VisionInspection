using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace VisionInspection;

/// <summary>
/// 字符训练弹窗（字符识别节点专用，海康式「字符训练」）：
/// 样本图（上游节点执行图像 / 图像文件）→ 拖框选中单个字符 → 输入标签 → 保存字模
/// （按节点预处理参数 二值化/形态学 → 归一化 32×48 → 存入字模库 字符/n.png，同字符可多采样追加）。
/// 弹窗内预处理参数与节点共用：保存时自动回写节点参数（HotApplyParam），保证训练/识别一致；
/// 字模列表支持删除；字模目录留空时自动用 方案目录/模板/节点名。
/// </summary>
public sealed class CharTemplateDialog : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly RecipeNode _node;
    private readonly NodeParamDialog? _hostDialog;

    private readonly TextBox _imagePathBox = new() { Height = 24, Width = 300 };
    private readonly Canvas _canvas = new()
    {
        Width = 540,
        Height = 400,
        Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
    };
    private readonly ComboBox _upstreamCombo = new() { Height = 24, Width = 200 };
    private readonly ComboBox _binMethodBox = new() { Height = 24, Width = 90 };
    private readonly TextBox _thresholdBox = new() { Text = "128", Width = 46, Height = 24 };
    private readonly ComboBox _polarityBox = new() { Height = 24, Width = 90 };
    private readonly TextBox _medianBox = new() { Text = "0", Width = 34, Height = 24 };
    private readonly TextBox _dilateBox = new() { Text = "0", Width = 34, Height = 24 };
    private readonly TextBox _erodeBox = new() { Text = "0", Width = 34, Height = 24 };
    private readonly TextBox _labelBox = new() { Height = 24, Width = 60, MaxLength = 16 };
    private readonly Image _previewImage = new()
    {
        Width = 96,
        Height = 144,
        Stretch = Stretch.Uniform,
        Cursor = Cursors.Cross,
        ToolTip = "归一化字模预览（白=前景）。按住鼠标拖动可擦除噪点/侵入的邻字符像素，擦完的样本即保存与匹配用的样本",
    };
    private readonly TextBox _eraseRadiusBox = new() { Text = "1", Width = 28, Height = 24, ToolTip = "擦除笔半径（字模像素）" };
    private readonly TextBox _dirBox = new() { Height = 24, Width = 300 };
    private readonly ListBox _libList = new() { Height = 130 };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DimGray,
        FontSize = 11,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private Mat? _image;
    private double _viewScale = 1.0;
    private double _offsetX;
    private double _offsetY;
    private WpfRect? _charRect;
    private WpfPoint? _dragStart;

    /// <summary>当前字模样本（归一化 32×48 二值像素；框选/参数变化时重算，擦除直接改写此数组）。</summary>
    private byte[]? _samplePixels;

    /// <summary>擦除撤销栈（每次擦笔前的样本快照，上限 50）。</summary>
    private readonly List<byte[]> _undoSamples = new();
    private bool _erasingSample;

    /// <summary>字模库清单（与 _libList 选项索引对齐；删除用）。</summary>
    private List<(string Label, string Path)> _samples = new();

    public CharTemplateDialog(MainWindow main, RecipeNode node, NodeParamDialog? hostDialog)
    {
        _main = main;
        _node = node;
        _hostDialog = hostDialog;

        Title = $"字符训练 — {node.Name}";
        Width = 600;
        Height = 780;
        MinWidth = 600;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["PanelBrush"];

        var panel = new StackPanel { Margin = new Thickness(10) };

        // 样本图来源 1：上游节点最近一次执行图像
        var upstreamRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        upstreamRow.Children.Add(new TextBlock { Text = "节点图像:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _upstreamCombo.ItemsSource = _main.GetUpstreamNodeNames(node);
        upstreamRow.Children.Add(_upstreamCombo);
        upstreamRow.Children.Add(new Button { Content = "使用该节点图像", Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => UseUpstreamImage()));
        upstreamRow.Children.Add(new TextBlock
        {
            Text = "（取该节点最近一次执行的图；也可用下方图像文件）",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        });
        panel.Children.Add(upstreamRow);

        // 样本图来源 2：图像文件
        var imageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        imageRow.Children.Add(new TextBlock { Text = "样本图:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        imageRow.Children.Add(_imagePathBox);
        imageRow.Children.Add(new Button { Content = "浏览", Width = 56, Height = 24, Margin = new Thickness(6, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => BrowseImage()));
        panel.Children.Add(imageRow);

        panel.Children.Add(_canvas);

        // 预处理参数（与节点参数同源，保存时回写，保证训练/识别一致）
        var paramRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _binMethodBox.ItemsSource = new[] { "固定阈值", "Otsu自动" };
        _polarityBox.ItemsSource = new[] { CharRecNode.PolarityBrightOnDark, CharRecNode.PolarityDarkOnBright };
        paramRow.Children.Add(new TextBlock { Text = "二值化:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        paramRow.Children.Add(_binMethodBox);
        paramRow.Children.Add(new TextBlock { Text = "阈值:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        paramRow.Children.Add(_thresholdBox);
        paramRow.Children.Add(new TextBlock { Text = "极性:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        paramRow.Children.Add(_polarityBox);
        paramRow.Children.Add(new TextBlock { Text = "中值:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        paramRow.Children.Add(_medianBox);
        paramRow.Children.Add(new TextBlock { Text = "膨胀:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        paramRow.Children.Add(_dilateBox);
        paramRow.Children.Add(new TextBlock { Text = "腐蚀:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        paramRow.Children.Add(_erodeBox);
        paramRow.Children.Add(new Button { Content = "同步参数到节点", Height = 24, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(10, 0, 0, 0), ToolTip = "把当前预处理参数写入节点（热生效），保证识别与训练一致" }
            .Apply(b => b.Click += (_, _) => SyncParamsToNode()));
        panel.Children.Add(paramRow);

        // 预处理参数变化 → 已框选的字符预览立即重算（调参即见归一化字模效果，无需重新框选）
        void RefreshPreviewLive()
        {
            if (_image is not null && _charRect is { } cr)
            {
                UpdatePreview(cr);
            }
        }
        _binMethodBox.SelectionChanged += (_, _) => RefreshPreviewLive();
        _thresholdBox.TextChanged += (_, _) => RefreshPreviewLive();
        _polarityBox.SelectionChanged += (_, _) => RefreshPreviewLive();
        _medianBox.TextChanged += (_, _) => RefreshPreviewLive();
        _dilateBox.TextChanged += (_, _) => RefreshPreviewLive();
        _erodeBox.TextChanged += (_, _) => RefreshPreviewLive();

        // 标签 + 预览 + 保存
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        saveRow.Children.Add(new TextBlock { Text = "字符标签:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        saveRow.Children.Add(_labelBox);
        saveRow.Children.Add(new TextBlock
        {
            Text = "（框选一个字符后输入，如 0/A/X/扬；同字符可多采样）",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        });
        saveRow.Children.Add(new Border
        {
            Child = _previewImage,
            Margin = new Thickness(10, 0, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["HoverBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        saveRow.Children.Add(new TextBlock { Text = "擦除半径:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        saveRow.Children.Add(_eraseRadiusBox);
        saveRow.Children.Add(new Button { Content = "撤销擦除", Width = 72, Height = 28, Margin = new Thickness(8, 0, 0, 0), ToolTip = "撤销上一次擦笔（50 步）" }
            .Apply(b => b.Click += (_, _) => UndoErase()));
        saveRow.Children.Add(new Button { Content = "保存字模", Width = 88, Height = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => SaveSample()));
        panel.Children.Add(saveRow);

        // 字模库目录
        var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        dirRow.Children.Add(new TextBlock { Text = "字模库目录(自动,可改):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        dirRow.Children.Add(_dirBox);
        dirRow.Children.Add(new Button { Content = "浏览", Width = 56, Height = 24, Margin = new Thickness(6, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => BrowseDir()));
        panel.Children.Add(dirRow);

        // 字模库清单
        panel.Children.Add(new TextBlock { Text = "已有字模（字符 [样本数]，选中后可删除）:", Margin = new Thickness(0, 8, 0, 2) });
        panel.Children.Add(_libList);
        panel.Children.Add(new Button { Content = "删除选中字模", Width = 100, Height = 24, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }
            .Apply(b => b.Click += (_, _) => DeleteSelected()));

        panel.Children.Add(_status);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };

        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _previewImage.MouseLeftButtonDown += Preview_MouseLeftButtonDown;
        _previewImage.MouseMove += Preview_MouseMove;
        _previewImage.MouseLeftButtonUp += Preview_MouseLeftButtonUp;
        Closed += (_, _) => _image?.Dispose();

        // 初始值：预处理参数取节点当前参数；目录留空 = 方案目录/模板/节点名（海康式随方案管理）
        _binMethodBox.SelectedItem = node.Params.GetValueOrDefault("bin_method") is { Length: > 0 } bm && bm == "Otsu自动" ? "Otsu自动" : "固定阈值";
        _thresholdBox.Text = node.Params.GetValueOrDefault("threshold") is { Length: > 0 } th ? th : "128";
        _polarityBox.SelectedItem = node.Params.GetValueOrDefault("polarity") is { Length: > 0 } po && po == CharRecNode.PolarityDarkOnBright
            ? CharRecNode.PolarityDarkOnBright
            : CharRecNode.PolarityBrightOnDark;
        _medianBox.Text = node.Params.GetValueOrDefault("median") is { Length: > 0 } md ? md : "0";
        _dilateBox.Text = node.Params.GetValueOrDefault("dilate") is { Length: > 0 } dl ? dl : "0";
        _erodeBox.Text = node.Params.GetValueOrDefault("erode") is { Length: > 0 } er ? er : "0";
        _dirBox.Text = node.Params.GetValueOrDefault("model_dir") ?? "";
        if (string.IsNullOrWhiteSpace(_dirBox.Text))
        {
            var autoDir = _main.GetRecipeTemplateDir(node.Name);
            if (autoDir != null)
            {
                _dirBox.Text = autoDir;
            }
        }
        RefreshLibrary();
    }

    private void UseUpstreamImage()
    {
        if (_upstreamCombo.SelectedItem is not string name)
        {
            SetStatus("请先选择上游节点（该节点需在流程中排在本节点之前）", true);
            return;
        }
        if (!_main.TryGetNodeThumb(name, out var bmp))
        {
            SetStatus($"节点「{name}」暂无图像（先执行一次再训练）", true);
            return;
        }
        var mat = BitmapSourceToMat(bmp);
        if (mat is null)
        {
            SetStatus("节点图像转换失败", true);
            return;
        }
        _image?.Dispose();
        _image = mat;
        _charRect = null;
        UpdatePreview(null);
        ComputeViewTransform();
        Redraw();
        SetStatus($"已使用节点「{name}」的执行图像 {mat.Width}×{mat.Height}，请框选单个字符");
    }

    private static Mat? BitmapSourceToMat(BitmapSource bmp)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var mat = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
            return mat.Empty() ? null : mat;
        }
        catch
        {
            return null;
        }
    }

    private void BrowseImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择样本图像",
            Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|全部文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _imagePathBox.Text = dlg.FileName;
            LoadImage();
        }
    }

    private void BrowseDir()
    {
        var dlg = new OpenFolderDialog { Title = "选择字模库目录" };
        if (dlg.ShowDialog(this) == true)
        {
            _dirBox.Text = dlg.FolderName;
        }
    }

    private void LoadImage()
    {
        var path = _imagePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetStatus("图像文件不存在", true);
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
            if (mat.Empty())
            {
                mat.Dispose();
                SetStatus("图像解码失败", true);
                return;
            }
            _image?.Dispose();
            _image = mat;
            _charRect = null;
            UpdatePreview(null);
            ComputeViewTransform();
            Redraw();
            SetStatus($"已加载 {mat.Width}×{mat.Height}，请在图像上框选单个字符");
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}", true);
        }
    }

    private void ComputeViewTransform()
    {
        if (_image is null) return;
        _viewScale = Math.Min(_canvas.Width / _image.Width, _canvas.Height / _image.Height);
        _offsetX = (_canvas.Width - _image.Width * _viewScale) / 2.0;
        _offsetY = (_canvas.Height - _image.Height * _viewScale) / 2.0;
    }

    private WpfPoint? CanvasToImage(WpfPoint p)
    {
        if (_image is null) return null;
        var x = (p.X - _offsetX) / _viewScale;
        var y = (p.Y - _offsetY) / _viewScale;
        if (x < 0 || y < 0 || x >= _image.Width || y >= _image.Height) return null;
        return new WpfPoint(x, y);
    }

    private WpfPoint ImageToCanvas(WpfPoint p) =>
        new(_offsetX + p.X * _viewScale, _offsetY + p.Y * _viewScale);

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_image is null) return;
        var p = CanvasToImage(e.GetPosition(_canvas));
        if (p is null) return;
        _dragStart = p;
        _canvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null || _image is null) return;
        var p = CanvasToImage(e.GetPosition(_canvas));
        if (p is null) return;
        _charRect = WpfRect.Inflate(new WpfRect(_dragStart.Value, p.Value), 0.5, 0.5);
        Redraw();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _canvas.ReleaseMouseCapture();
        _dragStart = null;
        if (_charRect is not { } cr) return; // 原地点击（未拖框）：保留现有框选
        if (cr.Width < 3 || cr.Height < 3)
        {
            _charRect = null;
            UpdatePreview(null);
            Redraw();
            SetStatus("字符区域过小（至少 3×3）", true);
            return;
        }
        UpdatePreview(cr);
        Redraw();
        SetStatus($"字符区域 {cr.Width:F0}×{cr.Height:F0}（归一化 {CharTemplateLibrary.NormW}×{CharTemplateLibrary.NormH}），输入标签后点「保存字模」");
    }

    /// <summary>当前弹窗预处理参数。</summary>
    private (double Threshold, bool Otsu, bool BrightOnDark, int Median, int Dilate, int Erode) ReadParams()
    {
        var otsu = (_binMethodBox.SelectedItem as string) == "Otsu自动";
        var bright = (_polarityBox.SelectedItem as string) != CharRecNode.PolarityDarkOnBright;
        var threshold = double.TryParse(_thresholdBox.Text, out var t) ? Math.Clamp(t, 0, 255) : 128;
        var median = int.TryParse(_medianBox.Text, out var m) ? Math.Clamp(m, 0, 9) : 0;
        var dilate = int.TryParse(_dilateBox.Text, out var d) ? Math.Clamp(d, 0, 10) : 0;
        var erode = int.TryParse(_erodeBox.Text, out var er) ? Math.Clamp(er, 0, 10) : 0;
        return (threshold, otsu, bright, median, dilate, erode);
    }

    /// <summary>把弹窗预处理参数回写节点（热生效），训练/识别保持一致。</summary>
    private void SyncParamsToNode()
    {
        var (threshold, _, bright, median, dilate, erode) = ReadParams();
        _node.Params["bin_method"] = (_binMethodBox.SelectedItem as string) ?? "固定阈值";
        _node.Params["threshold"] = threshold.ToString("0.#");
        _node.Params["polarity"] = (_polarityBox.SelectedItem as string) ?? CharRecNode.PolarityBrightOnDark;
        _node.Params["median"] = median.ToString();
        _node.Params["dilate"] = dilate.ToString();
        _node.Params["erode"] = erode.ToString();
        foreach (var (key, value) in _node.Params.Where(kv =>
                     kv.Key is "bin_method" or "threshold" or "polarity" or "median" or "dilate" or "erode"))
        {
            _main.HotApplyParam(_node, key, value);
        }
        _main.LogUi($"[字符训练] 节点 {_node.Name}: 预处理参数已同步（二值化={_node.Params["bin_method"]} 阈值={_node.Params["threshold"]} 极性={_node.Params["polarity"]} 中值={median} 膨胀={dilate} 腐蚀={erode}）");
        SetStatus("预处理参数已同步到节点");
    }

    /// <summary>框选区 → 归一化字模样本（重算会清掉手工擦除；与保存用的流程完全一致）。</summary>
    private void UpdatePreview(WpfRect? rect)
    {
        if (rect is null || _image is null)
        {
            _samplePixels = null;
            _previewImage.Source = null;
            return;
        }
        try
        {
            _samplePixels = ExtractSample(rect.Value);
            _undoSamples.Clear(); // 重算 = 新样本，旧擦除撤销点不再适用
        }
        catch
        {
            _samplePixels = null;
        }
        RenderSample();
    }

    /// <summary>把当前样本渲染到预览图（白=前景）。</summary>
    private void RenderSample()
    {
        if (_samplePixels is null)
        {
            _previewImage.Source = null;
            return;
        }
        using var mat = new Mat(CharTemplateLibrary.NormH, CharTemplateLibrary.NormW, MatType.CV_8UC1);
        mat.SetArray(_samplePixels); // 写入托管数组必须 SetArray 落回 Mat
        using var bgr = new Mat();
        Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
        var src = BitmapSource.Create(
            bgr.Width, bgr.Height, 96.0, 96.0, PixelFormats.Bgr24, null,
            bgr.Data, bgr.Width * bgr.Height * 3, bgr.Width * 3);
        src.Freeze();
        _previewImage.Source = src;
    }

    // ===== 字模预览擦除（所见即所得：擦的就是保存与匹配用的样本像素）=====

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_samplePixels is null) return;
        PushUndoSample();
        _erasingSample = true;
        _previewImage.CaptureMouse();
        EraseAtSample(e.GetPosition(_previewImage));
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_erasingSample || _samplePixels is null) return;
        EraseAtSample(e.GetPosition(_previewImage));
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_erasingSample) return;
        _erasingSample = false;
        _previewImage.ReleaseMouseCapture();
    }

    private void PushUndoSample()
    {
        if (_samplePixels is null) return;
        _undoSamples.Add((byte[])_samplePixels.Clone());
        while (_undoSamples.Count > 50)
        {
            _undoSamples.RemoveAt(0);
        }
    }

    private void UndoErase()
    {
        if (_undoSamples.Count == 0)
        {
            SetStatus("没有可撤销的擦除");
            return;
        }
        _samplePixels = _undoSamples[^1];
        _undoSamples.RemoveAt(_undoSamples.Count - 1);
        RenderSample();
        SetStatus("已撤销上一次擦除");
    }

    /// <summary>擦除预览图点击处的圆形区域（半径=字模像素）：前景置 0（背景）。</summary>
    private void EraseAtSample(WpfPoint pos)
    {
        var pixels = _samplePixels;
        if (pixels is null) return;
        var w = _previewImage.ActualWidth > 0 ? _previewImage.ActualWidth : _previewImage.Width;
        var h = _previewImage.ActualHeight > 0 ? _previewImage.ActualHeight : _previewImage.Height;
        var cx = (int)(pos.X / w * CharTemplateLibrary.NormW);
        var cy = (int)(pos.Y / h * CharTemplateLibrary.NormH);
        var r = int.TryParse(_eraseRadiusBox.Text, out var rr) ? Math.Clamp(rr, 0, 8) : 1;
        var removed = 0;
        for (var dy = -r; dy <= r; dy++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var px = cx + dx;
                var py = cy + dy;
                if (px < 0 || py < 0 || px >= CharTemplateLibrary.NormW || py >= CharTemplateLibrary.NormH) continue;
                var idx = py * CharTemplateLibrary.NormW + px;
                if (pixels[idx] != 0)
                {
                    pixels[idx] = 0;
                    removed++;
                }
            }
        }
        if (removed > 0)
        {
            RenderSample();
            SetStatus($"已擦除 {removed} 像素（「撤销擦除」可恢复；改任何预处理参数会重算样本、手工擦除作废）");
        }
    }

    /// <summary>框选区 → 归一化 32×48 二值样本（裁剪框内 中值/二值化/形态学，与节点 ROI 内流程一致）。</summary>
    private byte[] ExtractSample(WpfRect rect)
    {
        var (threshold, otsu, bright, median, dilate, erode) = ReadParams();
        using var gray = CharRecNode.ToGray(_image!);
        var x = Math.Clamp((int)rect.X, 0, Math.Max(0, gray.Width - 2));
        var y = Math.Clamp((int)rect.Y, 0, Math.Max(0, gray.Height - 2));
        var w = Math.Clamp((int)rect.Width, 2, Math.Max(2, gray.Width - x));
        var h = Math.Clamp((int)rect.Height, 2, Math.Max(2, gray.Height - y));
        using var crop = new Mat(gray, new OpenCvSharp.Rect(x, y, w, h));
        using var medianMat = CharRecNode.ApplyMedian(crop, median);
        var binSrc = medianMat ?? crop;
        using var bin = CharRecognizer.Binarize(binSrc, threshold, otsu, bright);
        CharRecognizer.MorphApply(bin, dilate, erode);
        return CharRecognizer.NormalizeSample(bin, new OpenCvSharp.Rect(0, 0, bin.Width, bin.Height));
    }

    private void SaveSample()
    {
        if (_image is null)
        {
            SetStatus("请先加载样本图（节点图像或图像文件）", true);
            return;
        }
        if (_charRect is not { } rect || rect.Width < 3 || rect.Height < 3)
        {
            SetStatus("请先在图像上框选单个字符", true);
            return;
        }
        var label = _labelBox.Text.Trim();
        if (label.Length == 0)
        {
            SetStatus("请输入字符标签（框选的字符是什么就输什么）", true);
            return;
        }
        var dir = _dirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            SetStatus("字模库目录为空（请填写或恢复自动目录）", true);
            return;
        }
        try
        {
            // 用当前样本（含手工擦除）保存，不重新提取——保证所见即所得
            var pixels = _samplePixels ?? ExtractSample(rect);
            var path = CharTemplateLibrary.SaveSample(dir, label, pixels);
            SyncParamsToNode(); // 训练时把预处理参数一并固化到节点
            if (!string.Equals(_node.Params.GetValueOrDefault("model_dir"), dir, StringComparison.Ordinal))
            {
                _node.Params["model_dir"] = dir;
                _main.HotApplyParam(_node, "model_dir", dir);
            }
            RefreshLibrary();
            _main.LogUi($"[字符训练] 节点 {_node.Name}: 字模「{label}」已保存 → {path}");
            SetStatus($"字模「{label}」已保存（同字符继续框选可多采样）");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", true);
        }
    }

    private void RefreshLibrary()
    {
        var dir = _dirBox.Text.Trim();
        _samples = CharTemplateLibrary.ListSamples(dir);
        _libList.Items.Clear();
        foreach (var group in _samples.GroupBy(s => s.Label, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var count = group.Count();
            var i = 0;
            foreach (var (label, path) in group)
            {
                i++;
                _libList.Items.Add($"{label}  样本{i}/{count}  {Path.GetFileName(path)}");
            }
        }
    }

    private void DeleteSelected()
    {
        var index = _libList.SelectedIndex;
        if (index < 0 || index >= _samples.Count)
        {
            SetStatus("请先在字模列表中选择一个样本", true);
            return;
        }
        try
        {
            var (label, path) = _samples[index];
            CharTemplateLibrary.DeleteSample(path);
            RefreshLibrary();
            _main.LogUi($"[字符训练] 节点 {_node.Name}: 字模「{label}」样本已删除 → {path}");
            SetStatus($"已删除字模「{label}」样本 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败: {ex.Message}", true);
        }
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        if (_image is null) return;

        using var display = new Mat();
        Cv2.Resize(_image, display, new OpenCvSharp.Size(
            (int)Math.Round(_image.Width * _viewScale), (int)Math.Round(_image.Height * _viewScale)));
        var bitmap = MatToBitmapSource(display);
        var imageElement = new Image
        {
            Source = bitmap,
            Width = display.Width,
            Height = display.Height,
        };
        _canvas.Children.Add(imageElement);
        Canvas.SetLeft(imageElement, _offsetX);
        Canvas.SetTop(imageElement, _offsetY);

        if (_charRect is { } rect)
        {
            var tl = ImageToCanvas(new WpfPoint(rect.X, rect.Y));
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = rect.Width * _viewScale,
                Height = rect.Height * _viewScale,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, tl.X);
            Canvas.SetTop(box, tl.Y);
            _canvas.Children.Add(box);
        }
    }

    private static BitmapSource MatToBitmapSource(Mat mat)
    {
        using var bgr = new Mat();
        if (mat.Channels() == 1)
        {
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            mat.CopyTo(bgr);
        }
        var bmp = BitmapSource.Create(bgr.Width, bgr.Height, 96.0, 96.0, PixelFormats.Bgr24, null,
            bgr.Data, bgr.Width * bgr.Height * 3, bgr.Width * 3);
        bmp.Freeze();
        return bmp;
    }

    private void SetStatus(string text, bool isError = false) =>
        _status.Text = text;
}
