using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SpeakerVisionInspection;

/// <summary>
/// 模块结果视图（海康式「模块结果」面板，代码构建、深色主题）：
/// 上半部 = 结果树（结果名称 | 当前值：模块状态/耗时/判定 + 各输出值），
/// 下半部 = 「当前结果」「历史结果」两个页签的表格。
/// 纯展示控件：数据由 MainWindow 注入（每轮流水线结果落地时刷新），不持有业务状态。
/// </summary>
public sealed class ModuleResultView : Grid
{
    /// <summary>输出键 → 中文结果名；未收录的键（含 roi_/match_ 前缀派生）按规则翻译，其余原样显示。</summary>
    private static readonly Dictionary<string, string> LabelMap = new()
    {
        ["decision"] = "判定",
        ["elapsed_ms"] = "耗时(ms)",
        ["score"] = "分数",
        ["threshold"] = "阈值",
        ["count"] = "数量",
        ["det_all"] = "检出总数",
        ["classes"] = "类别",
        ["max_conf"] = "最高置信度",
        ["max_ratio"] = "最大占比",
        ["defect_pixels"] = "缺陷像素",
        ["instances"] = "实例数",
        ["matches"] = "匹配个数",
        ["trigger_key"] = "触发键位",
        ["x"] = "匹配点X",
        ["y"] = "匹配点Y",
        ["angle"] = "角度",
        ["loc_x"] = "定位点X",
        ["loc_y"] = "定位点Y",
        ["loc_angle"] = "定位角度",
        ["loc_valid"] = "定位有效",
        ["source_decision"] = "来源判定",
        ["emitted"] = "已输出脉冲",
        ["duration_ms"] = "脉冲宽度(ms)",
        ["out_w"] = "图像宽度",
        ["out_h"] = "图像高度",
        ["current_file"] = "当前图像",
        ["error"] = "错误",
    };

    private static readonly string[] TreeFirstKeys = ["decision", "elapsed_ms"];

    private readonly Grid _treeGrid = new()
    {
        ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(118) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
        Margin = new Thickness(0, 2, 0, 6),
    };

    private readonly DataGrid _currentGrid = NewResultGrid();
    private readonly DataGrid _historyGrid = NewResultGrid();

