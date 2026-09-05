using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpeakerVisionInspection.Camera;

namespace SpeakerVisionInspection;

/// <summary>
/// 相机管理窗口（设备级）：相机列表/连接/预览/软触发 + 采集参数 + 触发参数（含 PLC 输入沿）。
/// IO 结果输出配置属于「相机IO通信」节点参数弹窗，不在本窗口。
/// 只接收主窗口持有的控制器，不创建第二个相机句柄。
/// </summary>
public sealed class CameraManagementDialog : Window
{
    private readonly ICameraController _camera;
    private readonly CameraParameterStore? _store;
    private readonly Action<string> _log;
    private readonly Action<CameraInfo> _connected;
    private readonly Action<CameraParameters, TriggerSettings> _saveCamera;
    private readonly Func<bool> _canModify;
    private readonly ListBox _cameraList = new();
    private readonly TextBlock _status = new();
    private readonly ComboBox _exposureAuto = new();
    private readonly TextBox _exposure = new();
    private readonly ComboBox _gainAuto = new();
    private readonly TextBox _gain = new();
    private readonly TextBox _gamma = new();
    private readonly TextBox _imageWidth = new();
    private readonly TextBox _imageHeight = new();
    private readonly TextBox _frameRate = new();
    private readonly TextBox _actualFrameRate = new() { IsReadOnly = true };
    private readonly ComboBox _pixelFormat = new();
    private readonly ComboBox _triggerMode = new();
    private readonly ComboBox _triggerEdge = new();
    private readonly TextBox _delay = new();
    private readonly TextBox _filter = new();
    private readonly TextBox _burstCount = new();
    private readonly TextBox _interval = new();
    private readonly TextBox _timeout = new();
    private readonly ComboBox _ioLine = new();
    private readonly ComboBox _ioMode = new();
    private readonly CheckBox _ioInvert = new();

    public CameraManagementDialog(
        Window owner,
        ICameraController camera,
        IReadOnlyList<CameraInfo> cameras,
        CameraInfo? selected,
        CameraParameterStore? store,
        Action<CameraInfo> connected,
        Action<string> log,
        Action<CameraParameters, TriggerSettings> saveCamera,
        Func<bool> canModify)
    {
        Owner = owner;
        _camera = camera;
        _store = store;
        _connected = connected;
        _log = log;
        _saveCamera = saveCamera;
        _canModify = canModify;
        Title = "相机管理";
        Width = 980;
        Height = 640;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)(Application.Current.TryFindResource("BgBrush") ?? Brushes.Black);
        Foreground = (Brush)(Application.Current.TryFindResource("TextBrush") ?? Brushes.White);
        Content = BuildContent();
        ApplyDarkListStyle(_cameraList);
        _cameraList.ItemsSource = cameras;
        if (selected is not null) _cameraList.SelectedItem = cameras.FirstOrDefault(c => c.SerialNumber == selected.SerialNumber);
        LoadValues();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(Button("刷新相机", RefreshAsync));
        toolbar.Children.Add(Button("连接选中", ConnectAsync));
        toolbar.Children.Add(Button("断开", DisconnectAsync));
        toolbar.Children.Add(Button("开始预览", StartPreviewAsync));
        toolbar.Children.Add(Button("停止预览", StopPreviewAsync));
        toolbar.Children.Add(Button("软触发", SoftTriggerAsync));
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

        _cameraList.Margin = new Thickness(0, 0, 10, 0);
        _cameraList.DisplayMemberPath = "DisplayName";
        Grid.SetColumn(_cameraList, 0);
        grid.Children.Add(_cameraList);

        var hint = new TextBlock
        {
            Text = "相机状态由主窗口和本窗口共享。\n\n连接后自动应用已保存的采集/触发/IO 输出参数；预览图像显示在主窗口图像区。\n\n" +
                   "触发链路：连续 / 软触发 / PLC 硬触发（Line0 输入，PNP 通常上升沿、NPN 通常下降沿）。\n\n" +
                   "IO 输出（设备控制）：在此配置输出线与反相（硬件初始状态）；NG 脉冲的来源/输出类型/脉宽在流程「相机IO通信」节点参数弹窗里配置，节点执行时按节点配置下发。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        var panel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        FillParameterPanel(panel);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);

        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Foreground = Brushes.LightGray;
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        return root;
    }

