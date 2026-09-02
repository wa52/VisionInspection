using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection;

/// <summary>
/// 检测项管理弹窗（表格化）：一行一个检测项，名称/启用/判断/阈值/存图/总范围直接在单元格编辑，
/// 几何列只读（几何编辑在主图像叠加层：选中→拖拽/缩放/旋转，右键删除，「重绘选中项」重画）。
/// </summary>
public sealed class RoiManagerDialog : Window
{
    /// <summary>表格行（与 own_rois 条目索引一一对应）。</summary>
    public sealed class RoiRow
    {
        public int Index { get; init; }
        public string No { get; init; } = "";
        public string Name { get; set; } = "";
        public string Geometry { get; init; } = "";
        public bool Enabled { get; set; }
        public string Judge { get; set; } = "参与判定";
        public string Threshold { get; set; } = "";
        public bool SaveImage { get; set; }
        public bool Scope { get; set; }
    }

    private readonly MainWindow _main;
    private readonly RecipeNode _hostNode;
    private readonly NodeParamDialog? _hostDialog;
    private readonly DataGrid _grid;
    private readonly TextBlock _hint;
    private bool _loading;

    public RoiManagerDialog(MainWindow main, RecipeNode hostNode, NodeParamDialog? hostDialog)
    {
        _main = main;
        _hostNode = hostNode;
        _hostDialog = hostDialog;

        Title = $"检测项管理 — {hostNode.Name}（独属本节点）";
        Width = 800;
        Height = 540;
        MinWidth = 660;
        MinHeight = 380;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = main.Left + main.ActualWidth - Width - 16;
        Top = Math.Max(0, main.Top + 70);
        Background = (Brush)Application.Current.Resources["PanelBrush"];

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = "本节点检测项（一个检测项 = 一个检测区域；独属本节点；允许重名）",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });

        _grid = BuildGrid();
        _grid.SelectionChanged += (_, _) =>
        {
            _main.SetManagerHighlight(_hostNode, _grid.SelectedIndex);
        };
        _grid.CellEditEnding += Grid_CellEditEnding;
        panel.Children.Add(_grid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(new Button
        {
            Content = "新建检测项",
            Width = 84,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0),
        }.Apply(b => b.Click += (_, _) => { AddNew(); ScheduleRefresh(); }));
        buttons.Children.Add(new Button
        {
            Content = "重绘选中项",
            Width = 84,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "进入绘制模式：在主图像区画一个新框，替换选中检测项的位置（名字与参数保留，画完自动退出）",
        }.Apply(b => b.Click += (_, _) => { RedrawSelected(); }));
        buttons.Children.Add(new Button
        {
            Content = "删除",
            Width = 60,
            Height = 26,
        }.Apply(b => b.Click += (_, _) => { DeleteSelected(); ScheduleRefresh(); }));
        panel.Children.Add(buttons);

        _hint = new TextBlock
        {
            Text = "提示：表格里直接改名称/启用/判断/阈值/存图；「总范围」勾选后其余检测项只保留落在范围内的结果。主图像区点框选中（第一下只选中，再按住拖动），右键删除。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
        };
        panel.Children.Add(_hint);

        Content = panel;
        RefreshData();
        Activated += (_, _) => RefreshData();
    }

    private List<RoiItem> OwnItemsFull =>
        NodeRois.ParseOwnFull(_hostNode.Params.GetValueOrDefault("own_rois"));

    private DataGrid BuildGrid()
    {
        var textBrush = Application.Current.TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var grid = new DataGrid
        {
            Height = 250,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Background = Brushes.Transparent,
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = Brushes.Transparent,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = (Brush)Application.Current.Resources["HoverBrush"],
            Foreground = textBrush,
            MinRowHeight = 26,
        };
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, (Brush)Application.Current.Resources["HoverBrush"]));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        grid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
        cellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        grid.CellStyle = cellStyle;

        // # 序号
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new System.Windows.Data.Binding(nameof(RoiRow.No)),
            IsReadOnly = true,
            Width = new DataGridLength(40),
        });
        // 名称（允许重名，单元格直接改）
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "名称",
            Binding = new System.Windows.Data.Binding(nameof(RoiRow.Name)) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
            Width = new DataGridLength(96),
        });
        // 几何（只读；编辑走主图像叠加层/重绘）
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "几何(cx,cy,w,h,角度)",
            Binding = new System.Windows.Data.Binding(nameof(RoiRow.Geometry)),
            IsReadOnly = true,
            Width = new DataGridLength(230),
        });
        // 启用
        grid.Columns.Add(CheckColumn("启用", 52, (sender, e) => OnCheckChanged(sender, e, row =>
        {
            row.Enabled = true;
            WriteMeta(row);
        }), (sender, e) => OnCheckChanged(sender, e, row =>
        {
            row.Enabled = false;
            WriteMeta(row);
        })));
        // 判断
        var judgeCol = new DataGridTemplateColumn { Header = "判断", Width = new DataGridLength(84) };
        var judgeFactory = new FrameworkElementFactory(typeof(ComboBox));
        judgeFactory.SetValue(ComboBox.ItemsSourceProperty, new[] { "参与判定", "仅观察" });
        judgeFactory.SetValue(ComboBox.HeightProperty, 22.0);
        judgeFactory.AddHandler(ComboBox.LoadedEvent, (RoutedEventHandler)((s, _) =>
        {
            if (s is ComboBox cb && cb.DataContext is RoiRow row)
            {
                cb.SelectedIndex = row.Judge == "仅观察" ? 1 : 0;
            }
        }));
        judgeFactory.AddHandler(ComboBox.SelectionChangedEvent, (SelectionChangedEventHandler)((s, _) =>
        {
            if (s is ComboBox cb && cb.DataContext is RoiRow row && !_loading)
            {
                row.Judge = cb.SelectedItem?.ToString() ?? "参与判定";
                WriteMeta(row);
            }
        }));
        judgeCol.CellTemplate = new DataTemplate { VisualTree = judgeFactory };
        grid.Columns.Add(judgeCol);
        // 阈值（检测项级）
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "阈值",
            Binding = new System.Windows.Data.Binding(nameof(RoiRow.Threshold)) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
            Width = new DataGridLength(64),
        });
        // 存图
        grid.Columns.Add(CheckColumn("存图", 52, (sender, e) => OnCheckChanged(sender, e, row =>
        {
            row.SaveImage = true;
            WriteMeta(row);
        }), (sender, e) => OnCheckChanged(sender, e, row =>
        {
            row.SaveImage = false;
            WriteMeta(row);
        })));
        // 总范围
        grid.Columns.Add(CheckColumn("总范围", 60, (sender, e) => OnCheckChanged(sender, e, row =>
        {
            var scope = _main.GetScopeIndex(_hostNode);
            _main.SetScopeIndex(_hostNode, scope == row.Index ? -1 : row.Index);
            ScheduleRefresh();
        }), (sender, e) => OnCheckChanged(sender, e, row =>
        {
            if (_main.GetScopeIndex(_hostNode) == row.Index)
            {
                _main.SetScopeIndex(_hostNode, -1);
                ScheduleRefresh();
            }
        })));
        return grid;
    }

    /// <summary>复选框列：模板内 CheckBox 居中，勾选事件带行上下文。</summary>
    private DataGridTemplateColumn CheckColumn(string header, double width, RoutedEventHandler onChecked, RoutedEventHandler onUnchecked)
    {
        var col = new DataGridTemplateColumn { Header = header, Width = new DataGridLength(width) };
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        factory.SetValue(FocusableProperty, false);
        factory.AddHandler(CheckBox.CheckedEvent, onChecked);
        factory.AddHandler(CheckBox.UncheckedEvent, onUnchecked);
        col.CellTemplate = new DataTemplate { VisualTree = factory };
        return col;
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e, Action<RoiRow> apply)
    {
        if (_loading) return;
        if (sender is CheckBox cb && cb.DataContext is RoiRow row)
        {
            apply(row);
        }
    }

    /// <summary>文本列编辑结束：名称 → 重命名（保元数据）；阈值 → 写回元数据。</summary>
    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_loading || e.Row.Item is not RoiRow row) return;
        var header = e.Column.Header?.ToString();
        var newText = (e.EditingElement as TextBox)?.Text?.Trim() ?? "";
        if (header == "名称")
        {
            if (newText.Length == 0 || newText == row.Name) return;
            row.Name = newText;
            _main.RenameOwnRoi(_hostNode, row.Index, newText);
            ScheduleRefresh();
        }
        else if (header == "阈值")
        {
            if (newText == row.Threshold) return;
            if (newText.Length > 0 && !double.TryParse(newText, out _))
            {
                AppendHint($"阈值「{newText}」不是数值，已忽略");
                ScheduleRefresh();
                return;
            }
            row.Threshold = newText;
            WriteMeta(row);
        }
    }

    private void WriteMeta(RoiRow row)
    {
        _main.SetRoiMeta(_hostNode, row.Index, new RoiMeta(
            Enabled: row.Enabled,
            Judge: row.Judge == "仅观察" ? "仅观察" : "",
            Threshold: row.Threshold.Trim(),
            SaveImage: row.SaveImage));
        ScheduleRefresh();
    }

    private void ScheduleRefresh() =>
        Dispatcher.BeginInvoke(RefreshData, System.Windows.Threading.DispatcherPriority.Background);

    private void RefreshData()
    {
        if (_loading) return;
        var selected = _grid.SelectedIndex;
        _loading = true;
        try
        {
            var items = OwnItemsFull;
            var scope = _main.GetScopeIndex(_hostNode);
            _grid.ItemsSource = items.Select((it, i) => new RoiRow
            {
                Index = i,
                No = $"[{i + 1}]",
                Name = it.Name,
                Geometry = it.Rect.Serialize(),
                Enabled = it.Meta.Enabled,
                Judge = NodeRois.Judges(it.Meta) ? "参与判定" : "仅观察",
                Threshold = it.Meta.Threshold,
                SaveImage = it.Meta.SaveImage,
                Scope = scope == i,
            }).ToList();
            _grid.SelectedIndex = selected >= 0 && selected < items.Count ? selected : -1;
        }
        finally
        {
            _loading = false;
        }
    }

    private void AddNew()
    {
        _main.AddOwnRoi(_hostNode);
    }

    private void RedrawSelected()
    {
        var index = _grid.SelectedIndex;
        var own = OwnItemsFull;
        if (index < 0 || index >= own.Count)
        {
            AppendHint("请先在表格中选择一个检测项");
            return;
        }
        _main.StartRoiRedraw(_hostNode, index, _hostDialog);
        AppendHint($"重绘模式：在主图像区画一个新框，替换「{own[index].Name}」的位置（画完自动退出）");
    }

    private void DeleteSelected()
    {
        var index = _grid.SelectedIndex;
        var own = OwnItemsFull;
        if (index < 0 || index >= own.Count)
        {
            AppendHint("请先在表格中选择一个检测项");
            return;
        }
        _main.DeleteOwnRoi(_hostNode, index);
    }

    private void AppendHint(string text) => _hint.Text = text;

    /// <summary>主图像叠加层点选检测项 → 表格同步选中（MainWindow 回调）。</summary>
    internal void SelectRoiIndex(int index)
    {
        if (index < 0 || index >= _grid.Items.Count || _grid.SelectedIndex == index)
        {
            return;
        }
        _grid.SelectedIndex = index;
        if (_grid.SelectedItem is { } item)
        {
            _grid.ScrollIntoView(item);
        }
    }

    /// <summary>own_rois 被外部（图像拖拽/重绘/删除/新建）修改后由 MainWindow 调用刷新。</summary>
    internal void RefreshExternal() => RefreshData();
}
