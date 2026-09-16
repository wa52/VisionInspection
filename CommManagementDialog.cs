using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using VisionInspection.Comm;

namespace VisionInspection;

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
    /// <summary>生产通信运行时（发送数据/接收数据节点持有链路时，本弹窗拒绝再连同一设备）。</summary>
    private readonly ICommRuntime? _commRuntime;
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
    private readonly TextBlock _protocolHint = new();
    private readonly TextBlock _paramDeviceHeader = new();
    private readonly Dictionary<Control, UIElement> _fields = new();
    private List<CommDevice> _devices = [];
    private List<DeviceItem> _items = [];
    private readonly List<(CommDevice Device, ICommLink Link)> _connected = [];

    /// <summary>设备行条目：设备 + 连接开关状态（驱动开关勾选与「连接/断开」文案）。</summary>
    private sealed class DeviceItem : System.ComponentModel.INotifyPropertyChanged
    {
        public DeviceItem(CommDevice device) => Device = device;

        public CommDevice Device { get; }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsConnected)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SwitchText)));
            }
        }

        public string SwitchText => IsConnected ? "断开" : "连接";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>每设备独立链路（VM 式：多个设备可同时连接，切选中项不断开）。</summary>
    private ICommLink? LinkOf(CommDevice device) =>
        _connected.FirstOrDefault(c => ReferenceEquals(c.Device, device)).Link;

    private void DisconnectDevice(CommDevice device)
    {
        var index = _connected.FindIndex(c => ReferenceEquals(c.Device, device));
        if (index < 0)
        {
            return;
        }

        var link = _connected[index].Link;
        _connected.RemoveAt(index);
        link.Dispose();
        MarkConnected(device, false);
    }

    /// <summary>同步设备行开关显示（绑定翻转触发的 Checked/Unchecked 事件由守卫拦截，不会重复连断）。</summary>
    private void MarkConnected(CommDevice device, bool connected)
    {
        var item = _items.FirstOrDefault(i => ReferenceEquals(i.Device, device));
        if (item is not null)
        {
            item.IsConnected = connected;
        }
    }

    private void DisposeAllLinks()
    {
        foreach (var entry in _connected)
        {
            try { entry.Link.Dispose(); } catch { /* 退出清理，忽略单个链路释放异常 */ }
        }

        _connected.Clear();
    }

    /// <summary>后台线程回显连接状态到状态栏（WPF 跨线程禁止直接改控件，统一调度）。</summary>
    private void SetStatus(string text)
    {
        if (Dispatcher.CheckAccess())
        {
            _status.Text = text;
        }
        else
        {
            Dispatcher.BeginInvoke(() => _status.Text = text);
        }
    }

    public CommManagementDialog(Window owner, CommDeviceStore? store, Action<string> log, ICommRuntime? commRuntime = null)
    {
        Owner = owner;
        _store = store;
        _log = log;
        _commRuntime = commRuntime;
        Title = "通信管理";
        Width = 980;
        Height = 640;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)(Application.Current.TryFindResource("BgBrush") ?? Brushes.Black);
        Foreground = (Brush)(Application.Current.TryFindResource("TextBrush") ?? Brushes.White);
        Content = BuildContent();
        ApplyDarkListStyle(_deviceList, accentSelection: true);
        ApplyDarkListStyle(_receiveLog);
        BuildDeviceListTemplate();
        _devices = store?.Load() ?? [];
        RefreshDeviceList(_devices.Count > 0 ? _devices[0] : null);

        // 设备名称随输入实时写回选中设备（CommDevice.Name 发 PropertyChanged → 列表即时改名）
        _name.TextChanged += (_, _) =>
        {
            if (SelectedDevice is not { } device || string.IsNullOrWhiteSpace(_name.Text))
            {
                return;
            }

            if (!string.Equals(device.Name, _name.Text, StringComparison.Ordinal))
            {
                device.Name = _name.Text;
                UpdateParamDeviceHeader();
            }
        };
    }

    /// <summary>ListBox 默认白底白字在深色主题下不可读；统一面板底色 + SelBrush 选中高亮。</summary>
    private static void ApplyDarkListStyle(ListBox list, bool accentSelection = false)
    {
        list.Background = (Brush)(Application.Current.TryFindResource("PanelBrush") ?? Brushes.Transparent);
        list.Foreground = (Brush)(Application.Current.TryFindResource("TextBrush") ?? Brushes.White);
        list.BorderBrush = (Brush)(Application.Current.TryFindResource("BorderBrush") ?? Brushes.Gray);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, list.Foreground));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(4, 2, 4, 2)));
        itemStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        itemStyle.Setters.Add(new Setter(BorderBrushProperty, Brushes.Transparent));
        var selected = new System.Windows.Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, (Brush)(Application.Current.TryFindResource("SelBrush") ?? Brushes.Navy)));
        if (accentSelection)
        {
            // 设备列表：选中行加深蓝底 + 亮蓝描边，与未选中行明显区分（参数面板跟随此行设备）
            selected.Setters.Add(new Setter(BorderBrushProperty, (Brush)(Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue)));
        }

        itemStyle.Triggers.Add(selected);
        list.ItemContainerStyle = itemStyle;
    }

    /// <summary>设备行模板：连接开关（VM 式每设备开关，拨动即连/断该设备）+ 名称（协议类型）。</summary>
    private void BuildDeviceListTemplate()
    {
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 7, 0));
        toggle.SetValue(FrameworkElement.MinWidthProperty, 56.0);
        toggle.SetValue(FrameworkElement.HeightProperty, 22.0);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(DeviceItem.IsConnected)));
        toggle.SetBinding(ButtonBase.ContentProperty, new Binding(nameof(DeviceItem.SwitchText)));
        toggle.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(DeviceSwitch_Checked));
        toggle.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(DeviceSwitch_Unchecked));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(CommDevice.Display)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var textStyle = new Style(typeof(TextBlock));
        var boldWhenSelected = new DataTrigger
        {
            Binding = new Binding(nameof(ListBoxItem.IsSelected))
            {
                RelativeSource = new RelativeSource
                {
                    Mode = RelativeSourceMode.FindAncestor,
                    AncestorType = typeof(ListBoxItem),
                    AncestorLevel = 1,
                },
            },
            Value = true,
        };
        boldWhenSelected.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        textStyle.Triggers.Add(boldWhenSelected);
        text.SetValue(FrameworkElement.StyleProperty, textStyle);

        panel.AppendChild(toggle);
        panel.AppendChild(text);
        template.VisualTree = panel;
        _deviceList.ItemTemplate = template;
    }

    private async void DeviceSwitch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: DeviceItem item } || LinkOf(item.Device) is not null)
        {
            return; // 銶态同步引发的翻转（连接失败回弹等），非用户拨动
        }

        try
        {
            _deviceList.SelectedItem = item; // 参数面板跟随，连接读的是该设备参数
            await ConnectDeviceCoreAsync(item.Device);
        }
        catch (Exception ex)
        {
            SetStatus($"{item.Device.Name} 连接失败: {ex.Message}");
            _log($"[通信管理] {item.Device.Name} 连接失败: {ex.Message}");
        }
    }

    private void DeviceSwitch_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: DeviceItem item } || LinkOf(item.Device) is null)
        {
            return; // 状态同步引发的翻转，非用户主动断开
        }

        DisconnectDevice(item.Device);
        SetStatus($"{item.Device.Name} 已断开");
        _log($"[通信管理] 通信设备已断开: {item.Device.Name}");
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
        // 参数面板归属指示：这里编辑的是左侧列表选中行的设备（选中行有蓝底+描边+加粗标识）
        _paramDeviceHeader.FontWeight = FontWeights.Bold;
        _paramDeviceHeader.Foreground = (Brush)(Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue);
        _paramDeviceHeader.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(_paramDeviceHeader);
        AddLabeled(panel, "设备名称", _name);
        foreach (var protocol in Protocols)
        {
            _protocol.Items.Add(protocol);
        }

        _protocol.SelectionChanged += (_, _) => RefreshProtocolFields();
        AddLabeled(panel, "协议类型", _protocol);
        _protocolHint.Foreground = Brushes.Gray;
        _protocolHint.TextWrapping = TextWrapping.Wrap;
        _protocolHint.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(_protocolHint);
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
        RefreshProtocolFields();
    }

    /// <summary>按协议类型显隐参数行：TCP客户端/UDP 显示目标IP，TCP服务端只显示端口（绑定全部网卡），串口显示串口参数；并更新协议方向提示。</summary>
    private void RefreshProtocolFields()
    {
        var protocol = _protocol.SelectedItem as string;
        var isSerial = string.Equals(protocol, "串口", StringComparison.Ordinal);
        var showHost = string.Equals(protocol, "TCP客户端", StringComparison.Ordinal) || string.Equals(protocol, "UDP", StringComparison.Ordinal);
        SetVisible([_host], showHost);
        SetVisible([_port], !isSerial);
        SetVisible([_serialPortName, _baudRate, _dataBits, _parity, _stopBits], isSerial);
        _protocolHint.Text = protocol switch
        {
            "TCP客户端" => "本机作为客户端主动连接 目标IP:端口，对端须先开启服务端；要被对端接入请改选「TCP服务端」。",
            "TCP服务端" => "本机监听 端口（绑定 0.0.0.0 全部网卡，对端连本机任意 IP 均可接入），等待对端作为客户端接入。",
            "UDP" => "绑定本地 端口 收包；目标IP 为发送目标（留空=回发最近来包对端）。",
            "串口" => "打开本机串口收发（COM 名/波特率/校验等参数）。",
            null => "请选择协议类型。",
            _ => "暂未支持（真实 PLC 协议待品牌确认）。",
        };
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
        _items = _devices.Select(d => new DeviceItem(d) { IsConnected = LinkOf(d) is not null }).ToList();
        _deviceList.ItemsSource = null;
        _deviceList.ItemsSource = _items;
        _deviceList.SelectedItem = selected is null ? null : _items.FirstOrDefault(i => ReferenceEquals(i.Device, selected));
        if (selected is null && _items.Count > 0)
        {
            _deviceList.SelectedIndex = 0;
        }
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 链路按设备独立持有，切换选中项不断开连接
        LoadSelectedToFields();
    }

    private CommDevice? SelectedDevice => (_deviceList.SelectedItem as DeviceItem)?.Device;

    /// <summary>参数面板归属指示：当前编辑的是哪台设备（选中变更/实时改名都跟随刷新）。</summary>
    private void UpdateParamDeviceHeader()
    {
        var device = SelectedDevice;
        _paramDeviceHeader.Text = device is null ? "参数设备：（未选中）" : $"参数设备：{device.Display}";
    }

    private void LoadSelectedToFields()
    {
        var device = SelectedDevice;
        UpdateParamDeviceHeader();
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

        DisconnectDevice(device);
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
        UpdateParamDeviceHeader();
        _store?.Save(_devices);
        // 名称/参数经 PropertyChanged 与字段写回已实时呈现，这里只落盘；不重建列表（否则会误断活动链路）
        _status.Text = "设备配置已保存";
        _log($"[通信管理] 设备已保存: {device.Name} ({DescribeDevice(device)})");
        return Task.CompletedTask;
    }

    private Task ConnectAsync()
    {
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择设备";
            return Task.CompletedTask;
        }

        return ConnectDeviceCoreAsync(device);
    }

    /// <summary>连接指定设备（「连接」按钮与设备行开关共用）：重复点连接=按新参数重连。</summary>
    private async Task ConnectDeviceCoreAsync(CommDevice device)
    {
        FillDeviceFromFields(device);
        UpdateParamDeviceHeader();
        _store?.Save(_devices);
        if (string.Equals(device.Protocol, "ModBus通信", StringComparison.Ordinal))
        {
            _status.Text = "协议 ModBus通信 暂未支持（真实 PLC 协议待品牌确认）";
            return;
        }

        if (string.Equals(device.Protocol, "TCP客户端", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(device.Host))
        {
            _status.Text = "目标IP 不能为空（TCP客户端 需指定对端 IP；要被对端接入请改选「TCP服务端」）";
            return;
        }

        if (_commRuntime?.IsHeld(device.Name) == true)
        {
            _status.Text = $"设备「{device.Name}」正被方案的发送/接收数据节点占用（生产链路），请先停用相关节点";
            _log($"[通信管理] 拒绝连接 {device.Name}: 设备被方案的发送/接收数据节点占用");
            return;
        }

        DisconnectDevice(device);
        var link = CommLinkFactory.Create(device);
        link.TextReceived = text => AppendReceive($"<< {text}");
        link.StatusChanged = text =>
        {
            AppendReceive($"-- {text}");
            SetStatus($"{device.Name}: {text}");
        };
        link.LinkClosed = text =>
        {
            AppendReceive($"-- {text}");
            SetStatus($"{device.Name}: {text}");
        };
        link.SetTerminator(device.Terminator);
        SetStatus($"{device.Name}（{DescribeDevice(device)}）连接中…");
        try
        {
            await link.StartAsync(device);
        }
        catch (Exception ex)
        {
            link.Dispose();
            SetStatus($"{device.Name} 连接失败: {ex.Message}");
            throw;
        }

        _connected.Add((device, link));
        MarkConnected(device, true);
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
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择设备";
            return Task.CompletedTask;
        }

        DisconnectDevice(device);
        _status.Text = $"{device.Name} 已断开";
        _log($"[通信管理] 通信设备已断开: {device.Name}");
        return Task.CompletedTask;
    }

    private async Task SendAsync()
    {
        if (SelectedDevice is not { } device)
        {
            _status.Text = "请先选择设备";
            return;
        }

        var link = LinkOf(device);
        if (link is null)
        {
            _status.Text = $"{device.Name} 未连接，请先打开设备行「连接」开关";
            return;
        }

        await link.SendTextAsync(_sendText.Text);
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
        DisposeAllLinks();
        base.OnClosed(e);
    }
}
