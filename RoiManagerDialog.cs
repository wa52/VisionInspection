using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection;

/// <summary>
/// 检测项管理弹窗（表格化）：一行一个检测项，名称/启用/判断/阈值/存图/总范围直接在单元格编辑，
/// 几何列只读（几何编辑在主图像叠加层：选中→拖拽/缩放/旋转，右键删除，「重绘选中项」重画）。
/// 单元格全部走数据绑定（行模型实现 INotifyPropertyChanged），用户编辑 → 行属性变化 → 写回节点参数；
/// 不再用 Loaded/Checked 事件手工同步（虚拟化行复用时会显示旧状态、且勾选后整表重建导致勾选"弹回"）。
/// </summary>
public sealed class RoiManagerDialog : Window
{
	/// <summary>表格行（与 own_rois 条目索引一一对应；可编辑属性双向绑定 + 通知）。</summary>
	public sealed class RoiRow : INotifyPropertyChanged
	{
		public int Index { get; init; }
		public string No { get; init; } = "";
		public string Geometry { get; init; } = "";

		private string _name = "";
		public string Name { get => _name; set => Set(ref _name, value); }

		private bool _enabled;
		public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

		/// <summary>0=参与判定，1=仅观察（ComboBox.SelectedIndex 直绑）。</summary>
		private int _judgeIndex;
		public int JudgeIndex { get => _judgeIndex; set => Set(ref _judgeIndex, value); }

		private string _threshold = "";
		public string Threshold { get => _threshold; set => Set(ref _threshold, value); }

		private bool _saveImage;
		public bool SaveImage { get => _saveImage; set => Set(ref _saveImage, value); }

		private bool _scope;
		public bool Scope { get => _scope; set => Set(ref _scope, value); }

		public event PropertyChangedEventHandler? PropertyChanged;

		private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
		{
			if (!EqualityComparer<T>.Default.Equals(field, value))
			{
				field = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
			}
		}
	}

