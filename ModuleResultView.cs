using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace VisionInspection;

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
        ["text"] = "文本",
        ["conf"] = "置信度",
        ["det_all"] = "检出总数",
        ["classes"] = "类别",
        ["max_conf"] = "最高置信度",
        ["max_ratio"] = "最大占比",
        ["defect_pixels"] = "缺陷像素",
        ["total_area"] = "总面积",
        ["max_area"] = "最大面积",
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
        ["x1"] = "起点X",
        ["y1"] = "起点Y",
        ["x2"] = "终点X",
        ["y2"] = "终点Y",
        ["mid_x"] = "中点X",
        ["mid_y"] = "中点Y",
        ["center_x"] = "圆心X",
        ["center_y"] = "圆心Y",
        ["radius"] = "半径",
        ["mean_contrast"] = "平均对比度",
        ["source_decision"] = "来源判定",
        ["emitted"] = "已输出脉冲",
        ["duration_ms"] = "脉冲宽度(ms)",
        ["sent"] = "已发送",
        ["resolved_text"] = "发送内容",
        ["received"] = "已收到",
        ["device"] = "通信设备",
        ["timeout_ms"] = "接收超时(ms)",
        ["abs_dist"] = "绝对距离",
        ["inter_x"] = "交点X",
        ["inter_y"] = "交点Y",
        ["line1_angle"] = "直线1角度",
        ["line2_angle"] = "直线2角度",
        ["line1_x1"] = "直线1起点X",
        ["line1_y1"] = "直线1起点Y",
        ["line1_x2"] = "直线1终点X",
        ["line1_y2"] = "直线1终点Y",
        ["line2_x1"] = "直线2起点X",
        ["line2_y1"] = "直线2起点Y",
        ["line2_x2"] = "直线2终点X",
        ["line2_y2"] = "直线2终点Y",
        ["dist"] = "距离",
        ["foot_x"] = "垂足X",
        ["foot_y"] = "垂足Y",
        ["inter1_x"] = "交点1X",
        ["inter1_y"] = "交点1Y",
        ["inter2_x"] = "交点2X",
        ["inter2_y"] = "交点2Y",
        ["line_angle"] = "测量直线角度",
        ["relation"] = "位置关系",
        ["c1_x"] = "圆1圆心X",
        ["c1_y"] = "圆1圆心Y",
        ["c1_r"] = "圆1半径",
        ["c2_x"] = "圆2圆心X",
        ["c2_y"] = "圆2圆心Y",
        ["c2_r"] = "圆2半径",
        ["center_dist"] = "中心距离",
        ["closest_dist"] = "最近距离",
        ["farthest_dist"] = "最远距离",
        ["point_x"] = "测量点X",
        ["point_y"] = "测量点Y",
        ["circle_x"] = "圆心X",
        ["circle_y"] = "圆心Y",
        ["fail_checks"] = "未过判定项",
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
        // DataGrid 自带滚动条——外层不能再套 ScrollViewer（横向滚动会给 Grid 无限宽度度量，Star 列塌缩、列头互叠）
        var tabs = new TabControl { MinHeight = 188 };
        tabs.Items.Add(new TabItem { Header = "当前结果", Content = _currentGrid });
        tabs.Items.Add(new TabItem { Header = "历史结果", Content = _historyGrid });
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
            MinWidth = 120,
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
        if (key.StartsWith("roi_", StringComparison.Ordinal))
        {
            // roi_{名} → 检测项 名；roi_{名}_text/conf/match → 检测项 名·识别文本/置信度/匹配（字符识别节点）
            var rest = key[4..];
            var sep = rest.LastIndexOf('_');
            if (sep > 0)
            {
                var field = rest[(sep + 1)..];
                var fieldName = field switch
                {
                    "text" => "识别文本",
                    "conf" => "置信度",
                    "match" => "匹配",
                    "area" => "最大面积",
                    "total" => "总面积",
                    _ => null,
                };
                if (fieldName != null) return $"检测项 {rest[..sep]}·{fieldName}";
            }
            return "检测项 " + rest;
        }
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
