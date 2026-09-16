using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using OpenCvSharp;
using System.Runtime.InteropServices;
using VisionInspection.Camera;
using VisionInspection.Comm;
using VisionInspection.Detection;
using VisionInspection.Models;
using VisionInspection.Production;
using VisionInspection.Services;
using WpfWindow = System.Windows.Window;
using ModelsCondition = VisionInspection.Models.Condition;

namespace VisionInspection;

public partial class MainWindow
{
	private void RefreshNodeTree()
	{
		if (_recipe == null)
		{
			return;
		}
		NodeTree.Items.Clear();
		foreach (RecipeNode node in _recipe.Nodes)
		{
			string value = TypeDisplay(node.Type);
			string text = (node.Name.Contains(value) ? ((node.Enabled ? "" : "[停] ") + node.Name) : $"{(node.Enabled ? "" : "[停] ")}{node.Name}（{value}）");
			DockPanel dockPanel = new DockPanel
			{
				LastChildFill = true
			};
			Button element = new Button
			{
				Content = new TextBlock
				{
					Text = "\ue713",
					FontFamily = new FontFamily("Segoe MDL2 Assets"),
					FontSize = 11.0
				},
				Width = 22.0,
				Height = 20.0,
				Padding = new Thickness(0.0),
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = "节点参数（弹出窗口编辑）"
			};
			ToolTipService.SetPlacement((DependencyObject)(object)element, PlacementMode.Bottom);
			ToolTipService.SetHorizontalOffset((DependencyObject)(object)element, 12.0);
			RecipeNode captured = node;
			element.Apply(delegate(Button b)
			{
				b.Click += delegate
				{
					OpenNodeDialog(captured);
				};
			});
			DockPanel.SetDock(element, Dock.Right);
			dockPanel.Children.Add(element);
			dockPanel.Children.Add(new TextBlock
			{
				Text = text,
				VerticalAlignment = VerticalAlignment.Center
			});
			NodeTree.Items.Add(new TreeViewItem
			{
				Header = dockPanel,
				Tag = node
			});
		}
		UpdateButtonState();
		_execution.Invalidate();
		if (_paramDialog != null)
		{
			if (_recipe.Nodes.All((RecipeNode n) => n != _paramDialog.Node))
			{
				_paramDialog.Close();
			}
			else
			{
				_paramDialog.Rebuild();
			}
		}
		RefreshStatusPanel();
		RenderRoiOverlay();
		RefreshKeyBindings();
	}

	internal void OpenNodeDialog(RecipeNode rn)
	{
		_paramDialog?.Close();
		NodeParamDialog nodeParamDialog = (_paramDialog = new NodeParamDialog(this, rn));
		nodeParamDialog.Show();
		nodeParamDialog.Activate();
	}

	internal void OnParamDialogClosed(NodeParamDialog dlg)
	{
		if (_paramDialog == dlg)
		{
			_paramDialog = null;
		}
		if (_roiDrawNode == dlg.Node)
		{
			ExitRoiDrawMode();
		}
	}

	/// <summary>模块弹窗取本节点最近一次执行结果（无则 null）。</summary>
	internal ModuleRunRecord? GetModuleLatestResult(string nodeName) => _moduleHistory.GetLatest(nodeName);

	/// <summary>模块弹窗取本节点执行历史（旧→新）。</summary>
	internal IReadOnlyList<ModuleRunRecord> GetModuleHistory(string nodeName) => _moduleHistory.GetHistory(nodeName);