    private void FillParameterPanel(Panel panel)
    {
        _exposureAuto.Items.Add("关闭");
        _exposureAuto.Items.Add("单次");
        _exposureAuto.Items.Add("连续");
        AddLabeled(panel, "曝光模式", _exposureAuto);
        AddLabeled(panel, "曝光时间 (us)", _exposure);
        _gainAuto.Items.Add("关闭");
        _gainAuto.Items.Add("单次");
        _gainAuto.Items.Add("连续");
        AddLabeled(panel, "增益模式", _gainAuto);
        AddLabeled(panel, "增益", _gain);
        AddLabeled(panel, "Gamma", _gamma);
        AddLabeled(panel, "图像宽度 (px, 0=不变)", _imageWidth);
        AddLabeled(panel, "图像高度 (px, 0=不变)", _imageHeight);
        AddLabeled(panel, "帧率 (fps, 0=不限)", _frameRate);
        AddLabeled(panel, "实际帧率 (fps)", _actualFrameRate);
        _pixelFormat.Items.Add("Mono8");
        _pixelFormat.Items.Add("RGB8");
        AddLabeled(panel, "像素格式", _pixelFormat);
        _triggerMode.Items.Add("连续");
        _triggerMode.Items.Add("软触发");
        _triggerMode.Items.Add("硬触发");
        AddLabeled(panel, "触发模式", _triggerMode);
        _triggerEdge.Items.Add("上升沿");
        _triggerEdge.Items.Add("下降沿");
        AddLabeled(panel, "硬触发沿", _triggerEdge);
        AddLabeled(panel, "触发延迟 (us)", _delay);
        AddLabeled(panel, "输入滤波 (us)", _filter);
        AddLabeled(panel, "条件触发数 (帧/次)", _burstCount);
        AddLabeled(panel, "最小间隔 (ms)", _interval);
        AddLabeled(panel, "取图超时 (ms)", _timeout);
        AddGroupHeader(panel, "IO 输出（设备控制）");
        _ioLine.Items.Add("Line1");
        _ioLine.Items.Add("Line2");
        _ioLine.Items.Add("Line3");
        AddLabeled(panel, "IO 选择项", _ioLine);
        _ioMode.Items.Add("频闪输出 (Strobe)");
        AddLabeled(panel, "IO 模式", _ioMode);
        _ioInvert.Content = "反相输出（低电平有效）";
        _ioInvert.Height = 28;
        _ioInvert.Margin = new Thickness(0, 7, 0, 2);
        panel.Children.Add(_ioInvert);
        panel.Children.Add(Button("应用 IO 输出配置", ApplyIoSettingsAsync));
        panel.Children.Add(Button("应用采集/触发参数", ApplyCameraSettingsAsync));
    }

