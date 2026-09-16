namespace VisionInspection.Comm;

/// <summary>通信设备配置（VM 通信管理式设备条目）。</summary>
public sealed record CommDevice : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "PLC";
    private string _protocol = "TCP客户端";

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>协议类型：TCP客户端 / TCP服务端 / UDP / 串口 可用，ModBus通信 为占位。</summary>
    public string Protocol
    {
        get => _protocol;
        set
        {
            _protocol = value;
            OnPropertyChanged(nameof(Protocol));
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>通信管理设备列表显示文本：名称（协议类型）。Name/Protocol 任一变更即发通知实时刷新。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => $"{Name}（{Protocol}）";

    /// <summary>名称/协议变更通知（设备列表 DisplayMemberPath 绑定消费）。</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    /// <summary>TCP客户端/UDP：目标IP；TCP服务端不使用（绑定本机全部网卡）。</summary>
    public string Host { get; set; } = "192.168.0.1";

    /// <summary>TCP客户端/服务端：目标/监听端口；UDP：本地绑定端口。</summary>
    public int Port { get; set; } = 502;

    public bool AutoReconnect { get; set; } = true;

    /// <summary>接收结束符（转义形式，如 \r\n）；空 = 按收包块显示。</summary>
    public string Terminator { get; set; } = "\\r\\n";

    /// <summary>串口参数（协议=串口 时使用）。</summary>
    public string SerialPortName { get; set; } = "COM1";

    public int BaudRate { get; set; } = 9600;

    public int DataBits { get; set; } = 8;

    /// <summary>校验位：None / Even / Odd。</summary>
    public string Parity { get; set; } = "None";

    /// <summary>停止位：One / Two。</summary>
    public string StopBits { get; set; } = "One";
}
