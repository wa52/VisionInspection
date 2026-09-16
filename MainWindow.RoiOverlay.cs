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
	private readonly List<System.Windows.Rect> _annotationLabelBounds = new();

	internal void ToggleRoiDraw(RecipeNode rn, NodeParamDialog dialog)
	{
		if (!EnsureUnlocked("进入绘制模式")) return;
		if (_roiDrawMode && _roiDrawNode == rn)
		{
			ExitRoiDrawMode();
			return;
		}
		ExitRoiDrawMode();
		// 先后顺序：ROI 坐标相对本节点的输出图。本节点还没执行过（无图）时不允许画——
		// 否则框会画在当前显示的其他节点图上，坐标映射到本节点后位置错误，表现为"画了 ROI 检测就不生效"。
		if (!_thumbImages.ContainsKey(rn.Name))
		{
			AppendLog("[ROI] 节点 " + rn.Name + " 还没有输出图：请先「单次执行」一次（无 ROI 也能全图执行），再画 ROI——先后顺序不能反；画完的框会显示在本节点自己的图上");
			return;
		}
		_roiDrawMode = true;
		_roiDrawNode = rn;
		_roiDialog = dialog;
		// 绘制目标与显示节点保持一致：自动切到本节点的输出图（叠加层只显示本节点 ROI）
		if (_thumbImages.ContainsKey(rn.Name))
		{
			ShowNodeImage(rn.Name);
		}
		RoiCanvas.IsHitTestVisible = true;
		RenderRoiOverlay();
		if (!DisplayedImageSize.HasValue)
		{
			AppendLog("[ROI] 绘制模式已开启（" + rn.Name + "）：当前没有可显示的图像，先执行一次再画框；画完再点一次弹窗里的绘制按钮退出");
		}
		else if (rn.Type == "CircleFind")
		{
			AppendLog("[ROI] 圆环绘制模式（" + rn.Name + "）：图像区按下定圆心、拖拽定外圆半径（内圆默认 0.6×外圆，画完可拖内圈手柄调整）；点击已有圆环可选中编辑，右键删除；画完再点一次绘制按钮退出");
		}
		else
		{
			AppendLog("[ROI] 绘制模式（" + rn.Name + "）：在中间图像区拖拽画框——每次画框都新建一个命名 ROI 并挂到本节点（支持多框）；点击已有 ROI 可移动/缩放/旋转，右键删除；画完再点一次绘制按钮退出");
		}
		dialog.NotifyRoiDrawChanged();
	}

	public bool IsRoiDrawActive(RecipeNode rn)
	{
		return _roiDrawMode && _roiDrawNode == rn;
	}

	internal void ExitRoiDrawMode()
	{
		bool roiDrawMode = _roiDrawMode;
		_roiDrawMode = false;
		_roiDrawNode = null;
		ResetRoiDragState();
		RoiCanvas.IsHitTestVisible = false;
		RoiCanvas.Cursor = Cursors.Arrow;
		if (roiDrawMode)
		{
			_roiDialog?.NotifyRoiDrawChanged();
			_roiDialog = null;
		}
		RenderRoiOverlay();
	}

	private void RoiCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!RoiCanvas.IsHitTestVisible) return;
		var size = DisplayedImageSize;
		if (size is null || _recipe is null) return;
		var position = e.GetPosition(RoiCanvas);
		var (item, item2) = RoiGeometry.DisplayToImage(position.X, position.Y, size.Value.Item1, size.Value.Item2, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		// 圆查找节点：圆环搜索区域分支（own_rings；矩形 ROI 机制不经过）
		if (FindDisplayedModelNode() is { Type: "CircleFind" } ringNode)
		{
			RingMouseDown(ringNode, position, (item, item2), size.Value, e);
			return;
		}
		// 命中测试：有位置修正预览时按修正后几何命中，否则按原始框
		Func<RoiRect, RoiHandle> hitOf = rect => RoiHitTest(position, size.Value, rect);
		IEnumerable<(int Idx, RoiRect Rect)> hitSource = _previewHits.Count > 0
			? _previewHits.Select((r, i) => (i, r))
			: _roiPolys.Select(p => (p.OwnIndex, p.Roi.ToRoiRect()));
		foreach (var (idx, rect) in hitSource)
		{
			var roiHandle = hitOf(rect);
			if (roiHandle == RoiHandle.None) continue;
			if (_roiSelIndex != idx)
			{
				// 防误触：第一下只选中（高亮+手柄切过去），不进入拖拽；已选中的框再按才拖动
				_roiSelIndex = idx;
				_roiManager?.SelectRoiIndex(idx);
				RenderRoiOverlay();
				e.Handled = true;
				return;
			}
			var editRect = _previewHits.Count > 0 && idx < _roiPolys.Count
				? _roiPolys[idx].Roi.ToRoiRect() // 拖拽编辑的是原始几何
				: rect;
			_roiDragHandle = roiHandle;
			_roiDragOrig = editRect;
			_roiDragOriginImage = (X: item, Y: item2);
			_roiDragStart = null;
			_roiPreviewRect = null;
			RoiCanvas.CaptureMouse();
			e.Handled = true;
			return;
		}
		if (_roiDrawMode)
		{
			// 空白处按下：绘制模式开始画新框
			_roiSelIndex = -1;
			_roiDragHandle = RoiHandle.None;
			_roiDragStart = new Point(item, item2);
			RoiCanvas.CaptureMouse();
			e.Handled = true;
			return;
		}
		// 非绘制模式点空白：取消选中
		if (_roiSelIndex >= 0)
		{
			_roiSelIndex = -1;
			RenderRoiOverlay();
		}
		e.Handled = true;
	}

	private void RoiCanvas_MouseMove(object sender, MouseEventArgs e)
	{
		if (!RoiCanvas.IsHitTestVisible) return;
		var size = DisplayedImageSize;
		if (size is null) return;
		var position = e.GetPosition(RoiCanvas);
		// 圆查找节点：圆环绘制预览/拖拽/悬停分支
		if (FindDisplayedModelNode() is { Type: "CircleFind" })
		{
			RingMouseMove(position, size.Value);
			e.Handled = true;
			return;
		}
		if (_roiDragStart.HasValue)
		{
			var (num, num2) = RoiGeometry.DisplayToImage(position.X, position.Y, size.Value.Item1, size.Value.Item2, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
			UpdateRoiPreview(_roiDragStart.Value, new Point(num, num2), size.Value);
			e.Handled = true;
			return;
		}
		if (_roiDragHandle != RoiHandle.None)
		{
			var dragOverride = ComputeDraggedRoi(position, size.Value);
			if (dragOverride.HasValue)
			{
				LayoutRoiOverlay(dragOverride);
			}
			e.Handled = true;
			return;
		}
		// 悬停光标：选中框的手柄/框体显示对应光标（非绘制模式也可编辑）
		var hitHandle = RoiHandle.None;
		IEnumerable<RoiRect> hoverSource = _previewHits.Count > 0 && _roiSelIndex >= 0 && _roiSelIndex < _previewHits.Count
			? new[] { _previewHits[_roiSelIndex] }
			: _roiPolys.Where(p => p.OwnIndex == _roiSelIndex).Select(p => p.Roi.ToRoiRect());
		foreach (var rect in hoverSource)
		{
			var h = RoiHitTest(position, size.Value, rect);
			if (h == RoiHandle.None) continue;
			hitHandle = h;
			break;
		}
		RoiCanvas.Cursor = (hitHandle == RoiHandle.None) ? Cursors.Arrow : CursorFor(hitHandle);
	}

	private void RoiCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!RoiCanvas.IsHitTestVisible) return;
		var size = DisplayedImageSize;
		if (size is null || _recipe is null)
		{
			ResetRoiDragState();
			return;
		}
		var position = e.GetPosition(RoiCanvas);
		// 圆查找节点：圆环新建/写回分支
		if (FindDisplayedModelNode() is { Type: "CircleFind" })
		{
			RingMouseUp(position, size.Value);
			e.Handled = true;
			return;
		}
		if (_roiDragStart.HasValue)
		{
			RoiCanvas.ReleaseMouseCapture();
			var (num, num2) = RoiGeometry.DisplayToImage(position.X, position.Y, size.Value.Item1, size.Value.Item2, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
			var num3 = Math.Min(_roiDragStart.Value.X, num);
			var num4 = Math.Min(_roiDragStart.Value.Y, num2);
			var num5 = Math.Abs(num - _roiDragStart.Value.X);
			var num6 = Math.Abs(num2 - _roiDragStart.Value.Y);
			var roiRect = RoiRect.FromPixelsCenter(num3 + num5 / 2, num4 + num6 / 2, num5, num6, 0.0, size.Value.Item1, size.Value.Item2);
			if (!roiRect.HasValue)
			{
				AppendLog("[ROI] 区域太小，已忽略");
			}
			else if (_roiDrawNode != null)
			{
				var items = NodeRois.ParseOwnFull(_roiDrawNode.Params.GetValueOrDefault("own_rois"));
				if (_redrawTargetIndex >= 0 && _redrawTargetIndex < items.Count)
				{
					// 重绘模式：新框替换选中检测项的几何（名字与元数据保留），画完自动退出绘制模式
					var keepName = items[_redrawTargetIndex].Name;
					items[_redrawTargetIndex] = items[_redrawTargetIndex] with { Rect = roiRect.Value };
					WriteOwnRoisFull(_roiDrawNode, items);
					AppendLog("[ROI] 已重绘检测项「" + keepName + "」→ " + roiRect.Value.Serialize() + "（节点 " + _roiDrawNode.Name + "）");
					_roiSelIndex = _redrawTargetIndex;
					_redrawTargetIndex = -1;
					ExitRoiDrawMode();
				}
				else
				{
					// 新建 ROI 只进本节点私有集（ROI 库已移除）；预填节点类型默认阈值
					var ownName = NodeRois.UniqueOwnName(items.Select(i => (i.Name, i.Rect)).ToList(), "ROI");
					items.Add(new RoiItem(ownName, roiRect.Value, new RoiMeta(Threshold: DefaultThresholdFor(_roiDrawNode))));
					WriteOwnRoisFull(_roiDrawNode, items);
					AppendLog("[ROI] 新建本节点 ROI「" + ownName + "」（节点 " + _roiDrawNode.Name + " 私有）");
					_roiSelIndex = items.Count - 1;
					_roiDialog?.NotifyRoiChanged();
				}
				_roiManager?.RefreshExternal();
			}
			ResetRoiDragState();
			RenderRoiOverlay();
			e.Handled = true;
			return;
		}
		if (_roiDragHandle != RoiHandle.None)
		{
			RoiCanvas.ReleaseMouseCapture();
			var roiRect2 = ComputeDraggedRoi(position, size.Value);
			var draggedIdx = _roiSelIndex;
			if (roiRect2.HasValue && draggedIdx >= 0)
			{
				// own：写回本节点私有集（索引定位，几何+元数据保留）
				var drawNode = _roiDrawNode ?? FindDisplayedModelNode();
				if (drawNode != null)
				{
					var items = NodeRois.ParseOwnFull(drawNode.Params.GetValueOrDefault("own_rois"));
					if (draggedIdx < items.Count)
					{
						var name = items[draggedIdx].Name;
						items[draggedIdx] = items[draggedIdx] with { Rect = roiRect2.Value };
						WriteOwnRoisFull(drawNode, items);
						AppendLog("[ROI] 检测项「" + name + "」几何已更新 -> " + roiRect2.Value.Serialize() + "（热生效）");
						_roiManager?.RefreshExternal();
					}
				}
			}
			ResetRoiDragState();
			RenderRoiOverlay();
			e.Handled = true;
		}
	}

	private void RoiCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!RoiCanvas.IsHitTestVisible) return;
		var size = DisplayedImageSize;
		if (size is null || _recipe is null) return;
		var position = e.GetPosition(RoiCanvas);
		// 圆查找节点：圆环右键删除分支
		if (FindDisplayedModelNode() is { Type: "CircleFind" })
		{
			RingRightButtonUp(position, size.Value, e);
			return;
		}
		foreach (var (entry, _, own, ownIdx) in _roiPolys.ToList())
		{
			if (RoiHitTest(position, size.Value, entry.ToRoiRect()) == RoiHandle.None) continue;
			if (own && ownIdx >= 0)
			{
				// own：只从本节点私有集删除（索引定位；ROI 库已移除）
				var drawNode = _roiDrawNode ?? FindDisplayedModelNode();
				if (drawNode != null)
				{
					var items = NodeRois.ParseOwnFull(drawNode.Params.GetValueOrDefault("own_rois"));
					if (ownIdx < items.Count)
					{
						var name = items[ownIdx].Name;
						items.RemoveAt(ownIdx);
						WriteOwnRoisFull(drawNode, items);
						AppendLog("[ROI] 已删除检测项「" + name + "」（节点 " + drawNode.Name + " 私有）");
						_roiSelIndex = -1;
						_roiDialog?.NotifyRoiChanged();
						_roiManager?.RefreshExternal();
					}
				}
			}
			e.Handled = true;
			return;
		}
	}

	private void ResetRoiDragState()
	{
		_roiDragStart = null;
		_roiPreviewRect = null;
		_roiDragHandle = RoiHandle.None;
		_ringPreviewOuter = null;
		_ringPreviewInner = null;
	}

	private RoiRect? ComputeDraggedRoi(Point canvasPos, (int W, int H) size)
	{
		(double X, double Y) tuple = RoiGeometry.DisplayToImage(canvasPos.X, canvasPos.Y, size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		double item = tuple.X;
		double item2 = tuple.Y;
		(double, double, int, int, double) tuple2 = _roiDragOrig.ToPixels(size.W, size.H);
		switch (_roiDragHandle)
		{
		case RoiHandle.Body:
		{
			double cx2 = tuple2.Item1 + (item - _roiDragOriginImage.X);
			double cy2 = tuple2.Item2 + (item2 - _roiDragOriginImage.Y);
			return RoiRect.FromPixelsCenter(cx2, cy2, tuple2.Item3, tuple2.Item4, tuple2.Item5, size.W, size.H);
		}
		case RoiHandle.Rotate:
		{
			double angle = Math.Atan2(item - tuple2.Item1, 0.0 - (item2 - tuple2.Item2)) * 180.0 / Math.PI;
			return RoiRect.FromPixelsCenter(tuple2.Item1, tuple2.Item2, tuple2.Item3, tuple2.Item4, angle, size.W, size.H);
		}
		case RoiHandle.None:
			return null;
		default:
		{
			(double Lx, double Ly) tuple3 = RoiGeometry.ToLocal(item, item2, tuple2.Item1, tuple2.Item2, tuple2.Item5);
			double item3 = tuple3.Lx;
			double item4 = tuple3.Ly;
			double maxSize = Math.Sqrt((double)size.W * (double)size.W + (double)size.H * (double)size.H);
			var (w, h, lx, ly) = RoiGeometry.ApplyRotatedEdgeDrag(_roiDragHandle, tuple2.Item3, tuple2.Item4, item3, item4, 2.0, maxSize);
			var (cx, cy) = RoiGeometry.ToImage(lx, ly, tuple2.Item1, tuple2.Item2, tuple2.Item5);
			return RoiRect.FromPixelsCenter(cx, cy, w, h, tuple2.Item5, size.W, size.H);
		}
		}
	}

	private RoiHandle RoiHitTest(Point canvasPos, (int W, int H) size, RoiRect roi)
	{
		double item = RoiGeometry.UniformFit(size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight).Scale;
		if (item <= 0.0)
		{
			return RoiHandle.None;
		}
		(double Cx, double Cy, int W, int H, double Angle) tuple = roi.ToPixels(size.W, size.H);
		double item2 = tuple.Cx;
		double item3 = tuple.Cy;
		int item4 = tuple.W;
		int item5 = tuple.H;
		double item6 = tuple.Angle;
		(double X, double Y) tuple2 = RoiGeometry.DisplayToImage(canvasPos.X, canvasPos.Y, size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		double item7 = tuple2.X;
		double item8 = tuple2.Y;
		double num = 24.0 / item;
		(double X, double Y) tuple3 = RoiGeometry.ToImage(0.0, 0.0 - ((double)item5 / 2.0 + num), item2, item3, item6);
		double item9 = tuple3.X;
		double item10 = tuple3.Y;
		double num2 = Math.Sqrt((item7 - item9) * (item7 - item9) + (item8 - item10) * (item8 - item10));
		if (num2 <= 7.0 / item * 1.5)
		{
			return RoiHandle.Rotate;
		}
		double tol = 7.0 / item;
		var (px, py) = RoiGeometry.ToLocal(item7, item8, item2, item3, item6);
		return RoiGeometry.HitTest((double)(-item4) / 2.0, (double)(-item5) / 2.0, item4, item5, px, py, tol);
	}

	private Cursor CursorFor(RoiHandle hit)
	{
		if (1 == 0)
		{
		}
		Cursor result;
		switch (hit)
		{
		case RoiHandle.TopLeft:
		case RoiHandle.BottomRight:
			result = Cursors.SizeNWSE;
			break;
		case RoiHandle.TopRight:
		case RoiHandle.BottomLeft:
			result = Cursors.SizeNESW;
			break;
		case RoiHandle.Right:
		case RoiHandle.Left:
			result = Cursors.SizeWE;
			break;
		case RoiHandle.Top:
		case RoiHandle.Bottom:
			result = Cursors.SizeNS;
			break;
		case RoiHandle.Body:
			result = Cursors.SizeAll;
			break;
		case RoiHandle.RingOuter:
		case RoiHandle.RingInner:
			result = Cursors.SizeNS;
			break;
		case RoiHandle.Rotate:
			result = Cursors.Hand;
			break;
		default:
			result = Cursors.Cross;
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private void UpdateRoiPreview(Point imgStart, Point imgEnd, (int W, int H) size)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		Point val = ImageToDisplayPoint(imgStart, size.W, size.H);
		Point val2 = ImageToDisplayPoint(imgEnd, size.W, size.H);
		if (_roiPreviewRect == null)
		{
			_roiPreviewRect = new Rectangle
			{
				Stroke = Brushes.Yellow,
				StrokeThickness = 2.0,
				StrokeDashArray = new DoubleCollection { 4.0, 2.0 }
			};
			RoiCanvas.Children.Add(_roiPreviewRect);
		}
		Canvas.SetLeft(_roiPreviewRect, Math.Min(val.X, val2.X));
		Canvas.SetTop(_roiPreviewRect, Math.Min(val.Y, val2.Y));
		_roiPreviewRect.Width = Math.Abs(val2.X - val.X);
		_roiPreviewRect.Height = Math.Abs(val2.Y - val.Y);
	}

	private Point ImageToDisplayPoint(Point p, int imgW, int imgH)
	{
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		var (num, num2, num3) = RoiGeometry.UniformFit(imgW, imgH, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		return new Point(num2 + p.X * num, num3 + p.Y * num);
	}

	// ===== 圆查找节点：圆环搜索区域叠加层（own_rings；矩形 ROI 机制不经过此分支） =====

	/// <summary>渲染圆查找节点的圆环叠加层：外圆实线 + 内圆虚线；选中时东向两个半径手柄。
	/// 位置修正预览（同矩形路径）：本节点被勾选为修正目标时，非选中项只显示修正后的圆环（青色虚线），选中项额外显示原环+手柄编辑。</summary>
	private void RenderRings(RecipeNode node, (int W, int H) size)
	{
		var rings = NodeRings.Parse(node.Params.GetValueOrDefault("own_rings"));
		RoiCanvas.IsHitTestVisible = !_uiLocked && (_roiDrawMode || rings.Count > 0);
		var preview = BuildPreviewCorrection(node);
		var previewActive = preview is not null && !_roiDrawMode;
		for (var i = 0; i < rings.Count; i++)
		{
			var (name, ring) = rings[i];
			if (previewActive)
			{
				var mapped = NodeRings.ApplyPoseCorrection([(name, ring)], node.Name, size.W, size.H, preview);
				var mappedRing = mapped[0].Ring;
				_ringPreviewHits.Add(mappedRing);
				var (mcx, mcy, mro, mri) = mappedRing.ToPixels(size.W, size.H);
				AddRingPreviewCircle(mcx, mcy, mro, size, "搜索圆环「" + name + "」修正后的位置（第" + (i + 1) + "个）；选中后拖蓝色圆环调整原几何");
				if (mri > 0.0)
				{
					AddRingPreviewCircle(mcx, mcy, mri, size, null);
				}
				if (_roiSelIndex != i)
				{
					continue; // 非选中项只画修正环
				}
			}
			var isSel = _roiSelIndex == i;
			var outer = new Ellipse
			{
				Stroke = isSel ? Brushes.DodgerBlue : Brushes.Orange,
				StrokeThickness = isSel ? 2.0 : 1.2,
				Fill = Brushes.Transparent,
				IsHitTestVisible = false,
				ToolTip = "搜索圆环「" + name + "」 第" + (i + 1) + "个（本节点私有）：拖外圆调外径，拖内圆调内径，拖内部移动"
			};
			var inner = new Ellipse
			{
				Stroke = isSel ? Brushes.DodgerBlue : Brushes.Orange,
				StrokeThickness = 1.0,
				StrokeDashArray = new DoubleCollection { 3.0, 3.0 },
				Fill = Brushes.Transparent,
				IsHitTestVisible = false
			};
			RoiCanvas.Children.Add(outer);
			RoiCanvas.Children.Add(inner);
			_ringPolys.Add((name, ring, outer, inner, i));
		}
		if (_roiSelIndex >= 0 && _roiSelIndex < rings.Count)
		{
			for (var k = 0; k < 2; k++)
			{
				Rectangle handle = new Rectangle
				{
					Width = 7.0,
					Height = 7.0,
					Fill = Brushes.White,
					Stroke = Brushes.DodgerBlue,
					StrokeThickness = 1.0,
					IsHitTestVisible = false,
					ToolTip = k == 0 ? "拖拽调整外圆半径" : "拖拽调整内圆半径"
				};
				_roiHandleRects.Add(handle);
				RoiCanvas.Children.Add(handle);
			}
		}
		LayoutRings();
	}

	/// <summary>画单个圆环「修正后」的预览圆（青色虚线，屏幕矢量层）。</summary>
	private void AddRingPreviewCircle(double cx, double cy, double rImg, (int W, int H) size, string? toolTip)
	{
		var (scale, ox, oy) = RoiGeometry.UniformFit(size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		Ellipse ellipse = new Ellipse
		{
			Stroke = Brushes.Cyan,
			StrokeThickness = 1.4,
			StrokeDashArray = new DoubleCollection { 4.0, 3.0 },
			Fill = Brushes.Transparent,
			IsHitTestVisible = false,
			ToolTip = toolTip
		};
		RoiCanvas.Children.Add(ellipse);
		var r = Math.Max(0.0, rImg * scale);
		ellipse.Width = 2.0 * r;
		ellipse.Height = 2.0 * r;
		Canvas.SetLeft(ellipse, ox + cx * scale - r);
		Canvas.SetTop(ellipse, oy + cy * scale - r);
	}

	/// <summary>摆放圆环叠加层几何（实时拖拽经 dragOverride 重摆，不整层重渲染）。</summary>
	private void LayoutRings(RoiRing? dragOverride = null)
	{
		var size = DisplayedImageSize;
		if (!size.HasValue || _ringPolys.Count == 0)
		{
			return;
		}
		var (scale, ox, oy) = RoiGeometry.UniformFit(size.Value.Item1, size.Value.Item2, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		if (scale <= 0.0)
		{
			return;
		}
		foreach (var (name, ring, outer, inner, idx) in _ringPolys)
		{
			var isSel = _roiSelIndex == idx;
			var r = isSel && dragOverride.HasValue ? dragOverride.Value : ring;
			var (cx, cy, ro, ri) = r.ToPixels(size.Value.Item1, size.Value.Item2);
			PlaceCircleAt(outer, ox, oy, cx, cy, ro, scale);
			if (ri > 0.0)
			{
				inner.Visibility = Visibility.Visible;
				PlaceCircleAt(inner, ox, oy, cx, cy, ri, scale);
			}
			else
			{
				inner.Visibility = Visibility.Collapsed;
			}
			if (isSel && _roiHandleRects.Count == 2)
			{
				Canvas.SetLeft(_roiHandleRects[0], ox + (cx + ro) * scale - 3.5);
				Canvas.SetTop(_roiHandleRects[0], oy + cy * scale - 3.5);
				Canvas.SetLeft(_roiHandleRects[1], ox + (cx + ri) * scale - 3.5);
				Canvas.SetTop(_roiHandleRects[1], oy + cy * scale - 3.5);
			}
		}
	}

	private void PlaceCircleAt(Ellipse ellipse, double ox, double oy, double cx, double cy, double rImg, double scale)
	{
		var r = Math.Max(0.0, rImg * scale);
		ellipse.Width = 2.0 * r;
		ellipse.Height = 2.0 * r;
		Canvas.SetLeft(ellipse, ox + cx * scale - r);
		Canvas.SetTop(ellipse, oy + cy * scale - r);
	}

	/// <summary>圆环绘制预览：按下点=圆心，拖拽距离=外圆半径，内圆默认 0.6×外圆（黄实线/黄虚线）。</summary>
	private void UpdateRingPreview(Point imgStart, Point imgCur, (int W, int H) size)
	{
		var ro = Math.Sqrt((imgCur.X - imgStart.X) * (imgCur.X - imgStart.X) + (imgCur.Y - imgStart.Y) * (imgCur.Y - imgStart.Y));
		var ri = Math.Min(0.6 * ro, Math.Max(0.0, ro - 4.0));
		var (scale, ox, oy) = RoiGeometry.UniformFit(size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		if (_ringPreviewOuter == null)
		{
			_ringPreviewOuter = new Ellipse
			{
				Stroke = Brushes.Yellow,
				StrokeThickness = 2.0,
				IsHitTestVisible = false
			};
			RoiCanvas.Children.Add(_ringPreviewOuter);
		}
		if (_ringPreviewInner == null)
		{
			_ringPreviewInner = new Ellipse
			{
				Stroke = Brushes.Yellow,
				StrokeThickness = 1.2,
				StrokeDashArray = new DoubleCollection { 4.0, 3.0 },
				IsHitTestVisible = false
			};
			RoiCanvas.Children.Add(_ringPreviewInner);
		}
		PlaceCircleAt(_ringPreviewOuter, ox, oy, imgStart.X, imgStart.Y, ro, scale);
		PlaceCircleAt(_ringPreviewInner, ox, oy, imgStart.X, imgStart.Y, ri, scale);
	}

	/// <summary>圆环命中测试：外圆/内圆圈线附近 → 半径柄；环带或内圆内部 → 主体移动。</summary>
	private RoiHandle RingHitTest(Point canvasPos, (int W, int H) size, RoiRing ring)
	{
		var scale = RoiGeometry.UniformFit(size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight).Scale;
		if (scale <= 0.0)
		{
			return RoiHandle.None;
		}
		var (x, y) = RoiGeometry.DisplayToImage(canvasPos.X, canvasPos.Y, size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		var (cx, cy, ro, ri) = ring.ToPixels(size.W, size.H);
		var tol = RoiHandleTolerancePx / scale;
		var dx = x - cx;
		var dy = y - cy;
		var d = Math.Sqrt(dx * dx + dy * dy);
		var dOuter = Math.Abs(d - ro);
		var dInner = Math.Abs(d - ri);
		if (dOuter <= tol && (ri <= 0.0 || dOuter <= dInner)) return RoiHandle.RingOuter;
		if (ri > 0.0 && dInner <= tol) return RoiHandle.RingInner;
		if (d <= ro + tol) return RoiHandle.Body;
		return RoiHandle.None;
	}

	/// <summary>圆环拖拽几何：Body=移心；RingOuter/RingInner=调半径（钳制保持环宽 ≥4px、几何落在图像内）。</summary>
	private RoiRing? ComputeDraggedRing(Point canvasPos, (int W, int H) size)
	{
		var (x, y) = RoiGeometry.DisplayToImage(canvasPos.X, canvasPos.Y, size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		var (cx, cy, ro, ri) = _ringDragOrig.ToPixels(size.W, size.H);
		switch (_roiDragHandle)
		{
			case RoiHandle.Body:
			{
				var nx = cx + (x - _roiDragOriginImage.X);
				var ny = cy + (y - _roiDragOriginImage.Y);
				return RoiRing.FromPixelsCenter(nx, ny, ro, ri, size.W, size.H);
			}
			case RoiHandle.RingOuter:
			{
				var nr = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
				if (nr < ri + 4.0) nr = ri + 4.0;
				return RoiRing.FromPixelsCenter(cx, cy, nr, ri, size.W, size.H);
			}
			case RoiHandle.RingInner:
			{
				var nr = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
				if (nr > ro - 4.0) nr = ro - 4.0;
				if (nr < 0.0) nr = 0.0;
				return RoiRing.FromPixelsCenter(cx, cy, ro, nr, size.W, size.H);
			}
			default:
				return null;
		}
	}

	private void RingMouseDown(RecipeNode node, Point canvasPos, (double X, double Y) imgPos, (int W, int H) size, MouseButtonEventArgs e)
	{
		// 防误触（同矩形）：第一下只选中（高亮+手柄切过去），已选中的环再按住才拖拽。
		// 修正预览激活时按修正后几何命中，编辑的原几何从节点参数现读（同矩形 _previewHits 语义）。
		var rings = NodeRings.Parse(node.Params.GetValueOrDefault("own_rings"));
		for (var i = 0; i < rings.Count; i++)
		{
			var hitRing = _ringPreviewHits.Count > 0 && i < _ringPreviewHits.Count ? _ringPreviewHits[i] : rings[i].Ring;
			var h = RingHitTest(canvasPos, size, hitRing);
			if (h == RoiHandle.None) continue;
			if (_roiSelIndex != i)
			{
				_roiSelIndex = i;
				RenderRoiOverlay();
				e.Handled = true;
				return;
			}
			_roiDragHandle = h;
			_ringDragOrig = rings[i].Ring;
			_roiDragOriginImage = imgPos;
			_roiDragStart = null;
			_roiPreviewRect = null;
			RoiCanvas.CaptureMouse();
			e.Handled = true;
			return;
		}
		if (_roiDrawMode)
		{
			// 空白处按下：绘制模式开始画新圆环（按下定圆心，拖拽定外圆半径）
			_roiSelIndex = -1;
			_roiDragHandle = RoiHandle.None;
			_roiDragStart = new Point(imgPos.X, imgPos.Y);
			RoiCanvas.CaptureMouse();
			e.Handled = true;
			return;
		}
		// 非绘制模式点空白：取消选中
		if (_roiSelIndex >= 0)
		{
			_roiSelIndex = -1;
			RenderRoiOverlay();
		}
		e.Handled = true;
	}

	private void RingMouseMove(Point canvasPos, (int W, int H) size)
	{
		if (_roiDragStart.HasValue)
		{
			var (x, y) = RoiGeometry.DisplayToImage(canvasPos.X, canvasPos.Y, size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
			UpdateRingPreview(_roiDragStart.Value, new Point(x, y), size);
			return;
		}
		if (_roiDragHandle != RoiHandle.None)
		{
			var dragged = ComputeDraggedRing(canvasPos, size);
			if (dragged.HasValue)
			{
				LayoutRings(dragged);
			}
			return;
		}
		// 悬停光标：选中圆环的手柄/主体显示对应光标（修正预览激活时按修正后几何）
		var hitHandle = RoiHandle.None;
		if (_roiSelIndex >= 0)
		{
			RoiRing hitRing;
			if (_ringPreviewHits.Count > 0 && _roiSelIndex < _ringPreviewHits.Count)
			{
				hitRing = _ringPreviewHits[_roiSelIndex];
			}
			else
			{
				var selected = _ringPolys.FirstOrDefault(p => p.RingIndex == _roiSelIndex);
				hitRing = selected == default ? default : selected.Ring;
			}
			hitHandle = RingHitTest(canvasPos, size, hitRing);
		}
		RoiCanvas.Cursor = hitHandle switch
		{
			RoiHandle.RingOuter or RoiHandle.RingInner => Cursors.SizeNS,
			RoiHandle.Body => Cursors.SizeAll,
			_ => Cursors.Arrow,
		};
	}

	private void RingMouseUp(Point canvasPos, (int W, int H) size)
	{
		var node = FindDisplayedModelNode();
		if (node == null)
		{
			ResetRoiDragState();
			return;
		}
		if (_roiDragStart.HasValue)
		{
			RoiCanvas.ReleaseMouseCapture();
			var (x, y) = RoiGeometry.DisplayToImage(canvasPos.X, canvasPos.Y, size.W, size.H, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
			var cx = _roiDragStart.Value.X;
			var cy = _roiDragStart.Value.Y;
			var ro = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
			var ri = Math.Min(0.6 * ro, Math.Max(0.0, ro - 4.0));
			var ring = RoiRing.FromPixelsCenter(cx, cy, ro, ri, size.W, size.H);
			if (!ring.HasValue)
			{
				AppendLog("[ROI] 圆环太小，已忽略（外圆半径至少 4px）");
			}
			else
			{
				var rings = NodeRings.Parse(node.Params.GetValueOrDefault("own_rings"));
				var name = NodeRings.UniqueName(rings, "圆环1");
				rings.Add((name, ring.Value));
				WriteOwnRings(node, rings);
				AppendLog($"[ROI] 新建本节点圆环「{name}」（外圆 R={ro:F0}px 内圆 R={ri:F0}px；节点 {node.Name} 私有，拖内圈手柄可调）");
				_roiSelIndex = rings.Count - 1;
				_roiDialog?.NotifyRoiChanged();
			}
			ResetRoiDragState();
			RenderRoiOverlay();
			return;
		}
		if (_roiDragHandle != RoiHandle.None)
		{
			RoiCanvas.ReleaseMouseCapture();
			var dragged = ComputeDraggedRing(canvasPos, size);
			var draggedIdx = _roiSelIndex;
			if (dragged.HasValue && draggedIdx >= 0)
			{
				var rings = NodeRings.Parse(node.Params.GetValueOrDefault("own_rings"));
				if (draggedIdx < rings.Count)
				{
					var name = rings[draggedIdx].Name;
					rings[draggedIdx] = (name, dragged.Value);
					WriteOwnRings(node, rings);
					AppendLog("[ROI] 圆环「" + name + "」几何已更新 -> " + dragged.Value.Serialize() + "（热生效）");
				}
			}
			ResetRoiDragState();
			RenderRoiOverlay();
		}
	}

	private void RingRightButtonUp(Point canvasPos, (int W, int H) size, MouseButtonEventArgs e)
	{
		var node = FindDisplayedModelNode();
		if (node == null) return;
		foreach (var entry in _ringPolys.ToList())
		{
			if (RingHitTest(canvasPos, size, entry.Ring) == RoiHandle.None) continue;
			var rings = NodeRings.Parse(node.Params.GetValueOrDefault("own_rings"));
			if (entry.RingIndex < rings.Count)
			{
				var name = rings[entry.RingIndex].Name;
				rings.RemoveAt(entry.RingIndex);
				WriteOwnRings(node, rings);
				AppendLog("[ROI] 已删除圆环「" + name + "」（节点 " + node.Name + " 私有）");
				_roiSelIndex = -1;
				_roiDialog?.NotifyRoiChanged();
				RenderRoiOverlay();
			}
			e.Handled = true;
			return;
		}
	}

	/// <summary>当前显示节点（缩略图选中的）对应的方案节点；非模型节点返回 null。
	/// ROI 绘制模式下跟随绘制目标节点——目标可能还没有执行图像（无法被缩略图选中），否则画完的框不显示。</summary>
	private RecipeNode? FindDisplayedModelNode()
	{
		if (_recipe == null)
		{
			return null;
		}
		if (_roiDrawMode && _roiDrawNode != null)
		{
			return _recipe.Nodes.FirstOrDefault((RecipeNode n) => string.Equals(n.Name, _roiDrawNode.Name, StringComparison.OrdinalIgnoreCase)
				&& n.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch" or "FastMatch" or "CharRec" or "Blob" or "LineFind" or "CircleFind");
		}
		if (string.IsNullOrWhiteSpace(_selectedThumbName))
		{
			return null;
		}
		return _recipe.Nodes.FirstOrDefault((RecipeNode n) =>
			string.Equals(n.Name, _selectedThumbName, StringComparison.OrdinalIgnoreCase)
			&& n.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch" or "FastMatch" or "CharRec" or "Blob" or "LineFind" or "CircleFind");
	}

	/// <summary>
	/// 叠加层可见 ROI 集：跟随当前显示的节点——
	/// own 模式节点只显示其私有 ROI；inherit 模式节点只显示其 rois 引用的库 ROI；
	/// 未选中/非模型节点（如图像源）不显示任何 ROI——避免把别的节点的 ROI 画到不相关图像上造成混淆
	/// （方案级 ROI 库的管理走「ROI 库」弹窗，不依赖叠加层）。
	/// </summary>
	private (List<RecipeRoi> Items, bool Own) VisibleRois()
	{
		var empty = (new List<RecipeRoi>(), false);
		if (_recipe == null) return empty;
		var node = FindDisplayedModelNode();
		if (node == null)
		{
			return empty;
		}
		// ROI 库已移除：只显示本节点私有 ROI
		return (NodeRois.ParseOwn(node.Params.GetValueOrDefault("own_rois"))
			.Select(r => RecipeRoi.FromRoiRect(r.Name, r.Rect))
			.ToList(), true);
	}

	/// <summary>own_rois 的写回辅助：更新节点私有 ROI 集（覆盖式）并热生效（不含元数据，旧格式）。</summary>
	private void WriteOwnRois(RecipeNode rn, List<(string Name, RoiRect Rect)> rois)
	{
		if (!EnsureUnlocked("修改检测区域")) return;
		rn.Params["own_rois"] = NodeRois.SerializeOwn(rois);
		HotApplyParam(rn, "own_rois", rn.Params["own_rois"]);
	}

	/// <summary>own_rois 的写回辅助（含检测项元数据，覆盖式热生效）。</summary>
	private void WriteOwnRoisFull(RecipeNode rn, List<RoiItem> items)
	{
		if (!EnsureUnlocked("修改检测区域")) return;
		rn.Params["own_rois"] = NodeRois.SerializeOwnFull(items);
		HotApplyParam(rn, "own_rois", rn.Params["own_rois"]);
	}

	/// <summary>own_rings 的写回辅助（圆查找节点圆环搜索区域，覆盖式热生效）。</summary>
	private void WriteOwnRings(RecipeNode rn, List<(string Name, RoiRing Ring)> rings)
	{
		if (!EnsureUnlocked("修改搜索圆环")) return;
		rn.Params["own_rings"] = NodeRings.Serialize(rings);
		HotApplyParam(rn, "own_rings", rn.Params["own_rings"]);
	}

	/// <summary>节点类型对应的检测项默认阈值（节点级总阈值已移除，新建检测项预填；PatchCore 用模型训练阈值）。</summary>
	internal string DefaultThresholdFor(RecipeNode rn) => rn.Type switch
	{
		"YOLO" => YoloNode.DefaultConf,
		"Seg" => SegNode.DefaultPercent,
		"SemanticSeg" => SemanticSegNode.DefaultPercent,
		"CharRec" => CharRecNode.DefaultConf,
		"Blob" => BlobNode.DefaultThreshold,
		"PatchCore" => ReadModelThreshold(rn)?.ToString("F4") ?? "0.5",
		_ => "",
	};

	/// <summary>设置选中检测项的元数据（检测项管理弹窗编辑用，索引定位，热生效）。</summary>
	internal void SetRoiMeta(RecipeNode rn, int index, RoiMeta meta)
	{
		if (!EnsureUnlocked("修改检测项参数")) return;
		var items = NodeRois.ParseOwnFull(rn.Params.GetValueOrDefault("own_rois"));
		if (index < 0 || index >= items.Count) return;
		items[index] = items[index] with { Meta = meta };
		WriteOwnRoisFull(rn, items);
		AppendLog($"[ROI] 节点 {rn.Name}: 检测项「{items[index].Name}」→ {(meta.Enabled ? "启用" : "停用")} / {(NodeRois.Judges(meta) ? "参与判定" : "仅观察")} / 阈值 {(meta.Threshold.Length > 0 ? meta.Threshold : "默认")} / {(meta.SaveImage ? "存图" : "不存图")}{(meta.Target.Length > 0 ? $" / 目标字符 {meta.Target}" : "")}");
	}

	/// <summary>
	/// 构造位置修正预览：取上游已启用的位置修正节点（target_nodes 含当前节点），
	/// 用其基准位姿 + 定位来源最近一次执行的 loc_* 位姿模拟变换；任一条件不满足返回 null。
	/// </summary>
	private PoseCorrection? BuildPreviewCorrection(RecipeNode node)
	{
		if (_recipe is null || _lastNodeValues is null) return null;
		foreach (var n in _recipe.Nodes)
		{
			if (n.Type != "PositionCorrection" || !n.Enabled) continue;
			var targets = (n.Params.GetValueOrDefault("target_nodes") ?? "")
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(s => s.Length > 0)
				.ToList();
			if (!targets.Any(t => string.Equals(t, node.Name, StringComparison.OrdinalIgnoreCase))) continue;
			if (!double.TryParse(n.Params.GetValueOrDefault("base_x"), out var bx) ||
				!double.TryParse(n.Params.GetValueOrDefault("base_y"), out var by) ||
				!double.TryParse(n.Params.GetValueOrDefault("base_angle"), out var ba))
			{
				return null; // 基准未设置：无法预览
			}
			var source = (n.Params.GetValueOrDefault("source") ?? "").Trim();
			if (source.Length == 0 || !_lastNodeValues.TryGetValue(source, out var vals)) return null;
			if (!double.TryParse(vals.GetValueOrDefault("loc_x"), out var lx) ||
				!double.TryParse(vals.GetValueOrDefault("loc_y"), out var ly) ||
				!double.TryParse(vals.GetValueOrDefault("loc_angle"), out var la))
			{
				return null; // 还没有定位结果
			}
			return new PoseCorrection(n.Name, targets, bx, by, ba, lx, ly, la);
		}
		return null;
	}

	private void RenderRoiOverlay()
	{
		RoiCanvas.Children.Clear();
		_roiPolys.Clear();
		_ringPolys.Clear();
		_ringPreviewHits.Clear();
		_roiHandleRects.Clear();
		_roiActivePoly = null;
		_roiRotateHandle = null;
		_roiPreviewRect = null;
		_ringPreviewOuter = null;
		_ringPreviewInner = null;
		if (_roiDragHandle != RoiHandle.None)
		{
			return;
		}
		var displayedImageSize = DisplayedImageSize;
		if (!displayedImageSize.HasValue || _recipe == null)
		{
			return;
		}
		// 圆查找节点：叠加层走圆环分支（矩形 ROI 机制不经过）
		if (FindDisplayedModelNode() is { Type: "CircleFind" } ringNode)
		{
			RenderRings(ringNode, displayedImageSize.Value);
			RenderNodeAnnotations();
			return;
		}
		var (visible, ownMode) = VisibleRois();
		_previewHits.Clear();
		// 画布交互：锁定时禁用（防误触）；否则绘制模式（画新框/编辑）或有可编辑 ROI 时启用
		RoiCanvas.IsHitTestVisible = !_uiLocked && (_roiDrawMode || visible.Count > 0);
		// 位置修正预览：当前节点被修正时，非绘制模式只显示修正后的框（青色虚线）；选中项才显示原始框+手柄供编辑
		var previewNode2 = FindDisplayedModelNode();
		var preview = previewNode2 is null ? null : BuildPreviewCorrection(previewNode2);
		var previewActive = preview is not null && !_roiDrawMode;
		for (int i = 0; i < visible.Count; i++)
		{
			var entry = visible[i];
			if (previewActive)
			{
				// 修正后几何（青色虚线，命中测试用）；原始框只对选中项显示
				var (imgW0, imgH0) = displayedImageSize.Value;
				var mappedOne = NodeRois.ApplyPoseCorrection(
					new List<(string, RoiRect)> { (entry.Name, entry.ToRoiRect()) }, previewNode2!.Name, imgW0, imgH0, preview);
				var mappedRect = mappedOne[0].Item2;
				_previewHits.Add(mappedRect);
				if (_roiSelIndex != i)
				{
					AddPreviewPolygon(mappedRect, imgW0, imgH0, entry.Name, i);
					continue;
				}
				// 选中项：青色框 + 原始框（编辑态）都画
				AddPreviewPolygon(mappedRect, imgW0, imgH0, entry.Name, i);
			}
			var flag = _roiDrawMode && _roiSelIndex == i;
			// 检测项管理弹窗列表选中的项：深天蓝高亮（非绘制模式下），一眼对应图像上的框
			var managed = !_roiDrawMode && ownMode && _managerHighlightIndex == i;
			Polygon polygon = new Polygon
			{
				Stroke = (flag ? Brushes.DodgerBlue : (managed ? Brushes.DeepSkyBlue : (ownMode ? Brushes.Orange : Brushes.MediumSeaGreen))),
				StrokeThickness = (flag ? 2.0 : (managed ? 2.5 : 1.0)),
				Fill = (flag
					? new SolidColorBrush(Color.FromArgb(40, 30, 144, byte.MaxValue))
					: managed ? new SolidColorBrush(Color.FromArgb(36, 30, 144, byte.MaxValue)) : Brushes.Transparent),
				IsHitTestVisible = false,
				ToolTip = "检测项「" + entry.Name + "」 第" + (i + 1) + "项" + (ownMode ? "（本节点私有）" : "")
			};
			RoiCanvas.Children.Add(polygon);
			_roiPolys.Add((entry, polygon, ownMode, i));
			if (flag)
			{
				_roiActivePoly = polygon;
			}
		}
		if (_roiSelIndex >= 0 && _roiActivePoly != null)
		{
			for (int num = 0; num < 8; num++)
			{
				Rectangle rectangle = new Rectangle
				{
					Width = 7.0,
					Height = 7.0,
					Fill = Brushes.White,
					Stroke = Brushes.DodgerBlue,
					StrokeThickness = 1.0,
					IsHitTestVisible = false
				};
				_roiHandleRects.Add(rectangle);
				RoiCanvas.Children.Add(rectangle);
			}
			_roiRotateHandle = new Ellipse
			{
				Width = 11.0,
				Height = 11.0,
				Fill = Brushes.White,
				Stroke = Brushes.Orange,
				StrokeThickness = 2.0,
				IsHitTestVisible = false,
				ToolTip = "拖拽旋转"
			};
			RoiCanvas.Children.Add(_roiRotateHandle);
		}
		RenderNodeAnnotations();
		LayoutRoiOverlay();
	}

	/// <summary>画单个检测项「修正后」的预览框（青色虚线，屏幕矢量层，命中测试坐标另行存 _previewHits）。</summary>
	private void AddPreviewPolygon(RoiRect rect, int imgW, int imgH, string name, int index)
	{
		var (pcx, pcy, pw, ph, pangle) = rect.ToPixels(imgW, imgH);
		var rad = pangle * Math.PI / 180.0;
		var pc = Math.Cos(rad);
		var ps = Math.Sin(rad);
		var corners = new (double X, double Y)[]
		{
			(-pw / 2.0, -ph / 2.0), (pw / 2.0, -ph / 2.0), (pw / 2.0, ph / 2.0), (-pw / 2.0, ph / 2.0),
		};
		var points = new PointCollection(corners.Select(o =>
		{
			var dp = ImageToDisplayPoint(new Point(pcx + o.X * pc - o.Y * ps, pcy + o.X * ps + o.Y * pc), imgW, imgH);
			return new System.Windows.Point(dp.X, dp.Y);
		}));
		RoiCanvas.Children.Add(new Polygon
		{
			Points = points,
			Stroke = Brushes.Cyan,
			StrokeThickness = 1.4,
			StrokeDashArray = new DoubleCollection { 4.0, 3.0 },
			Fill = Brushes.Transparent,
			IsHitTestVisible = false,
			ToolTip = "检测项「" + name + "」修正后的位置（第" + (index + 1) + "项）；选中后拖橙框调整",
		});
	}

	/// <summary>
	/// 节点矢量标注渲染（当前选中节点）：线宽/字号为屏幕常量，与图像分辨率、显示缩放无关。
	/// 形状坐标为源图像素，经 UniformFit 映射到画布。
	/// </summary>
	private void RenderNodeAnnotations()
	{
		_annotationLabelBounds.Clear();
		if (string.IsNullOrWhiteSpace(_selectedThumbName)
			|| !_nodeAnnotations.TryGetValue(_selectedThumbName, out var shapes)
			|| shapes.Count == 0)
		{
			return;
		}
		var displayed = DisplayedImageSize;
		if (!displayed.HasValue)
		{
			return;
		}
		var (scale, ox, oy) = RoiGeometry.UniformFit(displayed.Value.Item1, displayed.Value.Item2, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		if (scale <= 0.0)
		{
			return;
		}
		foreach (var shape in shapes)
		{
			Brush brush = shape.Kind switch
			{
				NodeShapeKind.Defect => Brushes.Red,
				NodeShapeKind.Ok => Brushes.LimeGreen,
				_ => Brushes.Gold,
			};
			if (shape.Box is { } box && box.Width > 0 && box.Height > 0)
			{
				var left = ox + box.X * scale;
				var top = oy + box.Y * scale;
				Rectangle rect = new Rectangle
				{
					Width = Math.Max(1.0, box.Width * scale),
					Height = Math.Max(1.0, box.Height * scale),
					Stroke = brush,
					StrokeThickness = 1.0,
					IsHitTestVisible = false
				};
				Canvas.SetLeft(rect, left);
				Canvas.SetTop(rect, top);
				RoiCanvas.Children.Add(rect);
				if (!string.IsNullOrEmpty(shape.Label))
				{
					AddAnnotationLabel(shape.Label, brush, left, top);
				}
				continue;
			}
			if (shape.Polys == null)
			{
				continue;
			}
			if (shape.AsPoints)
			{
				// 点集渲染：屏幕常量小实心点（与图像分辨率、显示缩放无关）
				foreach (var point in shape.Polys.SelectMany((OpenCvSharp.Point[] poly) => poly))
				{
					Rectangle dot = new Rectangle
					{
						Width = 3.0,
						Height = 3.0,
						Fill = brush,
						IsHitTestVisible = false
					};
					Canvas.SetLeft(dot, ox + point.X * scale - 1.5);
					Canvas.SetTop(dot, oy + point.Y * scale - 1.5);
					RoiCanvas.Children.Add(dot);
				}
				if (!string.IsNullOrEmpty(shape.Label))
				{
					OpenCvSharp.Point topmost = shape.Polys.SelectMany((OpenCvSharp.Point[] poly) => poly)
						.OrderBy((OpenCvSharp.Point p) => p.Y).First();
					AddAnnotationLabel(shape.Label, brush, ox + topmost.X * scale, oy + topmost.Y * scale);
				}
				continue;
			}
			OpenCvSharp.Point labelPos = default;
			var hasLabelPos = false;
			foreach (var poly in shape.Polys)
			{
				if (poly.Length == 0)
				{
					continue;
				}
				Polygon polygon = new Polygon
				{
					Points = new PointCollection(poly.Select((OpenCvSharp.Point p) => new Point(ox + p.X * scale, oy + p.Y * scale))),
					Stroke = brush,
					StrokeThickness = 1.0,
					Fill = Brushes.Transparent,
					IsHitTestVisible = false
				};
				RoiCanvas.Children.Add(polygon);
				// 标签只在形状级加一次：取全部折线的最高点（逐条加会把一个标注拆成多个散落标签）
				if (!string.IsNullOrEmpty(shape.Label))
				{
					OpenCvSharp.Point topmost = poly.OrderBy((OpenCvSharp.Point p) => p.Y).First();
					if (!hasLabelPos || topmost.Y < labelPos.Y)
					{
						labelPos = topmost;
						hasLabelPos = true;
					}
				}
			}
			if (hasLabelPos)
			{
				AddAnnotationLabel(shape.Label, brush, ox + labelPos.X * scale, oy + labelPos.Y * scale);
			}
		}
	}

	private void AddAnnotationLabel(string? text, Brush brush, double x, double y)
	{
		if (string.IsNullOrEmpty(text)) return;
		// Labels are anchored to their shape, but move vertically when a nearby label already occupies the slot.
		var width = Math.Min(Math.Max(24.0, text.Length * 11.0 + 4.0), Math.Max(24.0, RoiCanvas.ActualWidth));
		var height = 17.0;
		var left = Math.Clamp(x, 0.0, Math.Max(0.0, RoiCanvas.ActualWidth - width));
		var top = Math.Clamp(y - 15.0, 0.0, Math.Max(0.0, RoiCanvas.ActualHeight - height));
		var candidate = new System.Windows.Rect(left, top, width, height);
		for (var attempt = 0; attempt < 32 && _annotationLabelBounds.Any(r => r.IntersectsWith(candidate)); attempt++)
		{
			top += height + 2.0;
			if (top + height > RoiCanvas.ActualHeight)
			{
				top = Math.Max(0.0, y - 15.0) - (attempt + 1) * (height + 2.0);
				top = Math.Clamp(top, 0.0, Math.Max(0.0, RoiCanvas.ActualHeight - height));
			}
			candidate = new System.Windows.Rect(left, top, width, height);
		}
		_annotationLabelBounds.Add(candidate);
		TextBlock label = new TextBlock
		{
			Text = text,
			FontSize = 11.0,
			Foreground = brush,
			Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
			Padding = new Thickness(1.0, 0.0, 1.0, 0.0),
			IsHitTestVisible = false
		};
		Canvas.SetLeft(label, candidate.Left);
		Canvas.SetTop(label, candidate.Top);
		RoiCanvas.Children.Add(label);
	}

	private void LayoutRoiOverlay(RoiRect? dragOverride = null)
	{
		var displayedImageSize = DisplayedImageSize;
		if (!displayedImageSize.HasValue || _roiPolys.Count == 0)
		{
			return;
		}
		var (num, ox, oy) = RoiGeometry.UniformFit(displayedImageSize.Value.Item1, displayedImageSize.Value.Item2, RoiCanvas.ActualWidth, RoiCanvas.ActualHeight);
		if (num <= 0.0)
		{
			return;
		}
		foreach (var roiPoly in _roiPolys)
		{
			var isSel = _roiSelIndex >= 0 && roiPoly.OwnIndex == _roiSelIndex;
			RoiRect roiRect = ((isSel && dragOverride.HasValue) ? dragOverride.Value : roiPoly.Roi.ToRoiRect());			var px = roiRect.ToPixels(displayedImageSize.Value.Item1, displayedImageSize.Value.Item2);
			SetPolyGeometry(roiPoly.Poly, px, num, ox, oy);
			if (isSel && _roiHandleRects.Count == 8 && _roiRotateHandle != null)
			{
				LayoutHandles(px, num, ox, oy);
			}
		}
	}

	private void SetPolyGeometry(Polygon poly, (double Cx, double Cy, int W, int H, double Angle) px, double scale, double ox, double oy)
	{
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		double num = px.Angle * Math.PI / 180.0;
		double cos = Math.Cos(num);
		double sin = Math.Sin(num);
		double num2 = (double)px.W * scale;
		double num3 = (double)px.H * scale;
		poly.Points = new PointCollection
		{
			Local((0.0 - num2) / 2.0, (0.0 - num3) / 2.0),
			Local(num2 / 2.0, (0.0 - num3) / 2.0),
			Local(num2 / 2.0, num3 / 2.0),
			Local((0.0 - num2) / 2.0, num3 / 2.0)
		};
		Point Local(double lx, double ly)
		{
			//IL_0056: Unknown result type (might be due to invalid IL or missing references)
			return new Point(ox + px.Cx * scale + cos * lx - sin * ly, oy + px.Cy * scale + sin * lx + cos * ly);
		}
	}

	private void LayoutHandles((double Cx, double Cy, int W, int H, double Angle) px, double scale, double ox, double oy)
	{
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0253: Unknown result type (might be due to invalid IL or missing references)
		double num = px.Angle * Math.PI / 180.0;
		double cos = Math.Cos(num);
		double sin = Math.Sin(num);
		double num2 = (double)px.W * scale;
		double num3 = (double)px.H * scale;
		(double, double)[] array = new(double, double)[8]
		{
			((0.0 - num2) / 2.0, (0.0 - num3) / 2.0),
			(0.0, (0.0 - num3) / 2.0),
			(num2 / 2.0, (0.0 - num3) / 2.0),
			(num2 / 2.0, 0.0),
			(num2 / 2.0, num3 / 2.0),
			(0.0, num3 / 2.0),
			((0.0 - num2) / 2.0, num3 / 2.0),
			((0.0 - num2) / 2.0, 0.0)
		};
		for (int i = 0; i < _roiHandleRects.Count; i++)
		{
			Point val = Local(array[i].Item1, array[i].Item2);
			Canvas.SetLeft(_roiHandleRects[i], val.X - 3.5);
			Canvas.SetTop(_roiHandleRects[i], val.Y - 3.5);
		}
		Point val2 = Local(0.0, 0.0 - (num3 / 2.0 + 24.0));
		Canvas.SetLeft(_roiRotateHandle, val2.X - 5.5);
		Canvas.SetTop(_roiRotateHandle, val2.Y - 5.5);
		Point Local(double lx, double ly)
		{
			//IL_0056: Unknown result type (might be due to invalid IL or missing references)
			return new Point(ox + px.Cx * scale + cos * lx - sin * ly, oy + px.Cy * scale + sin * lx + cos * ly);
		}
	}

	/// <summary>节点检测项（own_rois）：新建默认居中项（自动递增命名，预填节点类型默认阈值）。</summary>
	internal void AddOwnRoi(RecipeNode rn)
	{
		if (!EnsureUnlocked("新建检测项")) return;
		var items = NodeRois.ParseOwnFull(rn.Params.GetValueOrDefault("own_rois"));
		var name = NodeRois.UniqueOwnName(items.Select(i => (i.Name, i.Rect)).ToList(), "检测项");
		items.Add(new RoiItem(name, new RoiRect(0.5, 0.5, 0.3, 0.3, 0), new RoiMeta(Threshold: DefaultThresholdFor(rn))));
		WriteOwnRoisFull(rn, items);
		AppendLog($"[ROI] 节点 {rn.Name}: 新建检测项「{name}」（默认居中，阈值 {DefaultThresholdFor(rn)}；主图像叠加层可拖拽/缩放/旋转）");
		_roiDialog?.NotifyRoiChanged();
	}

	/// <summary>节点检测项重命名（按索引定位，允许与其他检测项重名）。</summary>
	internal void RenameOwnRoi(RecipeNode rn, int index, string newName)
	{
		if (!EnsureUnlocked("重命名检测项")) return;
		newName = newName.Trim();
		var items = NodeRois.ParseOwnFull(rn.Params.GetValueOrDefault("own_rois"));
		if (index < 0 || index >= items.Count) return;
		var oldName = items[index].Name;
		if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
		items[index] = items[index] with { Name = newName };
		WriteOwnRoisFull(rn, items);
		AppendLog("[ROI] 节点 " + rn.Name + ": 检测项「" + oldName + "」→「" + newName + "」");
		_roiDialog?.NotifyRoiChanged();
	}

	/// <summary>节点检测项删除（按索引定位 own_rois 条目，热生效）；总范围索引随删除平移。</summary>
	internal void DeleteOwnRoi(RecipeNode rn, int index)
	{
		if (!EnsureUnlocked("删除检测项")) return;
		var items = NodeRois.ParseOwnFull(rn.Params.GetValueOrDefault("own_rois"));
		if (index < 0 || index >= items.Count) return;
		var name = items[index].Name;
		items.RemoveAt(index);
		WriteOwnRoisFull(rn, items);
		var scope = NodeRois.ParseScopeIndex(rn.Params);
		if (scope == index)
		{
			rn.Params["scope_index"] = "-1";
			HotApplyParam(rn, "scope_index", "-1");
		}
		else if (scope > index)
		{
			rn.Params["scope_index"] = (scope - 1).ToString();
			HotApplyParam(rn, "scope_index", rn.Params["scope_index"]);
		}
		AppendLog("[ROI] 节点 " + rn.Name + ": 已删除检测项「" + name + "」");
		_roiSelIndex = -1;
		_roiDialog?.NotifyRoiChanged();
	}

	/// <summary>当前总范围 ROI 索引（-1=未设置；检测项管理弹窗显示/切换用）。</summary>
	internal int GetScopeIndex(RecipeNode rn) => NodeRois.ParseScopeIndex(rn.Params);

	/// <summary>
	/// 设置/取消总范围 ROI：该 ROI 作为"总范围"，其余检测项的结果只保留落在范围内的部分
	/// （YOLO=检出中心点在范围内；实例分割=实例中心在范围内且缺陷掩码与范围求交）。index=-1 取消。
	/// </summary>
	internal void SetScopeIndex(RecipeNode rn, int index)
	{
		if (!EnsureUnlocked("设置总范围")) return;
		rn.Params["scope_index"] = index.ToString();
		HotApplyParam(rn, "scope_index", rn.Params["scope_index"]);
		var own = NodeRois.ParseOwn(rn.Params.GetValueOrDefault("own_rois"));
		if (index >= 0 && index < own.Count)
		{
			AppendLog($"[ROI] 节点 {rn.Name}: 检测项「{own[index].Name}」已设为总范围（其余检测项结果只保留落在范围内的部分）");
		}
		else
		{
			AppendLog($"[ROI] 节点 {rn.Name}: 已取消总范围（全部检测项独立检测）");
		}
	}

	/// <summary>打开检测项管理弹窗（节点私有 ROI；方案级 ROI 库已移除）。</summary>
	internal void OpenRoiManager(RecipeNode rn, NodeParamDialog? dialog)
	{
		// 叠加层切到 host 节点的结果图（有缩略图时），保证弹窗列表与图像上的框对应同一节点
		if (_thumbImages.ContainsKey(rn.Name))
		{
			ShowNodeImage(rn.Name);
		}
		var roiManagerDialog = new RoiManagerDialog(this, rn, dialog);
		roiManagerDialog.Owner = this;
		_roiManager = roiManagerDialog;
		roiManagerDialog.Closed += (_, _) =>
		{
			if (ReferenceEquals(_roiManager, roiManagerDialog))
			{
				_roiManager = null;
				_managerHighlightIndex = -1;
				_redrawTargetIndex = -1;
				RenderRoiOverlay();
			}
		};
		roiManagerDialog.Show();
		roiManagerDialog.Activate();
	}

	/// <summary>重绘选中检测项：进入绘制模式，下一次画的框替换该检测项的几何（名字保留），画完自动退出。</summary>
	internal void StartRoiRedraw(RecipeNode rn, int index, NodeParamDialog? dialog)
	{
		if (!EnsureUnlocked("重绘检测项")) return;
		if (_thumbImages.ContainsKey(rn.Name))
		{
			ShowNodeImage(rn.Name);
		}
		if (_roiDrawMode && !ReferenceEquals(_roiDrawNode, rn))
		{
			ExitRoiDrawMode(); // 从别的节点的绘制模式切过来
		}
		_redrawTargetIndex = index;
		if (!_roiDrawMode)
		{
			ToggleRoiDraw(rn, dialog);
		}
		if (!_roiDrawMode)
		{
			_redrawTargetIndex = -1; // 进入绘制模式失败（节点没执行过等）
			return;
		}
		var own = NodeRois.ParseOwn(rn.Params.GetValueOrDefault("own_rois"));
		var name = index >= 0 && index < own.Count ? own[index].Name : "?";
		AppendLog("[ROI] 重绘模式：在主图像区画一个新框，替换检测项「" + name + "」的位置（画完自动退出绘制模式）");
	}

	/// <summary>
	/// 检测项管理弹窗列表选中变化 → 图像叠加层高亮对应框。
	/// 同时同步编辑选中索引 _roiSelIndex：绘制模式下激活框（蓝框+手柄）跟随列表选中，
	/// 避免"列表选第 3 项、图像激活的还是第 1 项"的错位。
	/// </summary>
	internal void SetManagerHighlight(RecipeNode host, int index)
	{
		var displayed = FindDisplayedModelNode();
		var matched = displayed != null && ReferenceEquals(displayed, host);
		_managerHighlightIndex = matched ? index : -1;
		if (matched)
		{
			_roiSelIndex = index; // 编辑焦点同步（手柄/拖拽目标跟随列表选中）
		}
		RenderRoiOverlay();
	}

	/// <summary>节点 ROI 变化后的 UI 刷新（叠加层 + 状态页）。</summary>
	internal void OnRoiLibraryChanged()
	{
		RenderRoiOverlay();
		RefreshStatusPanel();
	}
}
