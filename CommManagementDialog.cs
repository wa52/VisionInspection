using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpeakerVisionInspection.Comm;

namespace SpeakerVisionInspection;

/// <summary>
/// 通信管理窗口（设备级，VM 通信管理式）：通信设备列表 + 通信参数（协议/目标/端口/串口参数/自动重连/接收结束符）
/// + 连接/断开/保存设备 + 发送测试 + 接收日志。支持 TCP客户端/TCP服务端/UDP/串口，ModBus通信 为占位。
/// 独立链路归本窗口所有，关闭窗口即断开；生产闭环仍走 IPlcClient 接缝。
/// </summary>
public sealed class CommManagementDialog : Window
{
    private static readonly string[] Protocols = ["TCP客户端", "TCP服务端", "UDP", "串口", "ModBus通信"];

    private readonly CommDeviceStore? _store;
    private readonly Action<string> _log;
    private readonly ListBox _deviceList = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _protocol = new();
    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly TextBox _serialPortName = new();
    private readonly TextBox _baudRate = new();
    private readonly TextBox _dataBits = new();
    private readonly ComboBox _parity = new();
    private readonly ComboBox _stopBits = new();
    private readonly CheckBox _autoReconnect = new();
    private readonly TextBox _terminator = new();
    private readonly TextBox _sendText = new() { Text = "Hello" };
    private readonly ListBox _receiveLog = new();
    private readonly Dictionary<Control, UIElement> _fields = new();
    private List<CommDevice> _devices = [];
    private ICommLink? _link;