    private static void AddGroupHeader(Panel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 12, 0, 2),
            FontWeight = FontWeights.Bold,
        });

    /// <summary>ListBox 默认白底白字在深色主题下不可读；统一面板底色 + SelBrush 选中高亮。</summary>
    private static void ApplyDarkListStyle(ListBox list)
    {
        list.Background = (Brush)(Application.Current.TryFindResource("PanelBrush") ?? Brushes.Transparent);
        list.Foreground = (Brush)(Application.Current.TryFindResource("TextBrush") ?? Brushes.White);
        list.BorderBrush = (Brush)(Application.Current.TryFindResource("BorderBrush") ?? Brushes.Gray);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, list.Foreground));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(4, 2, 4, 2)));
        var selected = new System.Windows.Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, (Brush)(Application.Current.TryFindResource("SelBrush") ?? Brushes.Navy)));
        itemStyle.Triggers.Add(selected);
        list.ItemContainerStyle = itemStyle;
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 4, 10, 4) };
        button.Click += async (_, _) =>
        {
            if (!_canModify()) { _status.Text = "生产检测运行中，设备参数暂不可修改"; return; }
            try { await action(); }
            catch (Exception ex) { _status.Text = ex.Message; _log($"[相机管理] {text}失败: {ex.Message}"); }
        };
        return button;
    }

    private static void AddLabeled(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        control.Height = 28;
        panel.Children.Add(control);
    }

    private async Task RefreshAsync()
    {
        var cameras = await _camera.EnumerateAsync();
        _cameraList.ItemsSource = cameras;
        _status.Text = $"发现 {cameras.Count} 台相机";
        _log($"相机管理：枚举完成，发现 {cameras.Count} 台相机");
    }

    private async Task ConnectAsync()
    {
        if (_cameraList.SelectedItem is not CameraInfo info) { _status.Text = "请先选择相机"; return; }
        if (_camera.State == CameraConnectionState.Connected) { _status.Text = "已有相机连接，请先断开"; return; }
        await _camera.ConnectAsync(info);
        _connected(info);
        if (_store is not null)
        {
            await _camera.ApplyParametersAsync(_store.Load());
            var trigger = _store.LoadTriggerSettings();
            await _camera.ApplyTriggerSettingsAsync(trigger);
            await _camera.SetTriggerModeAsync(trigger.TriggerMode == "软触发" ? CameraTriggerMode.Software : trigger.TriggerMode == "硬触发" ? CameraTriggerMode.Hardware : CameraTriggerMode.Continuous);
            await _camera.ConfigureIoCommunicationAsync(_store.LoadIoCommunicationSettings());
        }
        RefreshActualFrameRate();
        _status.Text = $"已连接：{info.DisplayName}";
        _log($"相机管理：已连接 {info.DisplayName} ({info.SerialNumber})");
    }

    private async Task DisconnectAsync() { await _camera.DisconnectAsync(); _status.Text = "已断开"; _log("相机管理：相机已断开"); }
    private async Task StartPreviewAsync() { await _camera.SetTriggerModeAsync(CameraTriggerMode.Continuous); await _camera.StartPreviewAsync(); _status.Text = "预览已开始"; }
    private async Task StopPreviewAsync() { await _camera.StopPreviewAsync(); _status.Text = "预览已停止"; }
    private async Task SoftTriggerAsync() { await _camera.SetTriggerModeAsync(CameraTriggerMode.Software); await _camera.SoftTriggerAsync(); _status.Text = "软触发已发送"; }

    private async Task ApplyIoSettingsAsync()
    {
        var io = _store?.LoadIoCommunicationSettings() ?? new IoCommunicationSettings();
        io.Enabled = true;
        io.OutputMode = "NgOnly";
        io.NgOutputLine = _ioLine.SelectedIndex switch { 1 => "Line2", 2 => "Line3", _ => "Line1" };
        io.StrobeSource = string.IsNullOrWhiteSpace(io.StrobeSource) ? "FrameTriggerWait" : io.StrobeSource.Trim();
        io.ActiveLevel = _ioInvert.IsChecked == true ? "Low" : "High";
        await _camera.ConfigureIoCommunicationAsync(io);
        _store?.SaveIoCommunicationSettings(io);
        RefreshActualFrameRate();
        _status.Text = $"IO 输出配置已应用: {io.NgOutputLine} / Strobe / {(io.ActiveLevel == "Low" ? "反相(低电平有效)" : "非反相(高电平有效)")}，脉冲宽度等执行参数在「相机IO通信」节点";
        _log($"相机管理：IO 输出配置已应用（{io.NgOutputLine}，{io.ActiveLevel}）");
    }

    private async Task ApplyCameraSettingsAsync()
    {
        var parameters = new CameraParameters
        {
            ExposureAuto = _exposureAuto.SelectedIndex switch { 1 => "Once", 2 => "Continuous", _ => "Off" },
            ExposureTimeUs = Number(_exposure.Text, 5000),
            GainAuto = _gainAuto.SelectedIndex switch { 1 => "Once", 2 => "Continuous", _ => "Off" },
            Gain = Number(_gain.Text, 0),
            Gamma = Number(_gamma.Text, 1),
            ImageWidth = (int)Number(_imageWidth.Text, 0),
            ImageHeight = (int)Number(_imageHeight.Text, 0),
            FrameRate = Number(_frameRate.Text, 0),
            PixelFormat = _pixelFormat.SelectedIndex == 1 ? "RGB8" : "Mono8",
        };
        await _camera.ApplyParametersAsync(parameters);
        var trigger = new TriggerSettings
        {
            TriggerMode = _triggerMode.SelectedIndex switch { 1 => "软触发", 2 => "硬触发", _ => "连续" },
            TriggerSource = "Line0",
            TriggerActivation = _triggerEdge.SelectedIndex == 1 ? "Falling" : "Rising",
            TriggerDelayUs = Number(_delay.Text, 0),
            TriggerFilterUs = Number(_filter.Text, 0),
            BurstFrameCount = Math.Max(1, (int)Number(_burstCount.Text, 1)),
            MinTriggerIntervalMs = Number(_interval.Text, 100),
            GrabTimeoutMs = Number(_timeout.Text, 500),
        };
        await _camera.ApplyTriggerSettingsAsync(trigger);
        await _camera.SetTriggerModeAsync(_triggerMode.SelectedIndex switch { 1 => CameraTriggerMode.Software, 2 => CameraTriggerMode.Hardware, _ => CameraTriggerMode.Continuous });
        _saveCamera(parameters, trigger);
        RefreshActualFrameRate();
        _status.Text = "采集与触发参数已应用";
        _log("相机管理：采集与触发参数已应用");
    }

    private void RefreshActualFrameRate() =>
        _actualFrameRate.Text = _camera.ResultingFrameRate is { } fps ? fps.ToString("F1", CultureInfo.InvariantCulture) : "";

    private void LoadValues()
    {
        var p = _store?.Load() ?? new CameraParameters();
        _exposureAuto.SelectedIndex = p.ExposureAuto.Equals("Once", StringComparison.OrdinalIgnoreCase) ? 1 : p.ExposureAuto.Equals("Continuous", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        _exposure.Text = p.ExposureTimeUs.ToString("F0", CultureInfo.InvariantCulture);
        _gainAuto.SelectedIndex = p.GainAuto.Equals("Once", StringComparison.OrdinalIgnoreCase) ? 1 : p.GainAuto.Equals("Continuous", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        _gain.Text = p.Gain.ToString("F1", CultureInfo.InvariantCulture);
        _gamma.Text = p.Gamma.ToString("F2", CultureInfo.InvariantCulture);
        _imageWidth.Text = p.ImageWidth.ToString(CultureInfo.InvariantCulture);
        _imageHeight.Text = p.ImageHeight.ToString(CultureInfo.InvariantCulture);
        _frameRate.Text = p.FrameRate > 0 ? p.FrameRate.ToString("F0", CultureInfo.InvariantCulture) : "0";
        _pixelFormat.SelectedIndex = p.PixelFormat == "RGB8" ? 1 : 0;
        var t = _store?.LoadTriggerSettings() ?? new TriggerSettings();
        _triggerMode.SelectedIndex = t.TriggerMode == "软触发" ? 1 : t.TriggerMode == "硬触发" ? 2 : 0;
        _triggerEdge.SelectedIndex = t.TriggerActivation == "Falling" ? 1 : 0;
        _delay.Text = t.TriggerDelayUs.ToString("F0", CultureInfo.InvariantCulture);
        _filter.Text = t.TriggerFilterUs.ToString("F0", CultureInfo.InvariantCulture);
        _burstCount.Text = Math.Max(1, t.BurstFrameCount).ToString(CultureInfo.InvariantCulture);
        _interval.Text = t.MinTriggerIntervalMs.ToString("F0", CultureInfo.InvariantCulture);
        _timeout.Text = t.GrabTimeoutMs.ToString("F0", CultureInfo.InvariantCulture);
        var io = _store?.LoadIoCommunicationSettings() ?? new IoCommunicationSettings();
        _ioLine.SelectedIndex = io.NgOutputLine == "Line2" ? 1 : io.NgOutputLine == "Line3" ? 2 : 0;
        _ioMode.SelectedIndex = 0;
        _ioInvert.IsChecked = string.Equals(io.ActiveLevel, "Low", StringComparison.OrdinalIgnoreCase);
        RefreshActualFrameRate();
    }

    private static double Number(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
}