	private readonly MainWindow _main;
	private readonly RecipeNode _hostNode;
	private readonly NodeParamDialog? _hostDialog;
	private readonly DataGrid _grid;
	private readonly TextBlock _hint;
	private bool _loading;
	private bool _reverting; // 回滚单元格显示（非法输入/空名恢复旧值）时不触发写回

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
			Binding = new Binding(nameof(RoiRow.No)),
			IsReadOnly = true,
			Width = new DataGridLength(40),
		});
		// 名称（允许重名，单元格直接改；常驻 TextBox，点一下即可改，失焦提交）
		grid.Columns.Add(TextColumn("名称", nameof(RoiRow.Name), 96));
		// 几何（只读；编辑走主图像叠加层/重绘）
		grid.Columns.Add(new DataGridTextColumn
		{
			Header = "几何(cx,cy,w,h,角度)",
			Binding = new Binding(nameof(RoiRow.Geometry)),
			IsReadOnly = true,
			Width = new DataGridLength(230),
		});
		// 启用
		grid.Columns.Add(CheckColumn("启用", 52, nameof(RoiRow.Enabled)));
		// 判断（下拉：绑定负责初始显示/回收同步；SelectionChanged 负责写回——工厂模板里
		// ComboBox 的 SelectedIndex 双向绑定不回推源，不能只依赖绑定）
		var judgeCol = new DataGridTemplateColumn { Header = "判断", Width = new DataGridLength(84) };
		var judgeFactory = new FrameworkElementFactory(typeof(ComboBox));
		judgeFactory.SetValue(ComboBox.ItemsSourceProperty, new[] { "参与判定", "仅观察" });
		judgeFactory.SetValue(ComboBox.HeightProperty, 22.0);
		judgeFactory.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(RoiRow.JudgeIndex)) { Mode = BindingMode.TwoWay });
		judgeFactory.AddHandler(ComboBox.SelectionChangedEvent, (SelectionChangedEventHandler)((s, _) =>
		{
			if (_loading || _reverting || s is not ComboBox cb || cb.DataContext is not RoiRow row) return;
			var idx = cb.SelectedIndex;
			if (idx >= 0 && idx != row.JudgeIndex)
			{
				row.JudgeIndex = idx; // 触发 Row_PropertyChanged → WriteMeta（值相同则视为绑定拉取，不写）
			}
		}));
		judgeCol.CellTemplate = new DataTemplate { VisualTree = judgeFactory };
		grid.Columns.Add(judgeCol);
		// 阈值（检测项级；常驻 TextBox，点一下即可改，失焦提交 → 校验后写元数据）
		grid.Columns.Add(TextColumn("阈值", nameof(RoiRow.Threshold), 64));
		// 存图
		grid.Columns.Add(CheckColumn("存图", 52, nameof(RoiRow.SaveImage)));
		// 总范围
		grid.Columns.Add(CheckColumn("总范围", 60, nameof(RoiRow.Scope)));
		return grid;
	}

	/// <summary>复选框列：CheckBox 的 IsChecked 直绑行属性（TwoWay + PropertyChanged，点一下即写回），居中不可聚焦。</summary>
	private DataGridTemplateColumn CheckColumn(string header, double width, string propertyPath)
	{
		var col = new DataGridTemplateColumn { Header = header, Width = new DataGridLength(width) };
		var factory = new FrameworkElementFactory(typeof(CheckBox));
		factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
		factory.SetValue(FocusableProperty, false);
		factory.SetBinding(ToggleButton.IsCheckedProperty, new Binding(propertyPath)
		{
			Mode = BindingMode.TwoWay,
			UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
		});
		col.CellTemplate = new DataTemplate { VisualTree = factory };
		return col;
	}

	/// <summary>文本列（常驻 TextBox，免双击进入编辑态）：失焦提交 → 行属性通知 → 写回；透明背景融入表格。</summary>
	private DataGridTemplateColumn TextColumn(string header, string propertyPath, double width)
	{
		var col = new DataGridTemplateColumn { Header = header, Width = new DataGridLength(width) };
		var factory = new FrameworkElementFactory(typeof(TextBox));
		factory.SetBinding(TextBox.TextProperty, new Binding(propertyPath)
		{
			Mode = BindingMode.TwoWay,
			UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
		});
		factory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
		factory.SetValue(TextBox.BackgroundProperty, Brushes.Transparent);
		factory.SetValue(TextBox.HeightProperty, 22.0);
		factory.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
		col.CellTemplate = new DataTemplate { VisualTree = factory };
		return col;
	}

	/// <summary>用户编辑（绑定把值推进行属性）→ 分发写回节点参数；_loading（重建行）/_reverting（回滚显示）时不写。</summary>
	private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_loading || _reverting || sender is not RoiRow row) return;
		switch (e.PropertyName)
		{
			case nameof(RoiRow.Name):
				HandleRename(row);
				break;
			case nameof(RoiRow.Threshold):
				if (row.Threshold.Trim().Length > 0 && !double.TryParse(row.Threshold.Trim(), out _))
				{
					AppendHint($"阈值「{row.Threshold.Trim()}」不是数值，已忽略");
					RevertThreshold(row);
				}
				else
				{
					WriteMeta(row);
				}
				break;
			case nameof(RoiRow.Enabled):
			case nameof(RoiRow.SaveImage):
			case nameof(RoiRow.JudgeIndex):
				// 行内值即最新状态，绑定已同步显示，无需重建整表
				WriteMeta(row);
				break;
			case nameof(RoiRow.Scope):
				if (row.Scope)
				{
					_main.SetScopeIndex(_hostNode, row.Index); // 勾选 = 设为总范围（覆盖旧项）
					ScheduleRefresh(); // 其他行的「总范围」勾选需要同步取消
				}
				else if (_main.GetScopeIndex(_hostNode) == row.Index)
				{
					_main.SetScopeIndex(_hostNode, -1);
				}
				break;
		}
	}

	/// <summary>重命名：空名回滚；同名/空白自动忽略（RenameOwnRoi 幂等）。</summary>
	private void HandleRename(RoiRow row)
	{
		var name = row.Name.Trim();
		if (name.Length == 0)
		{
			AppendHint("名称不能为空，已忽略");
			RevertName(row);
			return;
		}
		_main.RenameOwnRoi(_hostNode, row.Index, name);
		ScheduleRefresh(); // 规范化显示（去空白）
	}

	private void WriteMeta(RoiRow row)
	{
		_main.SetRoiMeta(_hostNode, row.Index, new RoiMeta(
			Enabled: row.Enabled,
			Judge: row.JudgeIndex == 1 ? "仅观察" : "",
			Threshold: row.Threshold.Trim(),
			SaveImage: row.SaveImage));
	}

	/// <summary>非法输入回滚单元格显示：从节点当前 own_rois 取回旧值写回行属性（_reverting 防再触发）。</summary>
	private void RevertName(RoiRow row)
	{
		var items = OwnItemsFull;
		var old = row.Index >= 0 && row.Index < items.Count ? items[row.Index].Name : row.Name;
		_reverting = true;
		try { row.Name = old; }
		finally { _reverting = false; }
	}

	private void RevertThreshold(RoiRow row)
	{
		var items = OwnItemsFull;
		var old = row.Index >= 0 && row.Index < items.Count ? items[row.Index].Meta.Threshold : row.Threshold;
		_reverting = true;
		try { row.Threshold = old; }
		finally { _reverting = false; }
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
			var rows = new List<RoiRow>(items.Count);
			for (var i = 0; i < items.Count; i++)
			{
				var it = items[i];
				var row = new RoiRow
				{
					Index = i,
					No = $"[{i + 1}]",
					Name = it.Name,
					Geometry = it.Rect.Serialize(),
					Enabled = it.Meta.Enabled,
					JudgeIndex = string.Equals(it.Meta.Judge, "仅观察", StringComparison.Ordinal) ? 1 : 0,
					Threshold = it.Meta.Threshold,
					SaveImage = it.Meta.SaveImage,
					Scope = scope == i,
				};
				row.PropertyChanged += Row_PropertyChanged;
				rows.Add(row);
			}
			_grid.ItemsSource = rows;
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