	private void NodeTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (NodeTree.SelectedItem is TreeViewItem { Tag: RecipeNode tag })
		{
			OpenNodeDialog(tag);
		}
	}

	private static string TypeDisplay(string type)
	{
		string value;
		return NodeTypeLabels.TryGetValue(type, out value) ? value : type;
	}

	private async void AddNode_Click(object sender, RoutedEventArgs e)
	{
		if (IsTriggerDriven)
		{
			AppendLog("[编辑] 硬触发检测进行中，暂不能添加模块；节点参数仍可调整");
			return;
		}
		if (_recipe == null || !EnsureUnlocked("添加模块"))
		{
			return;
		}
		if (!await ConfirmStop("添加模块将停止当前服务。继续？"))
		{
			return;
		}
		FrameworkElement placementTarget = sender as FrameworkElement;
		ContextMenu contextMenu = new ContextMenu
		{
			PlacementTarget = placementTarget
		};
		foreach (string type in NodeFactory.RegisteredTypes.OrderBy((string t) => t))
		{
			if (type == "Display")
			{
				continue;
			}
			string label = (NodeTypeLabels.TryGetValue(type, out string value) ? value : type);
			MenuItem menuItem = new MenuItem
			{
				Header = label + " (" + type + ")"
			};
			menuItem.Click += delegate
			{
				string shortLabel = (label.Contains('（') ? label.Substring(0, label.IndexOf('（')) : label);
				int n = _recipe.Nodes.Count + 1;
				while (_recipe.Nodes.Any((RecipeNode x) => x.Name == $"{n:D2} {shortLabel}"))
				{
					n++;
				}
				RecipeNode recipeNode = new RecipeNode
				{
					Name = $"{n:D2} {shortLabel}",
					Type = type,
					Enabled = true
				};
				if (_recipe.Nodes.Count > 0)
				{
					IModelNode modelNode = NodeFactory.Create(type, recipeNode.Name);
					if (modelNode != null && modelNode.ParamDefs.Any((ParamDef d) => d.Key == "source"))
					{
						Dictionary<string, string> dictionary = recipeNode.Params;
						List<RecipeNode> nodes = _recipe.Nodes;
						dictionary["source"] = nodes[nodes.Count - 1].Name;
					}
				}
				if (type == "Decision")
				{
					int num = 1;
					List<DecisionRule> list = new List<DecisionRule>(num);
					CollectionsMarshal.SetCount(list, num);
					ref DecisionRule reference = ref CollectionsMarshal.AsSpan(list)[0];
					DecisionRule obj = new DecisionRule
					{
						MatchMode = "all"
					};
					int num2 = 1;
					List<VisionInspection.Models.Condition> list2 = new List<VisionInspection.Models.Condition>(num2);
					CollectionsMarshal.SetCount(list2, num2);
					CollectionsMarshal.AsSpan(list2)[0] = new VisionInspection.Models.Condition
					{
						Node = (_recipe.Nodes.FirstOrDefault((RecipeNode x) => x.Type == "PatchCore")?.Name ?? ""),
						Field = "decision",
						Op = "=",
						Value = "OK"
					};
					obj.Conditions = list2;
					obj.Result = "OK";
					obj.ElseResult = "NG";
					reference = obj;
					recipeNode.Rules = list;
				}
				PushUndoSnapshot();
				_recipe.Nodes.Add(recipeNode);
				RefreshNodeTree();
				AppendLog("已添加节点: " + recipeNode.Name);
			};
			contextMenu.Items.Add(menuItem);
		}
		contextMenu.IsOpen = true;
	}

	private void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		if (NodeTree.SelectedItem is TreeViewItem { Tag: RecipeNode } && _roiDrawMode)
		{
			ExitRoiDrawMode();
		}
		RefreshInspectorModuleResult();
	}

	/// <summary>左侧流程树当前选中的节点（未选中为 null）。</summary>
	private RecipeNode? SelectedRecipeNode =>
		NodeTree.SelectedItem is TreeViewItem { Tag: RecipeNode rn } ? rn : null;

	internal void BuildNodeEditor(RecipeNode rn, StackPanel panel, NodeParamDialog dialog)
	{
		_editorPanel = panel;
		_paramBoxes = new Dictionary<string, TextBox>();
		TextBlock element = new TextBlock
		{
			Text = rn.Name,
			FontWeight = FontWeights.Bold,
			FontSize = 14.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		EditorPanel.Children.Add(element);
		AddInspectorLabel("类型", TypeDisplay(rn.Type) + " (" + rn.Type + ")");
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		dockPanel.Children.Add(new TextBlock
		{
			Text = "名称",
			Width = 44.0,
			Foreground = (Brush)FindResource("MutedTextBrush"),
			VerticalAlignment = VerticalAlignment.Center
		});
		TextBox nameBox = new TextBox
		{
			Text = rn.Name
		};
		nameBox.LostFocus += delegate
		{
			RenameNode(rn, nameBox.Text);
		};
		nameBox.KeyDown += delegate(object _, KeyEventArgs e)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0008: Invalid comparison between Unknown and I4
			if ((int)e.Key == 6)
			{
				Keyboard.ClearFocus();
			}
		};
		dockPanel.Children.Add(nameBox);
		EditorPanel.Children.Add(dockPanel);
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		CheckBox checkBox = new CheckBox
		{
			IsChecked = rn.Enabled,
			Content = "启用",
			VerticalAlignment = VerticalAlignment.Center
		};
		checkBox.Checked += delegate
		{
			rn.Enabled = true;
			RefreshNodeTree();
			ApplyNodeEnabledHot(rn);
		};
		checkBox.Unchecked += delegate
		{
			rn.Enabled = false;
			RefreshNodeTree();
			ApplyNodeEnabledHot(rn);
		};
		stackPanel.Children.Add(checkBox);
		EditorPanel.Children.Add(stackPanel);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		Button button = new Button
		{
			Content = "上移",
			Width = 64.0,
			Height = 26.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		};
		button.Click += delegate
		{
			MoveNode(rn, -1);
		};
		Button button2 = new Button
		{
			Content = "下移",
			Width = 64.0,
			Height = 26.0
		};
		button2.Click += delegate
		{
			MoveNode(rn, 1);
		};
		stackPanel2.Children.Add(button);
		stackPanel2.Children.Add(button2);
		EditorPanel.Children.Add(stackPanel2);
		if (rn.Type == "Decision")
		{
			ShowDecisionRulesEditor(rn);
		}
		else
		{
			ShowParamDefsInspector(rn, dialog);
		}
		if (rn.Type == "CameraIo")
		{
			// 相机IO通信节点：IO 输出配置即本节点参数；提供按当前参数的测试输出（与执行共用 CameraIoNode.BuildOutputSettings）
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "IO 输出测试",
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(0.0, 14.0, 0.0, 6.0)
			});
			Button ioTestButton = new Button
			{
				Content = "测试输出脉冲（按当前参数）",
				Height = 28.0,
				HorizontalAlignment = HorizontalAlignment.Left,
				ToolTip = "按本节点当前输出配置（输出线/Strobe源/有效电平/持续时间）输出一次脉冲；相机须已连接"
			};
			ioTestButton.Click += async delegate
			{
				if (_execution.IsRunning)
				{
					AppendLog("[IO测试] 生产检测运行中，暂不可测试");
					return;
				}
				ICameraController? camera = _camera;
				if (camera == null || camera.State != CameraConnectionState.Connected)
				{
					AppendLog("[IO测试] 相机未连接，无法输出（相机请在「相机管理」里连接）");
					return;
				}
				try
				{
					var settings = CameraIoNode.BuildOutputSettings(rn.Params);
					await camera.PulseNgOutputAsync(settings);
					AppendLog($"[IO测试] 已输出 {settings.NgOutputLine} 脉冲 {settings.PulseMs:F0}ms（Strobe={settings.StrobeSource}，有效电平={(settings.ActiveLevel == "Low" ? "低" : "高")}）");
				}
				catch (Exception ex)
				{
					AppendLog("[IO测试] 输出失败: " + ex.Message);
				}
			};
			EditorPanel.Children.Add(ioTestButton);
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "说明: 触发输入（Line0/触发沿）在「相机管理」的触发参数里配置；本节点只负责按判定结果输出脉冲。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				FontSize = 11.0,
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
			});
		}
		if (rn.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch" or "FastMatch" or "CharRec" or "Blob" or "LineFind" or "CircleFind")
		{
			// 轮廓匹配/快速匹配/直线查找/圆查找：只有「绘制搜索区域」一种 ROI 功能（无检测项管理）；ROI 库已移除，所有 ROI 均为本节点私有
			bool isContour = rn.Type is "ContourMatch" or "FastMatch" or "LineFind" or "CircleFind";
			var roiNames = rn.Type == "CircleFind"
				? NodeRings.Parse(rn.Params.GetValueOrDefault("own_rings")).Select(r => r.Name).ToList()
				: NodeRois.ParseOwn(rn.Params.GetValueOrDefault("own_rois")).Select(r => r.Name).ToList();
			bool flag = roiNames.Count > 0;

			DockPanel dockPanel2 = new DockPanel
			{
				Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
			};
			dockPanel2.Children.Add(new TextBlock
			{
				Text = "检测区域 (ROI)",
				FontWeight = FontWeights.Bold,
				VerticalAlignment = VerticalAlignment.Center
			});
			Button button3 = new Button
			{
				Width = 30.0,
				Height = 26.0,
				HorizontalAlignment = HorizontalAlignment.Right,
				Content = new TextBlock
				{
					Text = "\ue7a8",
					FontFamily = new FontFamily("Segoe MDL2 Assets"),
					FontSize = 14.0
				},
				ToolTip = "绘制 ROI：开启后在中间图像区拖拽画框（框只作用于本节点，可多框；点击已有框可移动/缩放/旋转，右键删除；再点一次退出）"
			};
			ToolTipService.SetPlacement((DependencyObject)(object)button3, PlacementMode.Bottom);
			ToolTipService.SetHorizontalOffset((DependencyObject)(object)button3, 16.0);
			button3.Apply(delegate(Button b)
			{
				b.Click += delegate
				{
					ToggleRoiDraw(rn, dialog);
				};
			});
			dockPanel2.Children.Add(button3);
			EditorPanel.Children.Add(dockPanel2);
			TextBlock textBlock = new TextBlock
			{
				Text = flag
					? ("本节点 ROI: " + string.Join("、", roiNames) + "（私有，仅作用于本节点）")
					: "本节点暂无 ROI（全图检测；点绘制按钮画框）",
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				FontSize = 11.0,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
			};
			EditorPanel.Children.Add(textBlock);
			if (!isContour)
			{
				EditorPanel.Children.Add(new Button
				{
					Content = "检测项管理（本节点）",
					Height = 26.0,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
					HorizontalAlignment = HorizontalAlignment.Left,
					ToolTip = "管理独属于本节点的检测项（一个检测项=一个检测区域）：新建/重命名/删除"
				}.Apply(delegate(Button c)
				{
					c.Click += delegate
					{
						OpenRoiManager(rn, dialog);
					};
				}));
			}
			if (rn.Type is "ContourMatch" or "FastMatch")
			{
				EditorPanel.Children.Add(new Button
				{
					Content = "创建模板（轮廓建模）",
					Height = 26.0,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
					HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "打开建模弹窗：已有模板会自动回填模板图/框选区域/基准点/参数，直接调参（滤波Sigma/建模边缘阈值/层数）→ 提取预览 → 保存即可更新模板"
				}.Apply(delegate(Button c)
				{
					c.Click += delegate
					{
						ContourTemplateDialog templateDialog = new(this, rn, dialog);
						templateDialog.Owner = dialog;
						templateDialog.Show();
						templateDialog.Activate();
					};
				}));
			}
			if (rn.Type == "CharRec")
			{
				EditorPanel.Children.Add(new Button
				{
					Content = "字符训练（建字模）",
					Height = 26.0,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
					HorizontalAlignment = HorizontalAlignment.Left,
					ToolTip = "打开训练弹窗：样本图上框选单个字符 → 输入标签 → 保存字模（归一化 32×48 存入字模库，同字符可多采样）；预处理参数与本节点一致"
				}.Apply(delegate(Button c)
				{
					c.Click += delegate
					{
						CharTemplateDialog charDialog = new(this, rn, dialog);
						charDialog.Owner = dialog;
						charDialog.Show();
						charDialog.Activate();
					};
				}));
			}
			if (rn.Type == "PositionCorrection")
			{
				EditorPanel.Children.Add(new TextBlock
				{
					Text = "定位失败 → 本节点 ERROR 停线；未创建基准 → 透传不修正",
					TextWrapping = TextWrapping.Wrap,
					Foreground = (Brush)FindResource("MutedTextBrush"),
					FontSize = 11.0,
					Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
				});
			}
			EditorPanel.Children.Add(new Button
			{
				Content = "清除本节点 ROI（全图检测）",
				Width = 150.0,
				Height = 24.0,
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
				HorizontalAlignment = HorizontalAlignment.Left,
				IsEnabled = flag
			}.Apply(delegate(Button c)
			{
				c.Click += delegate
				{
					if (_roiDrawMode && _roiDrawNode == rn)
					{
						ExitRoiDrawMode();
					}
					if (rn.Type == "CircleFind")
					{
						WriteOwnRings(rn, new List<(string, RoiRing)>());
						AppendLog("[ROI] " + rn.Name + ": 已清除本节点圆环搜索区域");
					}
					else
					{
						WriteOwnRois(rn, new List<(string, RoiRect)>());
						AppendLog("[ROI] " + rn.Name + ": 已清除本节点 ROI（恢复全图检测）");
					}
					dialog.Rebuild();
					RenderRoiOverlay();
				};
			}));
			dialog.RegisterRoiControls(button3, textBlock);
		}
		EditorPanel.Children.Add(new Button
		{
			Content = "删除此节点",
			Margin = new Thickness(0.0, 14.0, 0.0, 0.0),
			Background = (Brush)FindResource("NgBrush"),
			Foreground = (Brush)FindResource("TextBrush")
		}.Apply(delegate(Button c)
		{
			c.Click += async delegate
			{
				if (!EnsureUnlocked("删除节点")) return;
				if (await ConfirmStop("删除节点将停止当前服务。继续？"))
				{
					PushUndoSnapshot();
					_recipe?.Nodes.RemoveAll((RecipeNode n) => n.Name == rn.Name);
					RefreshNodeTree();
				}
			};
		}));
	}

	private void RenameNode(RecipeNode rn, string newName)
	{
		if (!EnsureUnlocked("重命名节点")) return;
		newName = newName.Trim();
		if (_recipe == null || string.IsNullOrWhiteSpace(newName) || newName == rn.Name)
		{
			return;
		}
		if (_recipe.Nodes.Any((RecipeNode n) => n != rn && n.Name == newName))
		{
			AppendLog("[重命名] 失败：已存在同名节点 " + newName);
			return;
		}
		string name = rn.Name;
		PushUndoSnapshot();
		rn.Name = newName;
		_moduleHistory.RenameNode(name, newName); // 模块结果历史跟随改名
		RefreshInspectorModuleResult();
		int num = 0;
		foreach (RecipeNode node in _recipe.Nodes)
		{
			if (node.Params.TryGetValue("source", out string value) && value == name)
			{
				node.Params["source"] = newName;
				num++;
			}
			if (node.Rules == null)
			{
				continue;
			}
			foreach (DecisionRule rule in node.Rules)
			{
				foreach (VisionInspection.Models.Condition condition in rule.Conditions)
				{
					if (condition.Node == name)
					{
						condition.Node = newName;
						num++;
					}
				}
			}
		}
		RefreshNodeTree();
		AppendLog($"[重命名] {name} → {newName}（同步更新 {num} 处引用）");
	}

	private async void MoveNode(RecipeNode rn, int delta)
	{
		if (_recipe == null || !EnsureUnlocked("调整节点顺序"))
		{
			return;
		}
		int num = _recipe.Nodes.IndexOf(rn);
		int num2 = num + delta;
		if (num < 0 || num2 < 0 || num2 >= _recipe.Nodes.Count || !await ConfirmStop("调整顺序将停止当前服务。继续？"))
		{
			return;
		}
		PushUndoSnapshot();
		_recipe.Nodes.RemoveAt(num);
		_recipe.Nodes.Insert(num2, rn);
		RefreshNodeTree();
		foreach (TreeViewItem item in NodeTree.Items.OfType<TreeViewItem>())
		{
			if (item.Tag == rn)
			{
				item.IsSelected = true;
				break;
			}
		}
		AppendLog($"已调整顺序: {rn.Name} → 第 {num2 + 1}/{_recipe.Nodes.Count} 位（保存方案后持久化）");
	}

	private void ShowDecisionRulesEditor(RecipeNode rn)
	{
		DecisionRule rule = EnsureDecisionRule(rn);
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "判断模块",
			FontWeight = FontWeights.Bold,
			FontSize = 15.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 8.0)
		});
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "基本参数 | 结果显示",
			Foreground = (Brush)FindResource("MutedTextBrush"),
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			FontSize = 12.0
		});
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "判断方式",
			Foreground = (Brush)FindResource("MutedTextBrush"),
			Margin = new Thickness(0.0, 8.0, 0.0, 3.0)
		});
		ComboBox modeCombo = new ComboBox
		{
			Height = 26.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		modeCombo.Items.Add("全部条件符合");
		modeCombo.Items.Add("任意条件符合");
		modeCombo.SelectedItem = (string.Equals(rule.MatchMode, "any", StringComparison.OrdinalIgnoreCase) ? "任意条件符合" : "全部条件符合");
		modeCombo.SelectionChanged += delegate
		{
			rule.MatchMode = ((modeCombo.SelectedItem?.ToString() == "任意条件符合") ? "any" : "all");
		};
		EditorPanel.Children.Add(modeCombo);
		string value = (string.Equals(rule.MatchMode, "any", StringComparison.OrdinalIgnoreCase) ? "任意启用条件符合" : "所有启用条件符合");
		EditorPanel.Children.Add(new TextBlock
		{
			Text = $"说明：{value}时输出 {DisplayDecision(rule.Result, "OK")}，否则输出 {DisplayDecision(rule.ElseResult, "NG")}",
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("MutedTextBrush"),
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
			FontSize = 12.0
		});
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 4.0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.8, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(34.0)
		});
		AddGridText(grid, "检测节点", 0, header: true);
		AddGridText(grid, "测试结果", 1, header: true);
		EditorPanel.Children.Add(grid);
		for (int num = 0; num < rule.Conditions.Count; num++)
		{
			AddDecisionConditionRow(rn, rule, num);
		}
		EditorPanel.Children.Add(new Button
		{
			Content = "+ 添加条件",
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
		}.Apply(delegate(Button c)
		{
			c.Click += delegate
			{
				rule.Conditions.Add(CreateDefaultDecisionCondition(rn));
				ShowDecisionRulesEditor(rn);
			};
		}));
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "输出",
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0.0, 16.0, 0.0, 6.0)
		});
		AddDecisionOutputRow("符合", rule, isHit: true);
		AddDecisionOutputRow("不符合", rule, isHit: false);
	}

	private void ShowParamDefsInspector(RecipeNode rn, NodeParamDialog? dialog = null)
	{
		string type = rn.Type;
		if (type == "PositionCorrection")
		{
			// 海康式自定义编辑器：全引用化（定位来源下拉 / 修正目标勾选），不走通用参数行
			ShowPositionCorrectionEditor(rn, dialog);
			return;
		}
		if (1 == 0)
		{
		}
		IReadOnlyList<ParamDef> readOnlyList;
		switch (type)
		{
		case "PatchCore":
			readOnlyList = PatchCoreNode.StaticParamDefs;
			break;
		case "SaveImage":
			readOnlyList = SaveImageNode.StaticParamDefs;
			break;
		case "CameraIo":
			readOnlyList = CameraIoNode.StaticParamDefs;
			break;
		case "KeyControl":
			readOnlyList = KeyControlNode.StaticParamDefs;
			break;
		case "ImageSource":
		case "ImageLoad":
			readOnlyList = ImageSourceNode.StaticParamDefs;
			break;
		case "Binarize":
			readOnlyList = BinarizeNode.StaticParamDefs;
			break;
		case "Geometry":
			readOnlyList = GeometryNode.StaticParamDefs;
			break;
		case "ColorTransform":
			readOnlyList = ColorTransformNode.StaticParamDefs;
			break;
		case "YOLO":
			readOnlyList = YoloNode.StaticParamDefs;
			break;
		case "Seg":
			readOnlyList = SegNode.StaticParamDefs;
			break;
		case "SemanticSeg":
			readOnlyList = SemanticSegNode.StaticParamDefs;
			break;
		case "ContourMatch":
		case "FastMatch":
			readOnlyList = ContourMatchNode.StaticParamDefs;
			break;
		case "CharRec":
			readOnlyList = CharRecNode.StaticParamDefs;
			break;
		case "Blob":
			readOnlyList = BlobNode.StaticParamDefs;
			break;
		case "SendData":
			readOnlyList = SendDataNode.StaticParamDefs;
			break;
		case "ReceiveData":
			readOnlyList = ReceiveDataNode.StaticParamDefs;
			break;
		case "LineFind":
			readOnlyList = LineFindNode.StaticParamDefs;
			break;
		case "CircleFind":
			readOnlyList = CircleFindNode.StaticParamDefs;
			break;
		case "LineLineMeasure":
			readOnlyList = LineLineMeasureNode.StaticParamDefs;
			break;
		case "LineCircleMeasure":
			readOnlyList = LineCircleMeasureNode.StaticParamDefs;
			break;
		case "CircleCircleMeasure":
			readOnlyList = CircleCircleMeasureNode.StaticParamDefs;
			break;
		case "PointCircleMeasure":
			readOnlyList = PointCircleMeasureNode.StaticParamDefs;
			break;
		case "PositionCorrection":
			readOnlyList = PositionCorrectionNode.StaticParamDefs;
			break;
		case "OverlayDisplay":
			readOnlyList = OverlayDisplayNode.StaticParamDefs;
			break;
		default:
			readOnlyList = Array.Empty<ParamDef>();
			break;
		}
		if (1 == 0)
		{
		}
		IReadOnlyList<ParamDef> readOnlyList2 = readOnlyList;
		if (readOnlyList2.Count == 0)
		{
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "模型类型 [" + rn.Type + "] 暂无参数定义（扩展点占位）。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
			});
			return;
		}
		foreach (ParamDef item in readOnlyList2)
		{
			EditorPanel.Children.Add(new TextBlock
			{
				Text = item.Label,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				Margin = new Thickness(0.0, 10.0, 0.0, 3.0)
			});
			switch (item.Kind)
			{
			case "hidden":
				continue; // 隐藏参数（如位置修正 base_*、总范围 scope_index）：由按钮/专用 UI 维护，不渲染通用行
			case "nodesource":
				ShowNodeSourceCombo(rn, item);
				break;
			case "nodechecklist":
				ShowNodeChecklist(rn, item);
				break;
			case "noderesult":
				ShowNodeResultCombo(rn, item);
				break;
			case "choice":
				ShowChoiceCombo(rn, item);
				break;
			case "device":
				ShowDeviceCombo(rn, item);
				break;
			case "folder":
				ShowFolderPicker(rn, item);
				break;
			case "path":
				ShowFilePicker(rn, item);
				break;
			default:
				ShowTextParam(rn, item);
				break;
			}
			if (rn.Type == "PatchCore" && item.Key == "threshold")
			{
				double? num = ReadModelThreshold(rn);
				EditorPanel.Children.Add(new TextBlock
				{
					Text = (num.HasValue ? $"模型阈值(训练): {num.Value:F4}" : "模型无阈值，请手动填写或标定"),
					Foreground = (Brush)FindResource("MutedTextBrush"),
					FontSize = 11.0,
					Margin = new Thickness(0.0, 2.0, 0.0, 4.0)
				});
			}
		}
		if (!(rn.Type == "PatchCore"))
		{
			return;
		}
		EditorPanel.Children.Add(new Button
		{
			Content = "对打分目录开始打分",
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = HorizontalAlignment.Left
		}.Apply(delegate(Button c)
		{
			c.Click += delegate
			{
				ScoreDirectory_Click(rn);
			};
		}));
	}

	/// <summary>
	/// 位置修正编辑器（海康式，全引用化）：选择方式（按点/按坐标）→ 定位来源引用下拉
	/// （上游定位节点，引用行显示「N 节点.匹配点X/Y / 角度」）→ X/Y方向尺度 → 创建基准 →
	/// 修正目标勾选（下游检测节点引用，不需手输节点名）→ 图像显示（基准点/运行点开关）。
	/// </summary>
	private void ShowPositionCorrectionEditor(RecipeNode rn, NodeParamDialog? dialog)
	{
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "位置修正",
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0.0, 10.0, 0.0, 2.0)
		});
		int selfIdx = _recipe?.Nodes.IndexOf(rn) ?? -1;
		var locators = (_recipe?.Nodes ?? new List<RecipeNode>())
			.Take(Math.Max(0, selfIdx))
			.Where(n => n.Type is "ContourMatch" or "FastMatch" or "LineFind" or "CircleFind") // 定位来源=输出 loc_* 契约键的节点（轮廓匹配/快速匹配/直线查找/圆查找）
			.ToList();

		System.Windows.UIElement Row(string label, FrameworkElement content, double top = 4.0)
		{
			var row = new DockPanel { Margin = new Thickness(0.0, top, 0.0, 0.0) };
			row.Children.Add(new TextBlock
			{
				Text = label,
				Width = 78.0,
				VerticalAlignment = VerticalAlignment.Center,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				FontSize = 12.0
			});
			content.VerticalAlignment = VerticalAlignment.Center;
			row.Children.Add(content);
			return row;
		}

		FrameworkElement RefExpr(string text)
		{
			var sp = new StackPanel { Orientation = Orientation.Horizontal };
			sp.Children.Add(new TextBlock
			{
				Text = "\uE71F",
				FontFamily = new FontFamily("Segoe MDL2 Assets"),
				FontSize = 12.0,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0.0, 0.0, 5.0, 0.0),
				ToolTip = "自动引用定位来源节点的输出（不需手填）"
			});
			sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
			return sp;
		}

		string RefText(RecipeNode locator, string field)
		{
			var seq = (_recipe?.Nodes.IndexOf(locator) ?? 0) + 1;
			return $"{seq} {locator.Name}.{field}";
		}

		// 选择方式（按点 / 按坐标）：仅决定引用行的展示形式，数据同源
		var currentMode = rn.Params.GetValueOrDefault("select_mode") is "按坐标" ? "按坐标" : "按点";
		var modePanel = new StackPanel { Orientation = Orientation.Horizontal };
		foreach (var mode in new[] { "按点", "按坐标" })
		{
			var radio = new RadioButton
			{
				Content = mode,
				GroupName = "pos_mode_" + rn.Name,
				IsChecked = mode == currentMode,
				Margin = new Thickness(0.0, 0.0, 14.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center
			};
			radio.Checked += delegate
			{
				if (rn.Params.GetValueOrDefault("select_mode") == mode)
				{
					return;
				}
				rn.Params["select_mode"] = mode;
				HotApplyParam(rn, "select_mode", mode);
				dialog?.Rebuild(); // 重建以切换引用行展示形式
			};
			modePanel.Children.Add(radio);
		}
		EditorPanel.Children.Add(Row("选择方式", modePanel, 6.0));

		// 定位来源（引用下拉：上游定位节点）
		var sourceCombo = new ComboBox { Height = 24.0, MinWidth = 170.0 };
		string RefDisplay(RecipeNode locator) => RefText(locator, "匹配点X/Y");
		sourceCombo.Items.Add("@input (原图)");
		foreach (var locator in locators)
		{
			sourceCombo.Items.Add(RefDisplay(locator));
		}
		var currentSource = (rn.Params.GetValueOrDefault("source") ?? "@input").Trim();
		var selectedDisplay = locators.FirstOrDefault(l => string.Equals(l.Name, currentSource, StringComparison.OrdinalIgnoreCase)) is { } hit
			? RefDisplay(hit)
			: currentSource == "@input" || currentSource.Length == 0
				? "@input (原图)"
				: currentSource; // 非定位节点的旧引用：原样显示避免静默改动
		if (!sourceCombo.Items.Contains(selectedDisplay))
		{
			sourceCombo.Items.Add(selectedDisplay);
		}
		sourceCombo.SelectedItem = selectedDisplay;
		if (locators.Count == 0)
		{
			EditorPanel.Children.Add(Row("定位来源", sourceCombo));
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "上游暂无定位节点（轮廓匹配/直线查找）；请先添加并放到本节点之前",
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				FontSize = 11.0,
				Margin = new Thickness(78.0, 3.0, 0.0, 0.0)
			});
		}
		else
		{
			sourceCombo.SelectionChanged += delegate
			{
				var text = (sourceCombo.SelectedItem?.ToString() ?? "@input").Trim();
				var value = text.StartsWith("@input") ? "@input" : (locators.FirstOrDefault(l => RefDisplay(l) == text)?.Name ?? text);
				if (rn.Params.GetValueOrDefault("source") == value)
				{
					return;
				}
				rn.Params["source"] = value;
				HotApplyParam(rn, "source", value);
				dialog?.Rebuild(); // 刷新引用行显示
			};
			EditorPanel.Children.Add(Row("定位来源", sourceCombo));
		}

		// 引用行：按点 = 原点+角度；按坐标 = 原点X/原点Y/角度（数据同源）
		var refLocator = locators.FirstOrDefault(l => string.Equals(l.Name, currentSource, StringComparison.OrdinalIgnoreCase));
		if (refLocator is null && locators.Count > 0)
		{
			refLocator = locators[0];
		}
		if (currentMode == "按点")
		{
			EditorPanel.Children.Add(Row("原点", refLocator is null
				? new TextBlock { Text = "—", Foreground = (Brush)FindResource("MutedTextBrush") }
				: RefExpr(RefText(refLocator, "匹配点X/Y"))));
			EditorPanel.Children.Add(Row("角度", refLocator is null
				? new TextBlock { Text = "—", Foreground = (Brush)FindResource("MutedTextBrush") }
				: RefExpr(RefText(refLocator, "角度"))));
		}
		else
		{
			EditorPanel.Children.Add(Row("原点X", refLocator is null
				? new TextBlock { Text = "—", Foreground = (Brush)FindResource("MutedTextBrush") }
				: RefExpr(RefText(refLocator, "匹配点X"))));
			EditorPanel.Children.Add(Row("原点Y", refLocator is null
				? new TextBlock { Text = "—", Foreground = (Brush)FindResource("MutedTextBrush") }
				: RefExpr(RefText(refLocator, "匹配点Y"))));
			EditorPanel.Children.Add(Row("角度", refLocator is null
				? new TextBlock { Text = "—", Foreground = (Brush)FindResource("MutedTextBrush") }
				: RefExpr(RefText(refLocator, "角度"))));
		}

		// X/Y 方向尺度
		foreach (var (key, label) in new[] { ("scale_x", "X方向尺度"), ("scale_y", "Y方向尺度") })
		{
			var scaleBox = new TextBox
			{
				Text = rn.Params.GetValueOrDefault(key) is { Length: > 0 } v ? v : "1",
				Width = 80.0,
				HorizontalAlignment = HorizontalAlignment.Left
			};
			scaleBox.LostFocus += delegate
			{
				if (double.TryParse(scaleBox.Text, out var v) && v > 0)
				{
					if (rn.Params.GetValueOrDefault(key) == scaleBox.Text)
					{
						return;
					}
					rn.Params[key] = scaleBox.Text;
					HotApplyParam(rn, key, scaleBox.Text);
				}
				else
				{
					scaleBox.Text = rn.Params.GetValueOrDefault(key) is { Length: > 0 } ok ? ok : "1";
				}
			};
			EditorPanel.Children.Add(Row(label, scaleBox));
		}

		// 创建基准（从定位来源最近一次执行一键采集，等效海康「创建基准」）
		double bx = 0, by = 0, ba = 0;
		bool hasBase = double.TryParse(rn.Params.GetValueOrDefault("base_x"), out bx) &&
			double.TryParse(rn.Params.GetValueOrDefault("base_y"), out by) &&
			double.TryParse(rn.Params.GetValueOrDefault("base_angle"), out ba);
		EditorPanel.Children.Add(new TextBlock
		{
			Text = hasBase ? $"当前基准: ({bx:F2}, {by:F2}, {ba:F2}°)" : "当前基准: 未创建",
			FontSize = 11.0,
			Foreground = (Brush)FindResource("MutedTextBrush"),
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
		});
		EditorPanel.Children.Add(new Button
		{
			Content = "创建基准",
			Width = 90.0,
			Height = 26.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
			HorizontalAlignment = HorizontalAlignment.Left,
			ToolTip = "把工件摆到标准位 → 单次执行 → 点此按钮：将定位来源本次输出的位姿存为基准位姿"
		}.Apply(delegate (Button c)
		{
			c.Click += delegate
			{
				CaptureBasePose(rn, dialog);
			};
		}));

		// 要修正 ROI 的节点（引用勾选：下游检测节点）
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "要修正ROI的节点",
			Foreground = (Brush)FindResource("MutedTextBrush"),
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		});
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "仅勾选的节点 ROI 会跟随修正（海康 VM 同语义）；未勾选的节点不修正",
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("MutedTextBrush"),
			FontSize = 11.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		});
		var downstream = (_recipe?.Nodes ?? new List<RecipeNode>())
			.Skip(selfIdx + 1)
			.Where(n => n.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch" or "FastMatch" or "CharRec" or "Blob" or "LineFind" or "CircleFind")
			.ToList();
		if (downstream.Count == 0)
		{
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "下游暂无检测节点（PatchCore/YOLO/实例分割/语义分割/轮廓匹配/快速匹配/字符识别/Blob分析/直线查找/圆查找）",
				Foreground = (Brush)FindResource("MutedTextBrush"),
				FontSize = 11.0,
				Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
			});
		}
		else
		{
			var targets = (rn.Params.GetValueOrDefault("target_nodes") ?? "")
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var targetBoxes = new List<CheckBox>();
			foreach (var node in downstream)
			{
				var nodeName = node.Name;
				var box = new CheckBox
				{
					Content = $"{(_recipe?.Nodes.IndexOf(node) ?? 0) + 1} {node.Name}",
					IsChecked = targets.Contains(nodeName),
					Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
				};
				box.Tag = nodeName;
				box.Checked += delegate { WriteTargets(); };
				box.Unchecked += delegate { WriteTargets(); };
				targetBoxes.Add(box);
				EditorPanel.Children.Add(box);
			}
			void WriteTargets()
			{
				rn.Params["target_nodes"] = string.Join(",", targetBoxes
					.Where(b => b.IsChecked == true)
					.Select(b => (string)b.Tag!));
				HotApplyParam(rn, "target_nodes", rn.Params["target_nodes"]);
				RenderRoiOverlay(); // 刷新青色虚线预览框
			}
		}

		// 图像显示（结果图上画基准点/运行点十字）
		EditorPanel.Children.Add(new TextBlock
		{
			Text = "图像显示",
			Foreground = (Brush)FindResource("MutedTextBrush"),
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		});
		foreach (var (key, label) in new[] { ("show_base", "基准点"), ("show_run", "运行点") })
		{
			var showBox = new CheckBox
			{
				Content = label,
				IsChecked = rn.Params.GetValueOrDefault(key) != "0",
				Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
			};
			showBox.Checked += delegate
			{
				rn.Params[key] = "1";
				HotApplyParam(rn, key, "1");
			};
			showBox.Unchecked += delegate
			{
				rn.Params[key] = "0";
				HotApplyParam(rn, key, "0");
			};
			EditorPanel.Children.Add(showBox);
		}
	}

	/// <summary>位置修正：把定位来源节点最近一次执行的 loc_* 位姿存为基准位姿（写入 base_* 参数并热生效）。</summary>
	private void CaptureBasePose(RecipeNode rn, NodeParamDialog? dialog)
	{
		var source = (rn.Params.GetValueOrDefault("source") ?? "").Trim();
		if (source.Length == 0 || source == "@input" || _lastNodeValues is null || !_lastNodeValues.TryGetValue(source, out var values))
		{
			AppendLog("[位置修正] " + rn.Name + ": 先执行一次（定位来源节点要有输出），再点「创建基准」");
			return;
		}
		if (!values.TryGetValue("loc_x", out var lx) || !values.TryGetValue("loc_y", out var ly) ||
			!values.TryGetValue("loc_angle", out var la) ||
			!double.TryParse(lx, out var x) || !double.TryParse(ly, out var y) || !double.TryParse(la, out var a))
		{
			AppendLog("[位置修正] " + rn.Name + ": 定位来源「" + source + "」未输出 loc_* 标准键（不是定位节点）");
			return;
		}
		if (values.TryGetValue("loc_valid", out var valid) && valid != "1")
		{
			AppendLog("[位置修正] " + rn.Name + ": 定位来源「" + source + "」本次定位无效（loc_valid=0），请摆好工件再执行一次");
			return;
		}
		rn.Params["base_x"] = x.ToString("F2");
		rn.Params["base_y"] = y.ToString("F2");
		rn.Params["base_angle"] = a.ToString("F2");
		HotApplyParam(rn, "base_x", rn.Params["base_x"]);
		HotApplyParam(rn, "base_y", rn.Params["base_y"]);
		HotApplyParam(rn, "base_angle", rn.Params["base_angle"]);
		AppendLog($"[位置修正] {rn.Name}: 基准位姿已创建 → X={x:F2} Y={y:F2} 角度={a:F2}（来源 {source}）");
		dialog?.Rebuild();
	}

	private async void ScoreDirectory_Click(RecipeNode rn)
	{		if (_recipe == null)
		{
			return;
		}
		if (!rn.Params.TryGetValue("score_dir", out string rawDir) || string.IsNullOrWhiteSpace(rawDir))
		{
			AppendLog("请先在节点参数里填写「打分目录」");
			return;
		}
		string dir = RecipeStore.Resolve(_recipe, rawDir);
		if (!Directory.Exists(dir))
		{
			AppendLog("打分目录不存在: " + dir);
		}
		else
		{
			if (!(await _execution.PrepareAsync()))
			{
				return;
			}
			Pipeline pipeline = _execution.CurrentPipeline!;
			PatchCoreNode runtimeNode = pipeline.Nodes.OfType<PatchCoreNode>().FirstOrDefault((PatchCoreNode n) => string.Equals(n.Name, rn.Name, StringComparison.OrdinalIgnoreCase));
			if (runtimeNode == null || !runtimeNode.Enabled)
			{
				AppendLog("[打分] 节点 " + rn.Name + " 不存在或未启用");
				return;
			}
			string sourceName = _recipe.Nodes.FirstOrDefault(delegate(RecipeNode n)
			{
				bool enabled = n.Enabled;
				bool flag = enabled;
				if (flag)
				{
					string type = n.Type;
					bool flag2 = ((type == "ImageSource" || type == "ImageLoad") ? true : false);
					flag = flag2;
				}
				return flag;
			})?.Name;
			if (sourceName == null)
			{
				AppendLog("[打分] 方案中没有启用的图像源节点，无法注入图片");
				return;
			}
			string[] files = (from f in Directory.EnumerateFiles(dir)
				where new string[6] { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains<string>(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
				select f).OrderBy<string, string>((string f) => f, StringComparer.OrdinalIgnoreCase).ToArray();
			if (files.Length == 0)
			{
				AppendLog("打分目录 " + dir + " 下没有图片");
				return;
			}
			AppendLog($"[打分] 开始: {files.Length} 张（走真实流水线链路，与检测同分布）");
			List<double> scores = new List<double>();
			string[] array = files;
			foreach (string file in array)
			{
				try
				{
					Dictionary<string, string> vals;
					string s;
					using (Mat bgr = ImagePreprocessService.LoadBgr(file))
					{
						Dictionary<string, Mat> overrides = new Dictionary<string, Mat> { [sourceName] = bgr };
						PipelineResult result = pipeline.Run(bgr, System.IO.Path.GetFileName(file), null, null, overrides);
						string raw = ((result.NodeValues.TryGetValue(rn.Name, out vals) && vals.TryGetValue("score", out s)) ? s : null);
						if (raw == null || !double.TryParse(raw, out var score))
						{
							AppendLog($"[打分] {System.IO.Path.GetFileName(file)} 无有效分数（{result.Error ?? "节点未产出"}）");
							continue;
						}
						scores.Add(score);
						AppendLog($"[打分] {System.IO.Path.GetFileName(file)} = {score:F4}（{result.Decision}）");
					}
					vals = null;
					s = null;
				}
				catch (Exception ex)
				{
					AppendLog("[打分] " + System.IO.Path.GetFileName(file) + " 读取失败: " + ex.Message);
				}
			}
			if (scores.Count == 0)
			{
				AppendLog("[打分] 完成，但没有有效分数");
				return;
			}
			double[] arr = scores.ToArray();
			double mean = arr.Average();
			double std = Math.Sqrt(arr.Sum((double v) => (v - mean) * (v - mean)) / (double)arr.Length);
			double? threshold = runtimeNode.EffectiveThreshold;
			int above = (threshold.HasValue ? arr.Count((double v) => v > threshold.Value) : 0);
			AppendLog($"[打分] 完成: {arr.Length}/{files.Length} 张 | min={arr.Min():F4} max={arr.Max():F4} mean={mean:F4} std={std:F4}" + (threshold.HasValue ? $" | 超过阈值({threshold.Value:F4})={above} 张（NG）" : " | 模型无阈值"));
		}
	}

	private double? ReadModelThreshold(RecipeNode rn)
	{
		if (_recipe == null || !rn.Params.TryGetValue("model_dir", out string value) || string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string path = RecipeStore.Resolve(_recipe, value);
		string path2 = System.IO.Path.Combine(path, "threshold.json");
		if (!File.Exists(path2))
		{
			return null;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path2));
			JsonElement value2;
			return jsonDocument.RootElement.TryGetProperty("threshold", out value2) ? new double?(value2.GetDouble()) : ((double?)null);
		}
		catch
		{
			return null;
		}
	}

	private void ApplyNodeEnabledHot(RecipeNode rn)
	{
		_execution.SetNodeEnabled(rn.Name, rn.Enabled);
	}

	internal void HotApplyParam(RecipeNode rn, string key, string value)
	{
		PushUndoSnapshot(); // 惰性捕获：上一个快照点保存的是本参数写入前的状态
		if (key == "trigger_key")
		{
			RefreshKeyBindings(); // 按键控制节点键位变更 → 立即重绑
		}
		_execution.HotApplyParam(rn.Name, key, value);
		SaveRecipeSilently();
	}

	private void ShowNodeSourceCombo(RecipeNode rn, ParamDef def)
	{
		ComboBox combo = new ComboBox
		{
			Height = 24.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
		};
		int num = _recipe?.Nodes.IndexOf(rn) ?? (-1);
		combo.Items.Add("@input (原图)");
		for (int i = 0; i < num; i++)
		{
			combo.Items.Add(_recipe.Nodes[i].Name);
		}
		string current = (rn.Params.TryGetValue(def.Key, out string value) ? value : (def.Default ?? "@input"));
		combo.SelectedItem = combo.Items.Cast<string>().FirstOrDefault((string x) => x == current || x.StartsWith(current + " (")) ?? "@input (原图)";
		combo.SelectionChanged += delegate
		{
			string text = combo.SelectedItem?.ToString() ?? "@input (原图)";
			rn.Params[def.Key] = (text.StartsWith("@input") ? "@input" : text);
			HotApplyParam(rn, def.Key, rn.Params[def.Key]);
		};
		EditorPanel.Children.Add(combo);
	}

	private void ShowNodeChecklist(RecipeNode rn, ParamDef def)
	{
		var selfIndex = _recipe?.Nodes.IndexOf(rn) ?? -1;
		var selected = OverlayDisplayNode.ParseNodeNames(rn.Params.GetValueOrDefault(def.Key));
		var panel = new StackPanel
		{
			Margin = new Thickness(0, 0, 0, 4),
			MaxHeight = 180,
		};
		foreach (var candidate in (_recipe?.Nodes ?? []).Take(Math.Max(0, selfIndex)))
		{
			var check = new CheckBox
			{
				Content = $"{candidate.Name}（{TypeDisplay(candidate.Type)}）",
				IsChecked = selected.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase),
				Margin = new Thickness(0, 2, 0, 2),
				ToolTip = "勾选后把该节点的 ROI、图像标注和判定摘要叠加到本节点",
			};
			check.Checked += (_, _) => UpdateOverlayNodeSelection(rn, panel);
			check.Unchecked += (_, _) => UpdateOverlayNodeSelection(rn, panel);
			panel.Children.Add(check);
		}
		if (panel.Children.Count == 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = "暂无上游节点可选择",
				Foreground = (Brush)FindResource("MutedTextBrush"),
				Margin = new Thickness(0, 2, 0, 4),
			});
		}
		EditorPanel.Children.Add(panel);
	}

	private void UpdateOverlayNodeSelection(RecipeNode rn, Panel panel)
	{
		var names = panel.Children.OfType<CheckBox>()
			.Where(c => c.IsChecked == true)
			.Select(c => (c.Content?.ToString() ?? "").Split("（", 2)[0])
			.Where(name => name.Length > 0);
		var value = string.Join(";", names);
		rn.Params["overlay_nodes"] = value;
		HotApplyParam(rn, "overlay_nodes", value);
	}

	/// <summary>
	/// 上游节点输出引用下拉（Kind=noderesult，几何测量节点的线/圆/点来源）：
	/// 选项 = 本节点上游中类型命中 def.SourceTypes 的节点；当前值不在清单时原样追加（引用被改名/失效时兜底显示，运行时报 ERROR）。
	/// </summary>
	private void ShowNodeResultCombo(RecipeNode rn, ParamDef def)
	{
		ComboBox combo = new ComboBox
		{
			Height = 24.0,
			MinWidth = 150.0
		};
		int selfIdx = _recipe?.Nodes.IndexOf(rn) ?? (-1);
		for (int i = 0; i < Math.Max(0, selfIdx); i++)
		{
			RecipeNode candidate = _recipe.Nodes[i];
			if (def.SourceTypes == null || def.SourceTypes.Length == 0 || Enumerable.Contains(def.SourceTypes, candidate.Type))
			{
				combo.Items.Add(candidate.Name);
			}
		}
		string? current = (rn.Params.TryGetValue(def.Key, out string value) && !string.IsNullOrEmpty(value)) ? value : null;
		if (current != null && !combo.Items.Contains(current))
		{
			combo.Items.Add(current);
		}
		combo.SelectedItem = current;
		combo.SelectionChanged += delegate
		{
			rn.Params[def.Key] = (combo.SelectedItem?.ToString() ?? "");
			HotApplyParam(rn, def.Key, rn.Params[def.Key]);
		};
		EditorPanel.Children.Add(combo);
	}

	private void ShowChoiceCombo(RecipeNode rn, ParamDef def)
	{
		ComboBox combo = new ComboBox
		{
			Height = 24.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
		};
		string[] array = def.Choices ?? Array.Empty<string>();
		foreach (string newItem in array)
		{
			combo.Items.Add(newItem);
		}
		ComboBox comboBox = combo;
		string? selectedItem;
		if (rn.Params.TryGetValue(def.Key, out string value) && !string.IsNullOrEmpty(value))
		{
			string[]? choices = def.Choices;
			if (choices != null && Enumerable.Contains(choices, value))
			{
				selectedItem = value;
			}
			else
			{
				// 旧配方的存量值（如已中文化的英文名）不在新选项里：原样追加显示，改选后自然迁移
				combo.Items.Add(value);
				selectedItem = value;
			}
		}
		else
		{
			selectedItem = def.Default;
		}
		comboBox.SelectedItem = selectedItem;
		combo.SelectionChanged += delegate
		{
			rn.Params[def.Key] = combo.SelectedItem?.ToString() ?? "";
			HotApplyParam(rn, def.Key, rn.Params[def.Key]);
		};
		EditorPanel.Children.Add(combo);
	}

	/// <summary>通信设备下拉（发送数据/接收数据节点）：选项实时读 comm.json 设备名；当前值不在清单时原样追加显示。</summary>
	private void ShowDeviceCombo(RecipeNode rn, ParamDef def)
	{
		var devices = new CommDeviceStore(System.IO.Path.Combine(ConfigDir, "comm.json")).Load();
		ComboBox combo = new ComboBox
		{
			Height = 24.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
		};
		string? current = rn.Params.TryGetValue(def.Key, out string value) && !string.IsNullOrEmpty(value) ? value : null;
		foreach (var device in devices)
		{
			combo.Items.Add(device.Name);
		}
		if (current != null && !combo.Items.Contains(current))
		{
			combo.Items.Add(current);
		}
		if (combo.Items.Count == 0)
		{
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "通信管理中暂无设备，请先在工具栏「通信管理」添加并保存设备",
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				FontSize = 11.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
			});
		}
		combo.SelectedItem = current;
		combo.SelectionChanged += delegate
		{
			rn.Params[def.Key] = combo.SelectedItem?.ToString() ?? "";
			HotApplyParam(rn, def.Key, rn.Params[def.Key]);
		};
		EditorPanel.Children.Add(combo);
	}

	private void ShowFolderPicker(RecipeNode rn, ParamDef def)
	{
		DockPanel dockPanel = new DockPanel();
		TextBox box = new TextBox
		{
			Text = (rn.Params.TryGetValue(def.Key, out string value) ? value : (def.Default ?? ""))
		};
		Button button = new Button
		{
			Content = "浏览",
			Width = 60.0
		};
		button.Click += delegate
		{
			OpenFolderDialog openFolderDialog = new OpenFolderDialog
			{
				Title = "选择目录"
			};
			if (openFolderDialog.ShowDialog(this) == true)
			{
				box.Text = openFolderDialog.FolderName;
			}
		};
		DockPanel.SetDock(button, Dock.Right);
		dockPanel.Children.Add(button);
		dockPanel.Children.Add(box);
		box.TextChanged += delegate
		{
			rn.Params[def.Key] = box.Text;
			HotApplyParam(rn, def.Key, box.Text);
		};
		EditorPanel.Children.Add(dockPanel);
	}

	private void ShowFilePicker(RecipeNode rn, ParamDef def)
	{
		DockPanel dockPanel = new DockPanel();
		TextBox box = new TextBox
		{
			Text = (rn.Params.TryGetValue(def.Key, out string value) ? value : (def.Default ?? ""))
		};
		Button button = new Button
		{
			Content = "浏览",
			Width = 60.0
		};
		button.Click += delegate
		{
			OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Title = "选择图像文件",
				Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件 (*.*)|*.*"
			};
			if (openFileDialog.ShowDialog(this) == true)
			{
				box.Text = openFileDialog.FileName;
			}
		};
		DockPanel.SetDock(button, Dock.Right);
		dockPanel.Children.Add(button);
		dockPanel.Children.Add(box);
		box.TextChanged += delegate
		{
			rn.Params[def.Key] = box.Text;
			HotApplyParam(rn, def.Key, box.Text);
		};
		EditorPanel.Children.Add(dockPanel);
	}

	private void ShowTextParam(RecipeNode rn, ParamDef def)
	{
		TextBox box = new TextBox
		{
			Text = (rn.Params.TryGetValue(def.Key, out string value) ? value : (def.Default ?? ""))
		};
		box.TextChanged += delegate
		{
			rn.Params[def.Key] = box.Text;
			HotApplyParam(rn, def.Key, box.Text);
			if (def.Key == "threshold" && _execution.ApplyNodeParam(rn.Name, "threshold", box.Text))
			{
				AppendLog("阈值已热更新: " + rn.Name + " = " + box.Text);
			}
		};
		EditorPanel.Children.Add(box);
	}

	private DecisionRule EnsureDecisionRule(RecipeNode rn)
	{
		if (rn.Rules == null)
		{
			List<DecisionRule> list = (rn.Rules = new List<DecisionRule>());
		}
		if (rn.Rules.Count == 0)
		{
			List<DecisionRule>? rules = rn.Rules;
			DecisionRule obj = new DecisionRule
			{
				MatchMode = "all",
				Result = "OK",
				ElseResult = "NG"
			};
			int num = 1;
			List<VisionInspection.Models.Condition> list3 = new List<VisionInspection.Models.Condition>(num);
			CollectionsMarshal.SetCount(list3, num);
			CollectionsMarshal.AsSpan(list3)[0] = CreateDefaultDecisionCondition(rn);
			obj.Conditions = list3;
			rules.Add(obj);
		}
		DecisionRule decisionRule = rn.Rules[0];
		decisionRule.MatchMode = (string.Equals(decisionRule.MatchMode, "any", StringComparison.OrdinalIgnoreCase) ? "any" : "all");
		if (string.IsNullOrWhiteSpace(decisionRule.Result))
		{
			decisionRule.Result = "OK";
		}
		if (string.IsNullOrWhiteSpace(decisionRule.ElseResult))
		{
			decisionRule.ElseResult = "NG";
		}
		return decisionRule;
	}

	private VisionInspection.Models.Condition CreateDefaultDecisionCondition(RecipeNode rn)
	{
		RecipeNode recipeNode = GetUpstreamNodes(rn).FirstOrDefault();
		return new VisionInspection.Models.Condition
		{
			Node = (recipeNode?.Name ?? ""),
			Field = "decision",
			Op = "=",
			Value = "OK"
		};
	}

	private List<RecipeNode> GetUpstreamNodes(RecipeNode rn)
	{
		if (_recipe == null)
		{
			return new List<RecipeNode>();
		}
		int num = _recipe.Nodes.IndexOf(rn);
		return (num <= 0) ? new List<RecipeNode>() : _recipe.Nodes.Take(num).ToList();
	}

	/// <summary>节点上游节点名列表（建模弹窗用）。</summary>
	internal IReadOnlyList<string> GetUpstreamNodeNames(RecipeNode rn) =>
		GetUpstreamNodes(rn).Select((RecipeNode n) => n.Name).ToList();


	/// <summary>解析节点模型/模板目录（支持相对路径；不存在返回 null）。</summary>
	internal string? ResolveModelDir(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}
		if (Directory.Exists(raw))
		{
			return raw;
		}
		if (_recipe != null)
		{
			try
			{
				string resolved = RecipeStore.Resolve(_recipe, raw);
				if (Directory.Exists(resolved))
				{
					return resolved;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	/// <summary>节点模板的方案内自动目录：方案目录/模板/节点名（海康习惯：模板随方案管理，无需用户选目录）。</summary>
	internal string? GetRecipeTemplateDir(string nodeName)
	{
		if (_recipe is null)
		{
			return null;
		}
		try
		{
			var safe = string.Join("_", nodeName.Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
			if (string.IsNullOrWhiteSpace(safe))
			{
				safe = "未命名";
			}
			return RecipeStore.Resolve(_recipe, System.IO.Path.Combine("模板", safe));
		}
		catch
		{
			return null;
		}
	}

	private void AddDecisionConditionRow(RecipeNode rn, DecisionRule rule, int conditionIndex)
	{
		VisionInspection.Models.Condition cond = rule.Conditions[conditionIndex];
		cond.Field = "decision";
		cond.Op = "=";
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.8, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(34.0)
		});
		ComboBox nodeCombo = new ComboBox
		{
			Height = 26.0,
			Margin = new Thickness(0.0, 0.0, 4.0, 0.0)
		};
		foreach (RecipeNode upstreamNode in GetUpstreamNodes(rn))
		{
			nodeCombo.Items.Add(upstreamNode.Name);
		}
		nodeCombo.SelectedItem = nodeCombo.Items.Cast<string>().FirstOrDefault((string x) => x == cond.Node) ?? nodeCombo.Items.Cast<string>().FirstOrDefault();
		nodeCombo.SelectionChanged += delegate
		{
			cond.Node = nodeCombo.SelectedItem?.ToString() ?? "";
		};
		Grid.SetColumn(nodeCombo, 0);
		grid.Children.Add(nodeCombo);
		ComboBox resultCombo = new ComboBox
		{
			Height = 26.0,
			Margin = new Thickness(0.0, 0.0, 4.0, 0.0)
		};
		string[] array = new string[3] { "OK", "NG", "ERROR" };
		foreach (string newItem in array)
		{
			resultCombo.Items.Add(newItem);
		}
		resultCombo.SelectedItem = resultCombo.Items.Cast<string>().FirstOrDefault((string x) => string.Equals(x, cond.Value, StringComparison.OrdinalIgnoreCase)) ?? "OK";
		resultCombo.SelectionChanged += delegate
		{
			cond.Field = "decision";
			cond.Op = "=";
			cond.Value = resultCombo.SelectedItem?.ToString() ?? "OK";
		};
		Grid.SetColumn(resultCombo, 1);
		grid.Children.Add(resultCombo);
		Button button = new Button
		{
			Content = "×",
			Width = 28.0,
			Height = 26.0,
			Padding = new Thickness(2.0, 0.0, 2.0, 0.0),
			Margin = new Thickness(0.0)
		};
		button.Click += delegate
		{
			if (!rule.Conditions.Contains(cond))
			{
				ShowDecisionRulesEditor(rn);
			}
			else if (rule.Conditions.Count <= 1)
			{
				AppendLog("[判断] 至少保留一个条件");
			}
			else
			{
				rule.Conditions.Remove(cond);
				ShowDecisionRulesEditor(rn);
			}
		};
		Grid.SetColumn(button, 2);
		grid.Children.Add(button);
		EditorPanel.Children.Add(grid);
	}

	private void AddDecisionOutputRow(string label, DecisionRule rule, bool isHit)
	{
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
		};
		dockPanel.Children.Add(new TextBlock
		{
			Text = label + "：",
			Width = 58.0,
			Foreground = (Brush)FindResource("MutedTextBrush")
		});
		TextBox box = new TextBox
		{
			Text = (isHit ? DisplayDecision(rule.Result, "OK") : DisplayDecision(rule.ElseResult, "NG")),
			Height = 26.0,
			Width = 80.0
		};
		box.TextChanged += delegate
		{
			if (isHit)
			{
				rule.Result = box.Text.Trim();
			}
			else
			{
				rule.ElseResult = box.Text.Trim();
			}
		};
		dockPanel.Children.Add(box);
		EditorPanel.Children.Add(dockPanel);
	}

	private void AddGridText(Grid grid, string text, int column, bool header)
	{
		TextBlock element = new TextBlock
		{
			Text = text,
			FontWeight = (header ? FontWeights.Bold : FontWeights.Normal),
			Foreground = (Brush)FindResource(header ? "MutedTextBrush" : "TextBrush"),
			Margin = new Thickness(0.0, 0.0, 4.0, 0.0)
		};
		Grid.SetColumn(element, column);
		grid.Children.Add(element);
	}

	private static string DisplayDecision(string value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}

	private void AddInspectorLabel(string label, string value)
	{
		EditorPanel.Children.Add(new DockPanel
		{
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		}.Apply(delegate(DockPanel d)
		{
			d.Children.Add(new TextBlock
			{
				Text = label + ": " + value,
				Foreground = (Brush)FindResource("MutedTextBrush")
			});
		}));
	}

	private static double? ParseDouble(string s)
	{
		double result;
		return double.TryParse(s, out result) ? new double?(result) : ((double?)null);
	}
}
