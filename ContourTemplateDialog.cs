using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace VisionInspection;

/// <summary>
/// 轮廓模板建模弹窗（独立于主图像区）：
/// 选模板图 → 显示 → 拖拽框选模板区域 → 设基准点（默认区域中心，可切换模式点击指定）
/// → 调 sigma/对比度 → 提取预览（轮廓点覆盖）→ 保存到模板目录（shape_template.json + 回填节点模板目录参数）。
/// </summary>
public sealed class ContourTemplateDialog : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly RecipeNode _node;
    private readonly NodeParamDialog? _hostDialog;

    private readonly TextBox _imagePathBox = new() { Height = 24, Width = 330 };
    private readonly Canvas _canvas = new()
    {
        Width = 560,
        Height = 420,
        Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
    };
    private readonly TextBox _sigmaBox = new() { Text = "1.0", Width = 50, Height = 24 };
    private readonly TextBox _contrastBox = new() { Text = "30", Width = 50, Height = 24 };
    private readonly TextBox _levelsBox = new() { Text = "0", Width = 40, Height = 24 };
    private readonly TextBox _dirBox = new() { Height = 24, Width = 330 };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DimGray,
        FontSize = 11,
        Margin = new Thickness(0, 6, 0, 0),
    };
    private readonly RadioButton _roiMode = new() { GroupName = "ctm", IsChecked = true };
    private readonly RadioButton _refMode = new() { GroupName = "ctm" };
    private readonly RadioButton _eraseMode = new() { GroupName = "ctm" };
    private readonly ComboBox _upstreamCombo = new() { Height = 24, Width = 220 };
    private readonly TextBox _eraseRadiusBox = new() { Text = "20", Width = 40, Height = 24 };

    private readonly List<(WpfRect? Roi, WpfPoint? Ref, ShapeTemplate? Preview)> _undo = new();
    private readonly List<(WpfRect? Roi, WpfPoint? Ref, ShapeTemplate? Preview)> _redo = new();
    private bool _erasing;

    /// <summary>擦除光标（圆圈，直径=擦除半径×视图缩放，随鼠标移动）。</summary>
    private readonly System.Windows.Shapes.Ellipse _eraseCursor = new()
    {
        Stroke = Brushes.Yellow,
        StrokeThickness = 1.2,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>擦除笔迹（红色半透明，画布坐标；提取预览/撤销时清空）。</summary>
    private readonly List<System.Windows.Shapes.Polyline> _eraseTrails = new();
    private System.Windows.Shapes.Polyline? _activeTrail;
    private System.Windows.Point _lastMouseCanvas;

    private Mat? _image;
    private double _viewScale = 1.0;
    private double _offsetX;
    private double _offsetY;
    private WpfRect? _roi;
    private WpfPoint? _ref;
    private WpfPoint? _dragStart;
    private ShapeTemplate? _preview;
    private bool _previewDirty;

    public ContourTemplateDialog(MainWindow main, RecipeNode node, NodeParamDialog? hostDialog)
    {
        _main = main;
        _node = node;
        _hostDialog = hostDialog;

        Title = $"轮廓模板建模 — {node.Name}";
        Width = 620;
        Height = 720;
        MinWidth = 620;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["PanelBrush"];

        var panel = new StackPanel { Margin = new Thickness(10) };

        // 模板来源 1：上游节点最近一次执行图像（所见即所得，先执行一次）
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

        // 模板来源 2：图像文件
        var imageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        imageRow.Children.Add(new TextBlock { Text = "模板图:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        imageRow.Children.Add(_imagePathBox);
        imageRow.Children.Add(new Button { Content = "浏览", Width = 56, Height = 24, Margin = new Thickness(6, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => BrowseImage()));
        imageRow.Children.Add(new Button { Content = "加载", Width = 56, Height = 24, Margin = new Thickness(6, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => LoadImage()));
        panel.Children.Add(imageRow);

        panel.Children.Add(_canvas);

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        SetToolIcon(_roiMode, "\uE7A8", "框选区域");
        SetToolIcon(_refMode, "\uE719", "基准点");
        SetToolIcon(_eraseMode, "\uED60", "擦除点");
        modeRow.Children.Add(_roiMode);
        modeRow.Children.Add(_refMode);
        modeRow.Children.Add(_eraseMode);
        modeRow.Children.Add(new TextBlock
        {
            Text = "（基准点=匹配输出的 X/Y 位置，默认区域中心；擦除点会显示光标圈和笔迹）",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        });
        modeRow.Children.Add(new Button
        {
            Content = "\uE7A7",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 34,
            Height = 24,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "撤销（画框/基准点/提取/擦除均可回退，50 步）",
        }.Apply(b => b.Click += (_, _) => UndoStep()));
        modeRow.Children.Add(new Button
        {
            Content = "\uE7A6",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 34,
            Height = 24,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "重做",
        }.Apply(b => b.Click += (_, _) => RedoStep()));
        panel.Children.Add(modeRow);

        var paramRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        paramRow.Children.Add(new TextBlock { Text = "滤波Sigma:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        paramRow.Children.Add(_sigmaBox);
        paramRow.Children.Add(new TextBlock { Text = "建模边缘阈值:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        paramRow.Children.Add(_contrastBox);
        paramRow.Children.Add(new TextBlock { Text = "金字塔层数(0=自动):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        paramRow.Children.Add(_levelsBox);
        paramRow.Children.Add(new TextBlock { Text = "擦除半径:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        paramRow.Children.Add(_eraseRadiusBox);
        paramRow.Children.Add(new Button { Content = "提取预览", Width = 84, Height = 24, Margin = new Thickness(12, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => ExtractPreview()));
        panel.Children.Add(paramRow);

        _sigmaBox.TextChanged += (_, _) => _previewDirty = true;
        _contrastBox.TextChanged += (_, _) => _previewDirty = true;
        _levelsBox.TextChanged += (_, _) => _previewDirty = true;

        var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        dirRow.Children.Add(new TextBlock { Text = "模板目录(自动,可改):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        dirRow.Children.Add(_dirBox);
        dirRow.Children.Add(new Button { Content = "浏览", Width = 56, Height = 24, Margin = new Thickness(6, 0, 0, 0) }
            .Apply(b => b.Click += (_, _) => BrowseDir()));
        panel.Children.Add(dirRow);

        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        saveRow.Children.Add(new Button { Content = "保存模板", Width = 100, Height = 28, FontWeight = FontWeights.Bold }
            .Apply(b => b.Click += (_, _) => SaveTemplate()));
        panel.Children.Add(saveRow);

        panel.Children.Add(_status);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };

        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _canvas.MouseLeave += (_, _) => _eraseCursor.Visibility = Visibility.Collapsed;
        _eraseRadiusBox.TextChanged += (_, _) => UpdateEraseCursor(_lastMouseCanvas);

        _dirBox.Text = node.Params.GetValueOrDefault("model_dir") ?? "";
        if (string.IsNullOrWhiteSpace(_dirBox.Text))
        {
            // 海康习惯：模板随方案自动管理，无需用户选目录（方案目录/模板/节点名）
            var autoDir = _main.GetRecipeTemplateDir(node.Name);
            if (autoDir != null)
            {
                _dirBox.Text = autoDir;
            }
        }
        TryRestoreExistingTemplate();
        Closed += (_, _) => _image?.Dispose();
    }

    /// <summary>
    /// 已有模板自动回填：模板图存档（模板目录/source_image.png）+ ROI/基准点/建模参数 →
    /// 用户直接调参数 → 提取预览 → 保存即可更新模板，无需从头重新建模。
    /// </summary>
    private void TryRestoreExistingTemplate()
    {
        var dir = _main.ResolveModelDir(_dirBox.Text.Trim());
        if (dir is null || !File.Exists(System.IO.Path.Combine(dir, "shape_template.json")))
        {
            return; // 无现有模板：保持全新建模流程
        }
        try
        {
            var template = ShapeTemplate.Load(dir);
            _sigmaBox.Text = template.Sigma.ToString("0.###");
            _contrastBox.Text = template.MinContrast.ToString("0.###");
            var sourcePath = System.IO.Path.Combine(dir, "source_image.png");
            if (!File.Exists(sourcePath))
            {
                SetStatus($"找到现有模板（滤波Sigma={template.Sigma:0.###}/建模边缘阈值={template.MinContrast:0.###} 已回填），但无来源图存档——请重新选同一张模板图后提取");
                return;
            }
            var mat = Cv2.ImDecode(File.ReadAllBytes(sourcePath), ImreadModes.Color);
            if (mat.Empty())
            {
                mat.Dispose();
                return;
            }
            _image?.Dispose();
            _image = mat;
            if (template.RoiW >= 2 && template.RoiH >= 2)
            {
                _roi = new WpfRect(template.RoiX, template.RoiY, template.RoiW, template.RoiH);
            }
            _ref = new WpfPoint(template.RoiX + template.ReferenceX, template.RoiY + template.ReferenceY);
            _preview = template;
            _previewDirty = false;
            ClearEraseTrails();
            ComputeViewTransform();
            Redraw();
            SetStatus($"已加载现有模板（{template.Levels} 层/{template.LevelPoints.FirstOrDefault()?.Count ?? 0} 点）：直接调「滤波Sigma/边缘阈值/金字塔层数」→ 提取预览 → 保存模板；也可重新选图/框选");
        }
        catch (Exception ex)
        {
            SetStatus($"加载现有模板失败: {ex.Message}", true);
        }
    }

    /// <summary>工具单选按钮的图标+文字（Segoe MDL2 Assets，海康工具栏风格）。</summary>
    private static void SetToolIcon(RadioButton rb, string glyph, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        rb.Content = sp;
    }

    /// <summary>使用上游节点最近一次执行的显示图作为模板图（缩略图 → PNG 内存流 → Mat）。</summary>
    private void UseUpstreamImage()
    {
        if (_upstreamCombo.SelectedItem is not string name)
        {
            SetStatus("请先选择上游节点（该节点需在流程中排在本节点之前）", true);
            return;
        }
        if (!_main.TryGetNodeThumb(name, out var bmp))
        {
            SetStatus($"节点「{name}」暂无图像（先执行一次再建模）", true);
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
        _roi = null;
        _ref = null;
        _preview = null;
        ClearEraseTrails();
        ComputeViewTransform();
        Redraw();
        SetStatus($"已使用节点「{name}」的执行图像 {mat.Width}×{mat.Height}，请框选模板区域");
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
            Title = "选择模板图像",
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
        var dlg = new OpenFolderDialog { Title = "选择模板保存目录" };
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
            _roi = null;
            _ref = null;
            _preview = null;
            ClearEraseTrails();
            ComputeViewTransform();
            Redraw();
            SetStatus($"已加载 {mat.Width}×{mat.Height}，请在图像上框选模板区域");
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
        if (_eraseMode.IsChecked == true)
        {
            if (_preview is null || _ref is null)
            {
                SetStatus("先「提取预览」再擦除", true);
                return;
            }
            PushUndo();
            _erasing = true;
            _canvas.CaptureMouse();
            // 笔迹：红色半透明，粗细=擦除直径（随视图缩放，与海康橡皮擦一致）
            _activeTrail = new System.Windows.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 70, 70)),
                StrokeThickness = Math.Max(2, 2 * EraseRadius() * _viewScale),
                IsHitTestVisible = false,
            };
            _activeTrail.Points.Add(e.GetPosition(_canvas));
            EraseAt(p.Value);
            return;
        }
        if (_refMode.IsChecked == true)
        {
            PushUndo();
            _ref = p;
            _preview = null;
            Redraw();
            return;
        }
        PushUndo();
        _dragStart = p;
        _canvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        _lastMouseCanvas = e.GetPosition(_canvas);
        UpdateEraseCursor(_lastMouseCanvas);
        if (_erasing)
        {
            var ep = CanvasToImage(_lastMouseCanvas);
            if (ep is not null)
            {
                _activeTrail?.Points.Add(_lastMouseCanvas);
                EraseAt(ep.Value);
            }
            return;
        }
        if (_dragStart is null || _image is null) return;
        var p = CanvasToImage(e.GetPosition(_canvas));
        if (p is null) return;
        _roi = WpfRect.Inflate(new WpfRect(_dragStart.Value, p.Value), 0.5, 0.5);
        Redraw();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_erasing)
        {
            _erasing = false;
            _canvas.ReleaseMouseCapture();
            if (_activeTrail is not null)
            {
                _eraseTrails.Add(_activeTrail);
                _activeTrail = null;
            }
            return;
        }
        if (_dragStart is null) return;
        _canvas.ReleaseMouseCapture();
        var start = _dragStart.Value;
        _dragStart = null;
        var p = CanvasToImage(e.GetPosition(_canvas));
        if (p is null)
        {
            Redraw();
            return;
        }
        var roi = new WpfRect(start, p.Value);
        if (roi.Width < 4 || roi.Height < 4)
        {
            _roi = null;
            Redraw();
            SetStatus("模板区域过小（至少 4×4）", true);
            return;
        }
        _roi = roi;
        _ref = new WpfPoint(roi.X + roi.Width / 2, roi.Y + roi.Height / 2);
        _preview = null;
        Redraw();
        SetStatus($"模板区域 {_roi.Value.Width:F0}×{_roi.Value.Height:F0}，基准点已设为区域中心（可切「设置基准点」点击修改）");
    }

    private void ExtractPreview()
    {
        if (_image is null || _roi is null || _ref is null)
        {
            SetStatus("请先加载图像并框选模板区域", true);
            return;
        }
        try
        {
            PushUndo();
            _preview = BuildTemplate();
            _previewDirty = false;
            ClearEraseTrails();
            var perLevel = string.Join("/", _preview.LevelPoints.Select(l => l.Count));
            Redraw();
            SetStatus($"提取完成：各层点数 {perLevel}。{DescribeTemplateQuality(_preview)}可切「擦除点」抹掉问题边缘，确认后点「保存模板」。");
        }
        catch (Exception ex)
        {
            SetStatus($"提取失败: {ex.Message}", true);
        }
    }

    /// <summary>清空擦除笔迹（重新提取/撤销/重做/换图后状态已变，旧笔迹不再有意义）。</summary>
    private void ClearEraseTrails()
    {
        _eraseTrails.Clear();
        _activeTrail = null;
    }

    /// <summary>擦除半径（图像像素，橡皮擦光标/笔迹粗细与该值联动）。</summary>
    private double EraseRadius() => double.TryParse(_eraseRadiusBox.Text, out var r) && r > 0 ? r : 20;

    /// <summary>擦除光标：黄色圆圈，直径 = 擦除半径×视图缩放（真实反映将擦除的范围）。</summary>
    private void UpdateEraseCursor(System.Windows.Point pos)
    {
        if (_eraseMode.IsChecked != true || _image is null)
        {
            _eraseCursor.Visibility = Visibility.Collapsed;
            return;
        }
        var d = 2 * EraseRadius() * _viewScale;
        _eraseCursor.Width = d;
        _eraseCursor.Height = d;
        Canvas.SetLeft(_eraseCursor, pos.X - d / 2);
        Canvas.SetTop(_eraseCursor, pos.Y - d / 2);
        _eraseCursor.Visibility = Visibility.Visible;
    }

    /// <summary>擦除点击处半径内的模板点（各金字塔层同步擦除，空间位置对齐原图坐标）。</summary>
    private void EraseAt(WpfPoint imgPoint)
    {
        var preview = _preview!;
        var radius = EraseRadius();
        var r2 = radius * radius;
        var removed = 0;
        var refX = _ref!.Value.X;
        var refY = _ref!.Value.Y;
        for (var level = 0; level < preview.LevelPoints.Count; level++)
        {
            var scale = Math.Pow(2, level);
            preview.LevelPoints[level].RemoveAll(pt =>
            {
                var dx = refX + pt.X * scale - imgPoint.X;
                var dy = refY + pt.Y * scale - imgPoint.Y;
                if (dx * dx + dy * dy <= r2)
                {
                    removed++;
                    return true;
                }
                return false;
            });
        }
        if (removed > 0)
        {
            Redraw();
            SetStatus($"擦除 {removed} 点（剩余 {preview.LevelPoints[0].Count}）。「撤销」可恢复；擦完直接「保存模板」");
        }
    }

    private (WpfRect? Roi, WpfPoint? Ref, ShapeTemplate? Preview) Snapshot() =>
        (_roi, _ref, _preview is null ? null : CloneTemplate(_preview));

    private void PushUndo()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > 50)
        {
            _undo.RemoveAt(0);
        }
        _redo.Clear();
    }

    private void UndoStep()
    {
        if (_undo.Count == 0)
        {
            SetStatus("没有可撤销的操作");
            return;
        }
        var current = Snapshot();
        var (roi, refPt, preview) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        _roi = roi;
        _ref = refPt;
        _preview = preview;
        ClearEraseTrails();
        Redraw();
        SetStatus("已撤销");
    }

    private void RedoStep()
    {
        if (_redo.Count == 0)
        {
            SetStatus("没有可重做的操作");
            return;
        }
        var current = Snapshot();
        var (roi, refPt, preview) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        _roi = roi;
        _ref = refPt;
        _preview = preview;
        ClearEraseTrails();
        Redraw();
        SetStatus("已重做");
    }

    private static ShapeTemplate CloneTemplate(ShapeTemplate t) => new()
    {
        ReferenceX = t.ReferenceX,
        ReferenceY = t.ReferenceY,
        Sigma = t.Sigma,
        MinContrast = t.MinContrast,
        MaxPointsPerLevel = t.MaxPointsPerLevel,
        RoiX = t.RoiX,
        RoiY = t.RoiY,
        RoiW = t.RoiW,
        RoiH = t.RoiH,
        LevelPoints = t.LevelPoints.Select(l => l.ToList()).ToList(),
    };

    private ShapeTemplate BuildTemplate()
    {
        var roi = _roi!.Value;
        var refAbs = _ref!.Value;
        if (!roi.Contains(refAbs))
        {
            throw new ArgumentException("基准点必须位于模板区域内");
        }
        using var gray = new Mat();
        Cv2.CvtColor(_image!, gray, ColorConversionCodes.BGR2GRAY);
        var x = Math.Clamp((int)roi.X, 0, gray.Width - 2);
        var y = Math.Clamp((int)roi.Y, 0, gray.Height - 2);
        var w = Math.Clamp((int)roi.Width, 2, gray.Width - x);
        var h = Math.Clamp((int)roi.Height, 2, gray.Height - y);
        using var patch = new Mat(gray, new OpenCvSharp.Rect(x, y, w, h)).Clone();
        var levels = int.TryParse(_levelsBox.Text, out var n) && n > 0 ? n : 8;
        var sigma = double.TryParse(_sigmaBox.Text, out var s) && s >= 0 ? s : 1.0;
        var contrast = double.TryParse(_contrastBox.Text, out var c) && c >= 0 ? c : 30;
        var template = ShapeTemplateBuilder.Build(
            patch, refAbs.X - x, refAbs.Y - y, sigma, contrast, levels, ShapeTemplateBuilder.DefaultMaxPoints);
        template.RoiX = x;
        template.RoiY = y;
        template.RoiW = w;
        template.RoiH = h;
        return template;
    }

    private static string DescribeTemplateQuality(ShapeTemplate template)
    {
        var points = template.LevelPoints.FirstOrDefault() ?? [];
        var cells = new HashSet<(int X, int Y)>();
        var width = Math.Max(1, template.RoiW);
        var height = Math.Max(1, template.RoiH);
        foreach (var point in points)
        {
            var x = Math.Clamp((int)Math.Floor((point.X + template.ReferenceX) / width * 4), 0, 3);
            var y = Math.Clamp((int)Math.Floor((point.Y + template.ReferenceY) / height * 4), 0, 3);
            cells.Add((x, y));
        }

        var coverage = cells.Count / 16.0;
        if (points.Count < 30)
        {
            return $"质量警告：有效点仅 {points.Count} 个，建议降低建模边缘阈值。";
        }
        if (coverage < 0.25)
        {
            return $"质量警告：模板空间覆盖率 {coverage:P0}，建议扩大 ROI 或重新选取图像。";
        }
        return $"质量：有效点 {points.Count} 个，空间覆盖率 {coverage:P0}。";
    }

    private void SaveTemplate()
    {
        var dir = _dirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            SetStatus("请先填写模板保存目录", true);
            return;
        }
        try
        {
            var template = _preview is null || _previewDirty ? BuildTemplate() : _preview;
            if (template.LevelPoints.Count == 0 || template.LevelPoints.All(l => l.Count == 0))
            {
                throw new InvalidDataException("模板没有有效轮廓点，请降低建模边缘阈值或重新框选区域");
            }
            template.Save(dir);
            // 立即回读，避免目录可写但生成文件不可用时误报保存成功。
            _ = ShapeTemplate.Load(dir);
            // 来源图存档：下次打开弹窗自动回填（调参数即可重建模）；纯 .NET PNG 编码兼容中文路径
            if (_image is not null)
            {
                var bmpSource = MatToBitmapSource(_image);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                File.WriteAllBytes(System.IO.Path.Combine(dir, "source_image.png"), ms.ToArray());
            }
            _node.Params["model_dir"] = dir;
            _main.HotApplyParam(_node, "model_dir", dir);
            _preview = template;
            _previewDirty = false;
            _main.LogUi($"[轮廓匹配] 节点 {_node.Name}: 模板已保存 → {dir}（{template.LevelPoints.FirstOrDefault()?.Count ?? 0} 点/{template.Levels} 层），基准点 ({template.ReferenceX:F0},{template.ReferenceY:F0})");
            _hostDialog?.NotifyRoiChanged();
            SetStatus("模板已保存，节点参数「模板目录」已更新");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", true);
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

        if (_roi is { } roi)
        {
            var tl = ImageToCanvas(new WpfPoint(roi.X, roi.Y));
            var rectElement = new System.Windows.Shapes.Rectangle
            {
                Width = roi.Width * _viewScale,
                Height = roi.Height * _viewScale,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(rectElement);
            Canvas.SetLeft(rectElement, tl.X);
            Canvas.SetTop(rectElement, tl.Y);
        }

        if (_ref is { } reference)
        {
            var c = ImageToCanvas(reference);
            _canvas.Children.Add(new Line
            {
                X1 = c.X - 7, Y1 = c.Y, X2 = c.X + 7, Y2 = c.Y,
                Stroke = Brushes.Red, StrokeThickness = 1.5, IsHitTestVisible = false,
            });
            _canvas.Children.Add(new Line
            {
                X1 = c.X, Y1 = c.Y - 7, X2 = c.X, Y2 = c.Y + 7,
                Stroke = Brushes.Red, StrokeThickness = 1.5, IsHitTestVisible = false,
            });
        }

        if (_preview is { } preview && _ref is not null && preview.LevelPoints.Count > 0)
        {
            foreach (var p in preview.LevelPoints[0])
            {
                var c = ImageToCanvas(new WpfPoint(_ref.Value.X + p.X, _ref.Value.Y + p.Y));
                var dot = new Ellipse
                {
                    Width = 2,
                    Height = 2,
                    Fill = Brushes.Lime,
                    IsHitTestVisible = false,
                };
                _canvas.Children.Add(dot);
                Canvas.SetLeft(dot, c.X - 1);
                Canvas.SetTop(dot, c.Y - 1);
            }
        }

        // 擦除笔迹 + 光标（Redraw 会清空画布，这里统一补回）
        foreach (var trail in _eraseTrails)
        {
            _canvas.Children.Add(trail);
        }
        if (_activeTrail is not null)
        {
            _canvas.Children.Add(_activeTrail);
        }
        if (_eraseMode.IsChecked == true && _image is not null)
        {
            _canvas.Children.Add(_eraseCursor);
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