    public CommManagementDialog(Window owner, CommDeviceStore? store, Action<string> log)
    {
        Owner = owner;
        _store = store;
        _log = log;
        Title = "通信管理";
        Width = 980;
        Height = 640;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)(Application.Current.TryFindResource("BgBrush") ?? Brushes.Black);
        Foreground = (Brush)(Application.Current.TryFindResource("TextBrush") ?? Brushes.White);
        Content = BuildContent();
        ApplyDarkListStyle(_deviceList);
        ApplyDarkListStyle(_receiveLog);
        _devices = store?.Load() ?? [];
        RefreshDeviceList(_devices.Count > 0 ? _devices[0] : null);
    }

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

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel();
        var listHeader = new TextBlock { Text = "通信设备列表", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(listHeader, Dock.Top);
        left.Children.Add(listHeader);
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        listButtons.Children.Add(Button("添加", AddDeviceAsync));
        listButtons.Children.Add(Button("删除", DeleteDeviceAsync));
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        _deviceList.DisplayMemberPath = nameof(CommDevice.Name);
        _deviceList.Margin = new Thickness(0, 0, 10, 0);
        _deviceList.SelectionChanged += DeviceList_SelectionChanged;
        left.Children.Add(_deviceList);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        var paramPanel = new StackPanel();
        BuildParameterPanel(paramPanel);
        var scroll = new ScrollViewer { Content = paramPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(scroll, Dock.Top);
        right.Children.Add(scroll);

        var logHeader = new TextBlock { Text = "接收日志", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) };
        DockPanel.SetDock(logHeader, Dock.Top);
        right.Children.Add(logHeader);
        _receiveLog.FontFamily = new FontFamily("Consolas");
        right.Children.Add(_receiveLog);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        Grid.SetRow(grid, 0);
        root.Children.Add(grid);

        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Foreground = Brushes.LightGray;
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        return root;
    }

    private void BuildParameterPanel(Panel panel)
    {
        AddLabeled(panel, "设备名称", _name);
        foreach (var protocol in Protocols)
        {
            _protocol.Items.Add(protocol);
        }

        _protocol.SelectionChanged += (_, _) => RefreshProtocolFields();
        AddLabeled(panel, "协议类型", _protocol);
        AddLabeled(panel, "目标IP (客户端/UDP)", _host);
        AddLabeled(panel, "端口 (连接/监听/本地)", _port);
        AddLabeled(panel, "串口名", _serialPortName);
        AddLabeled(panel, "波特率", _baudRate);
        AddLabeled(panel, "数据位", _dataBits);
        _parity.Items.Add("无校验");
        _parity.Items.Add("偶校验");
        _parity.Items.Add("奇校验");
        AddLabeled(panel, "校验位", _parity);
        _stopBits.Items.Add("1 位");
        _stopBits.Items.Add("2 位");
        AddLabeled(panel, "停止位", _stopBits);
        _autoReconnect.Content = "自动重连";
        _autoReconnect.Height = 28;
        _autoReconnect.Margin = new Thickness(0, 7, 0, 2);
        panel.Children.Add(_autoReconnect);
        AddLabeled(panel, "接收结束符 (\\r\\n / \\n)", _terminator);

        var connectRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        connectRow.Children.Add(Button("连接", ConnectAsync));
        connectRow.Children.Add(Button("断开", DisconnectAsync));
        connectRow.Children.Add(Button("保存设备", SaveDeviceAsync));
        panel.Children.Add(connectRow);

        var sendRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var sendButton = Button("发送", SendAsync);
        DockPanel.SetDock(sendButton, Dock.Right);
        sendRow.Children.Add(sendButton);
        _sendText.Height = 28;
        sendRow.Children.Add(_sendText);
        panel.Children.Add(sendRow);
    }

    /// <summary>按协议类型显隐参数行：网络协议显示 IP/端口，串口显示串口参数。</summary>
    private void RefreshProtocolFields()
    {
        var isSerial = string.Equals(_protocol.SelectedItem as string, "串口", StringComparison.Ordinal);
        SetVisible([_host, _port], !isSerial);
        SetVisible([_serialPortName, _baudRate, _dataBits, _parity, _stopBits], isSerial);
    }

    private void SetVisible(IReadOnlyList<Control> controls, bool visible)
    {
        foreach (var control in controls)
        {
            if (_fields.TryGetValue(control, out var label))
            {
                label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AddLabeled(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        control.Height = 28;
        panel.Children.Add(control);
        _fields[control] = panel.Children[^2];
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 4, 10, 4) };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
                _log($"[通信管理] {text}失败: {ex.Message}");
            }
        };
        return button;
    }

    private void RefreshDeviceList(CommDevice? selected)
    {
        _deviceList.ItemsSource = null;
        _deviceList.ItemsSource = _devices;
        _deviceList.SelectedItem = selected is null ? null : _devices.FirstOrDefault(d => ReferenceEquals(d, selected));
        if (selected is null && _devices.Count > 0)
        {
            _deviceList.SelectedIndex = 0;
        }
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DisposeLink();
        LoadSelectedToFields();
    }

    private CommDevice? SelectedDevice => _deviceList.SelectedItem as CommDevice;

    private void DisposeLink()
    {
        _link?.Dispose();
        _link = null;
    }

    private void LoadSelectedToFields()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            _name.Text = "";
            _host.Text = "";
            _port.Text = "";
            _serialPortName.Text = "";
            _baudRate.Text = "";
            _dataBits.Text = "";
            _terminator.Text = "";
            return;
        }

        _name.Text = device.Name;
        _protocol.SelectedIndex = Math.Max(0, Array.IndexOf(Protocols, device.Protocol));
        _host.Text = device.Host;
        _port.Text = device.Port.ToString(CultureInfo.InvariantCulture);
        _serialPortName.Text = device.SerialPortName;
        _baudRate.Text = device.BaudRate.ToString(CultureInfo.InvariantCulture);
        _dataBits.Text = device.DataBits.ToString(CultureInfo.InvariantCulture);
        _parity.SelectedItem = DisplayParity(device.Parity);
        _stopBits.SelectedItem = DisplayStopBits(device.StopBits);
        _autoReconnect.IsChecked = device.AutoReconnect;
        _terminator.Text = device.Terminator;
        RefreshProtocolFields();
    }

    private void FillDeviceFromFields(CommDevice device)
    {
        device.Name = string.IsNullOrWhiteSpace(_name.Text) ? "未命名设备" : _name.Text.Trim();
        device.Protocol = _protocol.SelectedItem as string ?? "TCP客户端";
        device.Host = _host.Text.Trim();
        device.Port = (int)Number(_port.Text, 502);
        device.SerialPortName = string.IsNullOrWhiteSpace(_serialPortName.Text) ? "COM1" : _serialPortName.Text.Trim().ToUpperInvariant();
        device.BaudRate = (int)Number(_baudRate.Text, 9600);
        device.DataBits = (int)Number(_dataBits.Text, 8);
        device.Parity = StoredParity(_parity.SelectedItem as string);
        device.StopBits = StoredStopBits(_stopBits.SelectedItem as string);
        device.AutoReconnect = _autoReconnect.IsChecked == true;
        device.Terminator = _terminator.Text;
    }

    private Task AddDeviceAsync()
    {
        var device = new CommDevice { Name = $"设备{_devices.Count + 1}" };
        _devices.Add(device);
        _store?.Save(_devices);
        RefreshDeviceList(device);
        _status.Text = "已添加设备（默认 TCP客户端，保存前请填写参数）";
        return Task.CompletedTask;
    }

    private Task DeleteDeviceAsync()
    {
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择要删除的设备";
            return Task.CompletedTask;
        }

        DisposeLink();
        _devices.Remove(device);
        _store?.Save(_devices);
        RefreshDeviceList(null);
        _status.Text = $"已删除设备: {device.Name}";
        return Task.CompletedTask;
    }

    private Task SaveDeviceAsync()
    {
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择设备";
            return Task.CompletedTask;
        }

        FillDeviceFromFields(device);
        _store?.Save(_devices);
        RefreshDeviceList(device);
        _status.Text = "设备配置已保存";
        _log($"[通信管理] 设备已保存: {device.Name} ({DescribeDevice(device)})");
        return Task.CompletedTask;
    }

    private async Task ConnectAsync()
    {
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择设备";
            return;
        }

        FillDeviceFromFields(device);
        _store?.Save(_devices);
        if (string.Equals(device.Protocol, "ModBus通信", StringComparison.Ordinal))
        {
            _status.Text = "协议 ModBus通信 暂未支持（真实 PLC 协议待品牌确认）";
            return;
        }

        DisposeLink();
        var link = CommLinkFactory.Create(device);
        link.TextReceived = text => AppendReceive($"<< {text}");
        link.StatusChanged = text => AppendReceive($"-- {text}");
        link.LinkClosed = text => AppendReceive($"-- {text}");
        link.SetTerminator(device.Terminator);
        _link = link;
        try
        {
            await link.StartAsync(device);
        }
        catch
        {
            DisposeLink();
            throw;
        }

        _log($"[通信管理] {device.Name} 已启动（{DescribeDevice(device)}）");
    }

    private static string DescribeDevice(CommDevice device) => device.Protocol switch
    {
        "TCP服务端" => $"TCP服务端 监听:{device.Port}",
        "UDP" => $"UDP 本地:{device.Port} → {device.Host}:{device.Port}",
        "串口" => $"串口 {device.SerialPortName} @{device.BaudRate}",
        _ => $"TCP客户端 {device.Host}:{device.Port}",
    };

    private Task DisconnectAsync()
    {
        DisposeLink();
        _status.Text = "已断开";
        _log("[通信管理] 通信设备已断开");
        return Task.CompletedTask;
    }

    private async Task SendAsync()
    {
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择设备";
            return;
        }

        if (_link is null)
        {
            _status.Text = "通信设备未连接，请先点「连接」";
            return;
        }

        await _link.SendTextAsync(_sendText.Text);
        AppendReceive($">> {_sendText.Text}");
        _status.Text = $"已发送到 {device.Name}: {_sendText.Text}";
    }

    private void AppendReceive(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendReceive(line));
            return;
        }

        _receiveLog.Items.Add($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        while (_receiveLog.Items.Count > 200)
        {
            _receiveLog.Items.RemoveAt(0);
        }

        if (_receiveLog.Items.Count > 0)
        {
            _receiveLog.ScrollIntoView(_receiveLog.Items[^1]);
        }
    }

    /// <summary>校验位显示↔存储映射（comm.json 恒存 SDK 英文名，旧文件兼容）。</summary>
    private static string DisplayParity(string? stored) => stored?.Trim() switch
    {
        "Even" => "偶校验",
        "Odd" => "奇校验",
        _ => "无校验",
    };

    private static string StoredParity(string? display) => display switch
    {
        "偶校验" => "Even",
        "奇校验" => "Odd",
        _ => "None",
    };

    private static string DisplayStopBits(string? stored) => string.Equals(stored, "Two", StringComparison.OrdinalIgnoreCase) ? "2 位" : "1 位";

    private static string StoredStopBits(string? display) => string.Equals(display, "2 位", StringComparison.Ordinal) ? "Two" : "One";

    private static double Number(string? value, double fallback) => double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    protected override void OnClosed(EventArgs e)
    {
        DisposeLink();
        base.OnClosed(e);
    }
}
