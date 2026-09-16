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
using VisionInspection.Trigger;
using WpfWindow = System.Windows.Window;
using ModelsCondition = VisionInspection.Models.Condition;

namespace VisionInspection;

public partial class MainWindow
{
	private void ApplyImageViewTransform()
	{
		var transform = new TransformGroup();
		transform.Children.Add(_imageScale);
		transform.Children.Add(_imagePan);
		ResultImage.RenderTransform = transform;
		RoiCanvas.RenderTransform = transform;
	}

	private void ResetImageView()
	{
		_imageScale.ScaleX = 1;
		_imageScale.ScaleY = 1;
		_imagePan.X = 0;
		_imagePan.Y = 0;
		ApplyImageViewTransform();
	}

	private void ChangeImageZoom(double factor)
	{
		if (ResultImage.Source == null) return;
		var next = Math.Clamp(_imageScale.ScaleX * factor, 0.25, 8.0);
		_imageScale.ScaleX = next;
		_imageScale.ScaleY = next;
		ApplyImageViewTransform();
	}

	private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ChangeImageZoom(1.25);

	private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ChangeImageZoom(0.8);

	private void BtnImageFit_Click(object sender, RoutedEventArgs e) => ResetImageView();

	private void ImageViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (ResultImage.Source == null) return;
		ChangeImageZoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
		e.Handled = true;
	}

	private void ImageViewport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Middle || ResultImage.Source == null) return;
		_isPanningImage = true;
		_imagePanStart = e.GetPosition(ImageViewport);
		_imagePanOrigin = new Vector(_imagePan.X, _imagePan.Y);
		ImageViewport.CaptureMouse();
		ImageViewport.Cursor = Cursors.SizeAll;
		e.Handled = true;
	}

	private void ImageViewport_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_isPanningImage) return;
		var current = e.GetPosition(ImageViewport);
		_imagePan.X = _imagePanOrigin.X + current.X - _imagePanStart.X;
		_imagePan.Y = _imagePanOrigin.Y + current.Y - _imagePanStart.Y;
		ApplyImageViewTransform();
		e.Handled = true;
	}

	private void ImageViewport_PreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Middle || !_isPanningImage) return;
		_isPanningImage = false;
		ImageViewport.ReleaseMouseCapture();
		ImageViewport.Cursor = Cursors.Arrow;
		e.Handled = true;
	}

	private void OnRunnerCompleted(PipelineResult result)
	{
		var skipped = _pendingQueue.Publish(result);
		if (skipped == null)
		{
			return;
		}
		// 跳过未渲染的上一轮结果：释放其节点图（PendingResultQueue.DisposeSkipped 逐个容错共享 Mat）
		PendingResultQueue.DisposeSkipped(skipped);
	}

	private void RenderPendingResult()
	{
		var pending = _pendingQueue.Take();
		if (pending != null)
		{
			try
			{
				ShowExecutionResult(pending);
			}
			catch (ObjectDisposedException ex)
			{
				// 渲染链路中访问到已被释放的 Mat：丢弃本轮剩余渲染，记日志，不允许异常打断渲染定时器
				AppendLog("[显示] 渲染结果时图像已被释放，本轮已跳过: " + ex.Message);
			}
		}
	}

	private void ShowExecutionResult(PipelineResult result)
	{
		_latestResult = result;
		double score = double.NaN;
		foreach (Dictionary<string, string> value2 in result.NodeValues.Values)
		{
			if (value2.TryGetValue("score", out var value) && double.TryParse(value, out var result2))
			{
				score = result2;
				break;
			}
		}
		ResultGrid.Items.Insert(0, new DetectionResult(result.Image, score, result.Threshold, result.Decision, result.ProcessedAt, result.Error)
		{
			NodeDetails = result.NodeValues
		});
		_lastNodeValues = result.NodeValues;
		// 模块结果历史：按节点记录本轮输出（供「模块结果」页签），并刷新右侧面板当前选中节点的结果
		foreach (var (nodeName, nodeVals) in result.NodeValues)
		{
			_moduleHistory.Record(nodeName, nodeVals);
		}
		RefreshInspectorModuleResult();
		while (ResultGrid.Items.Count > 200)
		{
			ResultGrid.Items.RemoveAt(ResultGrid.Items.Count - 1);
		}
		ViewModel.FinalDecision = result.Decision;
		TextBlock finalVerdictText = FinalVerdictText;
		string decision = result.Decision;
		if (1 == 0)
		{
		}
		Brush foreground = ((decision == "OK") ? new SolidColorBrush(Color.FromRgb(29, 209, 161)) : ((!(decision == "NG")) ? new SolidColorBrush(Color.FromRgb(245, 166, 35)) : new SolidColorBrush(Color.FromRgb(byte.MaxValue, 107, 107))));
		if (1 == 0)
		{
		}
		finalVerdictText.Foreground = foreground;
		_thumbImages.Clear();
		_nodeAnnotations.Clear();
		// 同一 Mat 实例可能在多个节点键下共享：ConvertNodeImages 按引用去重转换一次，转换完统一释放输入 Mat
		foreach (var (key, bitmapSource) in UiExtensions.ConvertNodeImages(result.NodeImages))
		{
			_thumbImages[key] = bitmapSource;
		}
		foreach (var (key, shapes) in result.NodeAnnotations)
		{
			_nodeAnnotations[key] = shapes;
		}
		if (_thumbStripVisible)
		{
			RefreshThumbStrip();
		}
		// 选中节点保持：用户手动点选的缩略图不被每轮自动选中弹回（连续执行时可停留在某节点查看）；
		// 未选中/选中的节点本轮无图时，才自动选上屏节点（最后一个检测节点）
		if (_selectedThumbName != null && _thumbImages.ContainsKey(_selectedThumbName))
		{
			ShowNodeImage(_selectedThumbName);
		}
		else if (result.DisplayNodeName != null && _thumbImages.ContainsKey(result.DisplayNodeName))
		{
			ShowNodeImage(result.DisplayNodeName);
		}
		else
		{
			ResultImage.Source = null;
			ResultCaption.Text = "执行结果（本轮无节点输出图像）";
		}
		RenderRoiOverlay();
		RefreshStatusPanel();
	}

	/// <summary>切换方案时清理上一套方案的执行结果，避免旧结果与新流程混显。</summary>
	private void ClearExecutionResults()
	{
		var pending = _pendingQueue.Take();
		if (pending != null)
		{
			PendingResultQueue.DisposeSkipped(pending);
		}
		ResultGrid.Items.Clear();
		_latestResult = null;
		_lastNodeValues = null;
		_thumbImages.Clear();
		_nodeAnnotations.Clear();
		_selectedThumbName = null;
		ResultImage.Source = null;
		ResultCaption.Text = "暂无检测结果";
		ViewModel.FinalDecision = "待机";
		FinalVerdictText.Foreground = (Brush)FindResource("MutedTextBrush");
		if (_thumbStripVisible)
		{
			RefreshThumbStrip();
		}
		RefreshStatusPanel();
	}

	private void BtnThumbToggle_Click(object sender, RoutedEventArgs e)
	{
		_thumbStripVisible = BtnThumbToggle.IsChecked == true;
		ThumbStripHost.Visibility = ((!_thumbStripVisible) ? Visibility.Collapsed : Visibility.Visible);
		if (_thumbStripVisible)
		{
			RefreshThumbStrip();
		}
	}

	private void RefreshThumbStrip()
	{
		ThumbStrip.Children.Clear();
		foreach (KeyValuePair<string, BitmapSource> thumbImage in _thumbImages)
		{
			ThumbStrip.Children.Add(CreateThumbnail(thumbImage.Key, thumbImage.Value));
		}
		if (_selectedThumbName != null && _thumbImages.ContainsKey(_selectedThumbName))
		{
			HighlightThumb(_selectedThumbName);
		}
	}

	private void ShowNodeImage(string nodeName)
	{
		if (_thumbImages.TryGetValue(nodeName, out BitmapSource value))
		{
			var nodeChanged = !string.Equals(_selectedThumbName, nodeName, StringComparison.OrdinalIgnoreCase);
			ResultImage.Source = value;
			if (nodeChanged) ResetImageView();
			ResultCaption.Text = "节点结果: " + nodeName;
			_selectedThumbName = nodeName;
			HighlightThumb(nodeName);
			// 叠加层跟随显示节点（每个节点只显示自己的 ROI），切换节点即刷新
			RenderRoiOverlay();
		}
	}

	private void HighlightThumb(string nodeName)
	{
		foreach (Border item in ThumbStrip.Children.OfType<Border>())
		{
			bool flag = string.Equals(item.Tag as string, nodeName, StringComparison.OrdinalIgnoreCase);
			item.BorderThickness = new Thickness((!flag) ? 1 : 2);
			item.BorderBrush = (flag ? Brushes.DodgerBlue : ((Brush)FindResource("BorderBrush")));
		}
	}

	private UIElement CreateThumbnail(string nodeName, BitmapSource bmp)
	{
		Image element = new Image
		{
			Source = bmp,
			Width = 96.0,
			Height = 72.0,
			Stretch = Stretch.Uniform,
			Margin = new Thickness(1.0, 1.0, 1.0, 0.0)
		};
		TextBlock element2 = new TextBlock
		{
			Text = nodeName,
			FontSize = 10.0,
			Foreground = (Brush)FindResource("MutedTextBrush"),
			HorizontalAlignment = HorizontalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(2.0, 0.0, 2.0, 1.0),
			Width = 96.0
		};
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(element);
		stackPanel.Children.Add(element2);
		Border border = new Border
		{
			Child = stackPanel,
			Background = Brushes.Transparent, // 空白区域也可命中点击
			BorderBrush = (Brush)FindResource("BorderBrush"),
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(2.0, 2.0, 0.0, 2.0),
			Cursor = Cursors.Hand,
			Tag = nodeName,
			ToolTip = nodeName
		};
		border.MouseLeftButtonUp += delegate
		{
			ShowNodeImage(nodeName);
		};
		return border;
	}

	private void UpdateStatePanel()
	{
		string text;
		string text2;
		Brush fill;
		CameraInspectionService? triggerDriven = _triggerDriven;
		if (triggerDriven != null)
		{
			switch (triggerDriven.State)
			{
			case TriggerState.Armed:
				text = "等待 PLC 触发（触发一次检测一次）";
				text2 = "触发待机";
				fill = Brushes.DodgerBlue;
				break;
			case TriggerState.Busy:
				text = "触发检测中";
				text2 = "检测中";
				fill = Brushes.Orange;
				break;
			case TriggerState.Error:
				text = "触发检测错误: " + (triggerDriven.LastError ?? "未知");
				text2 = "触发错误";
				fill = Brushes.Red;
				break;
			default:
				text = "硬触发检测未启用";
				text2 = "空闲";
				fill = Brushes.Gray;
				break;
			}
		}
		else if (!_execution.IsRunning)
		{
			SolidColorBrush gray = Brushes.Gray;
			text = "未执行";
			text2 = "空闲";
			fill = gray;
		}
		else
		{
			SolidColorBrush orange = Brushes.Orange;
			string text3 = (_execution.IsContinuous ? "连续执行中（点「停止执行」结束）" : "单次执行中");
			text = text3;
			text2 = "执行中";
			fill = orange;
		}
		StateDot.Fill = fill;
		ViewModel.ProductionState = text2;
		ViewModel.ProductionDetail = text;
		ViewModel.TriggerStatus = triggerDriven != null ? $"触发溢出: {triggerDriven.OverrunCount}" : "触发: -";
	}

	private void UpdateButtonState()
	{
		bool flag6 = _execution.IsRunning;
		bool flag7 = _execution.IsContinuous;
		BtnAddNode.IsEnabled = !IsTriggerDriven;
		bool isEnabled = _recipe != null && HasEnabledImageSource() && !flag6 && !IsTriggerDriven;
		BtnRunOnce.IsEnabled = isEnabled;
		if (flag7)
		{
			RunContinuousIcon.Data = Geometry.Parse(IconStopData);
			BtnRunContinuous.Background = new SolidColorBrush(Color.FromRgb(107, 48, 48));
			BtnRunContinuous.BorderBrush = new SolidColorBrush(Color.FromRgb(138, 64, 64));
			BtnRunContinuous.IsEnabled = true;
			BtnRunContinuous.ToolTip = "停止执行";
		}
		else
		{
			RunContinuousIcon.Data = Geometry.Parse(IconContinuousData);
			BtnRunContinuous.Background = new SolidColorBrush(Color.FromRgb(31, 74, 110));
			BtnRunContinuous.BorderBrush = new SolidColorBrush(Color.FromRgb(47, 106, 158));
			BtnRunContinuous.IsEnabled = isEnabled;
			BtnRunContinuous.ToolTip = "连续执行：循环执行检测流程（再次点击停止）";
		}
	}

	private bool HasEnabledImageSource()
	{
		return _recipe?.Nodes.Any(delegate(RecipeNode n)
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
		}) ?? false;
	}

	private void RefreshStatusPanel()
	{
		InspectorPanel.Children.Clear();
		InspectorPanel.Children.Add(new TextBlock
		{
			Text = "节点状态",
			FontWeight = FontWeights.Bold,
			FontSize = 14.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		if (_latestResult != null)
		{
			string decision = _latestResult.Decision;
			if (1 == 0)
			{
			}
			Brush brush = ((decision == "OK") ? ((Brush)FindResource("OkBrush")) : ((!(decision == "NG")) ? Brushes.Orange : ((Brush)FindResource("NgBrush"))));
			if (1 == 0)
			{
			}
			Brush foreground = brush;
			InspectorPanel.Children.Add(new TextBlock
			{
				Text = "最终判定: " + _latestResult.Decision,
				FontWeight = FontWeights.Bold,
				FontSize = 16.0,
				Foreground = foreground,
				Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
			});
			if (_latestResult.Error != null)
			{
				InspectorPanel.Children.Add(new TextBlock
				{
					Text = "执行错误: " + _latestResult.Error,
					TextWrapping = TextWrapping.Wrap,
					Foreground = (Brush)FindResource("NgBrush"),
					FontSize = 11.0,
					Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
				});
			}
		}
		if (CheckCropRoiMismatch())
		{
			InspectorPanel.Children.Add(new TextBlock
			{
				Text = "⚠ 切图目录里的切图与当前 ROI 不一致——改过 ROI 后请重新切图并重训模型，否则分数不可比",
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)FindResource("NgBrush"),
				FontSize = 11.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
			});
		}
		if (_recipe == null)
		{
			return;
		}
		foreach (RecipeNode node in _recipe.Nodes)
		{
			bool enabled = node.Enabled;
			Dictionary<string, string> value = null;
			_latestResult?.NodeValues.TryGetValue(node.Name, out value);
			Brush foreground2 = ((!enabled || value == null) ? Brushes.Gray : ((!value.TryGetValue("decision", out var value2)) ? Brushes.Gray : ((value2 == "OK") ? ((Brush)FindResource("OkBrush")) : ((value2 == "NG") ? ((Brush)FindResource("NgBrush")) : Brushes.Orange))));
			StackPanel stackPanel = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0.0, 7.0, 0.0, 0.0)
			};
			stackPanel.Children.Add(new TextBlock
			{
				Text = "●",
				Foreground = foreground2,
				Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
				FontSize = 11.0
			});
			stackPanel.Children.Add(new TextBlock
			{
				Text = node.Name,
				FontWeight = FontWeights.SemiBold
			});
			if (!enabled)
			{
				stackPanel.Children.Add(new TextBlock
				{
					Text = "\u3000[停用]",
					Foreground = (Brush)FindResource("MutedTextBrush"),
					FontSize = 11.0
				});
			}
			InspectorPanel.Children.Add(stackPanel);
			string text = ((!enabled) ? "节点已停用" : ((value == null) ? "未执行" : BuildNodeStatusLine(value)));
			InspectorPanel.Children.Add(new TextBlock
			{
				Text = text,
				TextWrapping = TextWrapping.Wrap,
				FontSize = 11.0,
				Foreground = (Brush)FindResource("MutedTextBrush"),
				Margin = new Thickness(18.0, 1.0, 0.0, 0.0)
			});
		}
		InspectorPanel.Children.Add(new TextBlock
		{
			Text = "编辑节点参数：双击左侧节点行，或点节点行的 ⚙ 按钮",
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("MutedTextBrush"),
			FontSize = 11.0,
			Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
		});
	}

	private static string BuildNodeStatusLine(Dictionary<string, string> vals)
	{
		List<string> list = vals.Select<KeyValuePair<string, string>, string>((KeyValuePair<string, string> p) => (p.Key == "decision") ? ("结果: " + p.Value) : (p.Key + "=" + p.Value)).ToList();
		return (list.Count == 0) ? "已执行（无输出值）" : string.Join("  ", list);
	}

	private bool CheckCropRoiMismatch()
	{
		if (!((DateTime.Now - _lastRoiCheckAt).TotalSeconds < 5.0))
		{
			_lastRoiCheckAt = DateTime.Now;
			_lastRoiMismatch = ComputeCropRoiMismatch();
		}
		return _lastRoiMismatch == true;
	}

	private bool? ComputeCropRoiMismatch()
	{
		RecipeNode recipeNode = _recipe?.Nodes.FirstOrDefault((RecipeNode n) => n.Enabled && (n.Type == "PatchCore" || n.Type == "YOLO" || n.Type == "Seg" || n.Type == "SemanticSeg" || n.Type == "CharRec" || n.Type == "Blob"));
		if (recipeNode == null)
		{
			return false;
		}
		if (!recipeNode.Params.TryGetValue("crop_dir", out string dir) || string.IsNullOrWhiteSpace(dir))
		{
			return false;
		}
		// 检测项名来自节点私有 own_rois（方案级 ROI 库已移除，旧 rois 参数已废弃不再读）
		var names = NodeRois.ParseOwn(recipeNode.Params.GetValueOrDefault("own_rois"))
			.Select(r => r.Name)
			.Where((string s) => s.Length > 0)
			.ToList();
		if (names.Count == 0)
		{
			return false;
		}
		string text = new string[2] { "OK", "NG" }.Select((string sub) => System.IO.Path.Combine(dir, sub)).Where(Directory.Exists).SelectMany((string d) => Directory.EnumerateFiles(d, "*.jpg.json"))
			.OrderByDescending<string, string>((string f) => f, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault();
		if (text == null)
		{
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(text));
			if (!jsonDocument.RootElement.TryGetProperty("roi_name", out var value2) || value2.ValueKind != JsonValueKind.String)
			{
				return false;
			}
			string roiName = value2.GetString() ?? "";
			if (roiName.Length == 0)
			{
				return false; // 整图模式切图无检测项名，不算失配
			}
			// 最新切图引用的 ROI 名不在当前节点引用列表中 → 说明参数变了
			bool flag = !names.Contains(roiName, StringComparer.OrdinalIgnoreCase);
			if (flag && _lastRoiMismatch != true)
			{
				AppendLog("[校验] 切图目录 " + dir + " 最新切图引用的 ROI(" + roiName + ") 与当前节点引用(" + string.Join(",", names) + ") 不一致");
			}
			return flag;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>刷新右侧「模块结果」页签（跟随流程树选中节点；每轮执行结果落地后也会调用）。</summary>
	private void RefreshInspectorModuleResult()
	{
		var rn = SelectedRecipeNode;
		ModuleResultTitle.Text = rn is null
			? "模块结果（在左侧流程选择节点）"
			: $"模块结果 - {rn.Name}";
		if (rn is null)
		{
			InspectorResultView.Update(null, Array.Empty<ModuleRunRecord>());
			return;
		}
		InspectorResultView.Update(_moduleHistory.GetLatest(rn.Name), _moduleHistory.GetHistory(rn.Name));
	}

	/// <summary>取某节点最近一次执行的显示图（建模弹窗用；无则 false）。</summary>
	internal bool TryGetNodeThumb(string nodeName, out BitmapSource bmp)
	{
		if (_thumbImages.TryGetValue(nodeName, out var value))
		{
			bmp = value;
			return true;
		}
		bmp = null!;
		return false;
	}

	// ===== 硬触发驱动检测结果回显（CameraInspectionService 后台线程回调，均自行投 UI 线程）=====

	private void OnTriggerDrivenResult(DetectionResult result)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			_latestResult = new PipelineResult
			{
				Image = result.Image,
				Decision = result.Decision,
				Threshold = result.Threshold,
				ProcessedAt = result.ProcessedAt,
				Error = result.Error,
			};
			foreach (var (nodeName, nodeValues) in result.NodeDetails)
			{
				_latestResult.NodeValues[nodeName] = nodeValues;
			}
			ResultGrid.Items.Insert(0, result);
			while (ResultGrid.Items.Count > 200)
			{
				ResultGrid.Items.RemoveAt(ResultGrid.Items.Count - 1);
			}
			foreach (var (nodeName, nodeVals) in result.NodeDetails)
			{
				_moduleHistory.Record(nodeName, nodeVals);
			}
			_lastNodeValues = result.NodeDetails;
			RefreshInspectorModuleResult();
			ViewModel.FinalDecision = result.Decision;
			FinalVerdictText.Foreground = ((result.Decision == "OK") ? new SolidColorBrush(Color.FromRgb(29, 209, 161)) : ((!(result.Decision == "NG")) ? new SolidColorBrush(Color.FromRgb(245, 166, 35)) : new SolidColorBrush(Color.FromRgb(byte.MaxValue, 107, 107))));
			RefreshStatusPanel();
		}, Array.Empty<object>());
	}

    private void OnTriggerDrivenPreview(
        DetectionResult result,
        Mat? frame,
        float[,]? heatMap,
        IReadOnlyDictionary<string, Mat> nodeImages)
    {
        // 后台线程：转位图后立即释放 Mat（显示用位图与 Mat 解耦，不持有节点图像所有权）。
        BitmapSource? bitmap = null;
        var thumbnails = UiExtensions.ConvertNodeImages(nodeImages);
        try
        {
            if (frame != null)
            {
                bitmap = UiExtensions.MatToBitmap(frame);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            frame?.Dispose();
        }

		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			_nodeAnnotations.Clear();
			foreach (var (nodeName, shapes) in result.NodeAnnotations)
			{
				_nodeAnnotations[nodeName] = shapes;
			}
			_thumbImages.Clear();
            foreach (var (nodeName, thumbnail) in thumbnails)
            {
                _thumbImages[nodeName] = thumbnail;
            }
            if (_thumbStripVisible)
            {
                RefreshThumbStrip();
            }

            if (_selectedThumbName != null && _thumbImages.ContainsKey(_selectedThumbName))
            {
                ShowNodeImage(_selectedThumbName);
            }
            else if (_thumbImages.Count > 0)
            {
                ShowNodeImage(_thumbImages.Keys.Last());
            }
            if (_thumbImages.Count == 0)
            {
                ResultImage.Source = bitmap;
                ResultCaption.Text = bitmap == null
                    ? "触发结果（本轮无节点输出图像）"
                    : "触发检测: " + result.Image;
            }
            RenderRoiOverlay();
		}, Array.Empty<object>());
	}

	private void OnTriggerDrivenStateChanged(TriggerState state)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			UpdateStatePanel();
		}, Array.Empty<object>());
	}
}
