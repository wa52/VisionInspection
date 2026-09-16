using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionInspection;

/// <summary>
/// 主窗口的可观察状态。检测、设备和 ROI 逻辑仍由现有应用服务负责，ViewModel 只承载可绑定的界面状态。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string productionState = "空闲";

    [ObservableProperty]
    private string productionDetail = "-";

    [ObservableProperty]
    private string cameraStatus = "相机: 未连接";

    [ObservableProperty]
    private string triggerStatus = "触发: -";

    [ObservableProperty]
    private string fpsStatus = "FPS: -";

    [ObservableProperty]
    private string finalDecision = "待机";
}