    public ModuleResultView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new TabControl { Height = 188 };
        tabs.Items.Add(new TabItem { Header = "当前结果", Content = new ScrollViewer { Content = _currentGrid } });
        tabs.Items.Add(new TabItem { Header = "历史结果", Content = new ScrollViewer { Content = _historyGrid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } });
        SetRow(_treeGrid, 0);
        SetRow(tabs, 1);
        Children.Add(_treeGrid);
        Children.Add(tabs);
        Update(null, Array.Empty<ModuleRunRecord>());
    }

    /// <summary>刷新：current=本模块最近一次执行记录；history=历史（旧→新，表格倒序显示）。</summary>
    public void Update(ModuleRunRecord? current, IReadOnlyList<ModuleRunRecord> history)
    {
        RebuildTree(current);
        RebuildCurrentGrid(current);
        RebuildHistoryGrid(history);
    }

    // ===== 结果树 =====

    private void RebuildTree(ModuleRunRecord? current)
    {
        _treeGrid.Children.Clear();
        _treeGrid.RowDefinitions.Clear();
        if (current is null)
        {
            AddTreeRow(0, "尚未执行", "单次执行/连续执行后显示本模块结果", mutedName: true, mutedValue: true);
            return;
        }

        var values = current.Values;
        var hasError = values.ContainsKey("error")
            || (values.TryGetValue("decision", out var d) && d.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        AddTreeHeaderRow();
        var row = 1;
        AddTreeRow(row++, "模块状态", hasError ? "0（执行异常）" : "1（执行正常）", valueBrush: hasError ? ResourceBrush("NgBrush") : ResourceBrush("OkBrush"));
        if (values.TryGetValue("elapsed_ms", out var elapsed))
        {
            AddTreeRow(row++, "耗时(ms)", elapsed);
        }
        foreach (var kv in EnumerateTreeValues(values))
        {
            var brush = kv.Key == "decision"
                ? kv.Value switch
                {
                    "OK" => ResourceBrush("OkBrush"),
                    "NG" => ResourceBrush("NgBrush"),
                    _ => ResourceBrush("NgBrush"),
                }
                : kv.Key == "error" ? ResourceBrush("NgBrush") : null;
            AddTreeRow(row++, DisplayName(kv.Key), kv.Value, valueBrush: brush);
        }
    }

    /// <summary>树行顺序：判定/耗时（已在表头行单独处理的除外）→ 其余按键插入顺序。</summary>
    private static IEnumerable<KeyValuePair<string, string>> EnumerateTreeValues(IReadOnlyDictionary<string, string> values) =>
        TreeFirstKeys.Where(values.ContainsKey).Select(k => new KeyValuePair<string, string>(k, values[k]))
            .Concat(values.Where(kv => !TreeFirstKeys.Contains(kv.Key)));

    private void AddTreeHeaderRow()
    {
        var row = _treeGrid.RowDefinitions.Count;
        _treeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = "结果名称", FontSize = 11, Foreground = ResourceBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 6, 2) };
        var value = new TextBlock { Text = "当前值", FontSize = 11, Foreground = ResourceBrush("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 2) };
        Grid.SetRow(name, row);
        Grid.SetColumn(name, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        _treeGrid.Children.Add(name);
        _treeGrid.Children.Add(value);
    }

    private void AddTreeRow(int row, string name, string value, bool mutedName = false, bool mutedValue = false, Brush? valueBrush = null)
    {
        while (_treeGrid.RowDefinitions.Count <= row)
        {
            _treeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 6, 1),
            Foreground = mutedName ? ResourceBrush("MutedTextBrush") : ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 1),
            Foreground = mutedValue ? ResourceBrush("MutedTextBrush") : valueBrush ?? ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(nameText, row);
        Grid.SetColumn(nameText, 0);
        Grid.SetRow(valueText, row);
        Grid.SetColumn(valueText, 1);
        _treeGrid.Children.Add(nameText);
        _treeGrid.Children.Add(valueText);
    }

    // ===== 当前结果 / 历史结果 =====

    private void RebuildCurrentGrid(ModuleRunRecord? current)
    {
        _currentGrid.Columns.Clear();
        _currentGrid.Items.Clear();
        _currentGrid.Columns.Add(SeqColumn("序号"));
        if (current is null) return;
        foreach (var kv in current.Values)
        {
            _currentGrid.Columns.Add(new DataGridTextColumn
            {
                Header = DisplayName(kv.Key),
                Binding = new Binding($"[{kv.Key}]"),
                Width = new DataGridLength(96),
            });
        }
        _currentGrid.Items.Add(current.Values);
    }

    private void RebuildHistoryGrid(IReadOnlyList<ModuleRunRecord> history)
    {
        _historyGrid.Columns.Clear();
        _historyGrid.Items.Clear();
        _historyGrid.Columns.Add(SeqColumn("执行序号"));
        _historyGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "时间",
            Binding = new Binding("Time") { StringFormat = "HH:mm:ss.fff" },
            Width = new DataGridLength(96),
        });
        _historyGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "模块数据",
            Binding = new Binding("Summary"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        for (var i = history.Count - 1; i >= 0; i--)
        {
            _historyGrid.Items.Add(history[i]);
        }
    }

    private static DataGridTextColumn SeqColumn(string header) => new()
    {
        Header = header,
        Binding = new Binding("Seq"),
        Width = new DataGridLength(56),
    };

    // ===== 键名翻译与通用构建 =====

    /// <summary>输出键 → 展示名：收录键用中文，roi_{名}→检测项 名，match_{i}_{字段}→匹配{i}·字段，其余原样。</summary>
    public static string DisplayName(string key)
    {
        if (LabelMap.TryGetValue(key, out var label)) return label;
        if (key.StartsWith("roi_", StringComparison.Ordinal)) return "检测项 " + key[4..];
        if (key.StartsWith("match_", StringComparison.Ordinal))
        {
            var rest = key[6..];
            var sep = rest.IndexOf('_');
            if (sep > 0 && int.TryParse(rest[..sep], out var i))
            {
                var field = rest[(sep + 1)..];
                var fieldName = field switch
                {
                    "x" => "X",
                    "y" => "Y",
                    "angle" => "角度",
                    "score" => "分数",
                    _ => field,
                };
                return $"匹配{i}·{fieldName}";
            }
        }
        return key;
    }

    private static DataGrid NewResultGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        SelectionMode = DataGridSelectionMode.Single,
        ColumnWidth = new DataGridLength(96),
        FontSize = 11.5,
    };

    private static Brush ResourceBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
