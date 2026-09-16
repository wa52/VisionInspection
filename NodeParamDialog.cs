using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisionInspection.Detection;
using VisionInspection.Models;

namespace VisionInspection;

/// <summary>
/// 节点参数弹窗（非模态、单实例）：每个节点的参数内容弹出独立窗口编辑。
/// PatchCore 节点内含「绘制 ROI」图标按钮（悬停右下角显示功能提示），
/// 绘制始终作用于本弹窗对应的节点，与左侧树的选中状态无关。
/// </summary>
public sealed class NodeParamDialog : Window
{
    private readonly MainWindow _main;
    private readonly StackPanel _root;
    private Button? _drawRoiButton;
    private TextBlock? _roiStatus;

    public RecipeNode Node { get; }

    public NodeParamDialog(MainWindow main, RecipeNode rn)
    {
        _main = main;
        Node = rn;

        Title = $"节点参数 - {rn.Name}";
        Width = 380;
        Height = 660;
        MinWidth = 320;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // 贴着主窗口右侧显示（覆盖原参数页位置附近）
        Left = main.Left + main.ActualWidth - Width - 16;
        Top = Math.Max(0, main.Top + 70);
        Background = (Brush)Application.Current.Resources["PanelBrush"];

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(2),
        };
        _root = new StackPanel { Margin = new Thickness(8) };
        scroll.Content = _root;
        Content = scroll;

        Rebuild();
        Closed += (_, _) => _main.OnParamDialogClosed(this);
    }

    /// <summary>重建内容（节点重命名/结构变化/ROI 变化后调用）。</summary>
    internal void Rebuild()
    {
        Title = $"节点参数 - {Node.Name}";
        _root.Children.Clear();
        _main.BuildNodeEditor(Node, _root, this);
    }

    /// <summary>由 BuildNodeEditor（弹窗模式）注册 ROI 控件引用。</summary>
    internal void RegisterRoiControls(Button drawRoiButton, TextBlock roiStatus)
    {
        _drawRoiButton = drawRoiButton;
        _roiStatus = roiStatus;
        SyncRoiUi();
    }

    /// <summary>绘制模式开/关后刷新按钮高亮与状态文字。</summary>
    internal void NotifyRoiDrawChanged() => SyncRoiUi();

    /// <summary>ROI 参数被图像区写入/删除后刷新（重建以同步 roi 文本框与按钮可用性）。</summary>
    internal void NotifyRoiChanged() => Rebuild();

    private void SyncRoiUi()
    {
        if (_drawRoiButton is null || _roiStatus is null) return;
        var active = _main.IsRoiDrawActive(Node);
        _drawRoiButton.Background = (Brush)(Application.Current.TryFindResource(active ? "SelBrush" : "HoverBrush") ?? Brushes.Transparent);
        if (active)
        {
            _roiStatus.Text = "绘制中：在中间图像区拖拽画框（画新框=只进本节点私有集，可多框），画完再点一次绘制按钮退出";
            return;
        }
        var own = NodeRois.ParseOwn(Node.Params.GetValueOrDefault("own_rois"))
            .Select(r => r.Name).ToList();
        _roiStatus.Text = own.Count > 0
            ? $"本节点 ROI: {string.Join("、", own)}（私有，仅作用于本节点；在主图像叠加层选中后可移动/缩放/旋转，右键删除）"
            : "本节点暂无 ROI（全图检测；点绘制按钮画框）";
    }
}
