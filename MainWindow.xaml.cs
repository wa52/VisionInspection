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
using SpeakerVisionInspection.Camera;
using SpeakerVisionInspection.Comm;
using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using SpeakerVisionInspection.Production;
using SpeakerVisionInspection.Services;
using WpfWindow = System.Windows.Window;
using ModelsCondition = SpeakerVisionInspection.Models.Condition;

namespace SpeakerVisionInspection;

public partial class MainWindow : WpfWindow
{
	private static readonly string ConfigDir = AppContext.BaseDirectory;

	private Recipe? _recipe;

	private Pipeline? _pipeline;

	private bool _pipelineDirty;

	private PipelineRunner? _runner;

	private CameraFrameGrabber? _frameGrabber;

	private ICameraController? _camera;

	private CameraParameterStore? _store;

	private CameraInfo? _selectedCamera;

	/// <summary>「按键控制」节点绑定的触发键位（空闲时按下 = 执行一次完整检测流程）。</summary>
	private readonly HashSet<Key> _keyTriggerKeys = new();

	private Dictionary<string, TextBox> _paramBoxes;

	private PipelineResult? _latestResult;

	private DateTime _lastRoiCheckAt;

	private bool? _lastRoiMismatch;

	private StackPanel? _editorPanel;

	private readonly DispatcherTimer _renderTimer;

	private PipelineResult? _pendingResult;

	private readonly object _pendingGate;

	private readonly Dictionary<string, BitmapSource> _thumbImages;

	/// <summary>最近一次执行的节点结果值（位置修正「设置基准位姿」读取 loc_* 用）。</summary>
	private IReadOnlyDictionary<string, Dictionary<string, string>>? _lastNodeValues;

	/// <summary>模块结果历史（海康式：每节点最近 N 次执行记录，供模块弹窗「模块结果/当前结果/历史结果」展示）。</summary>
	private readonly ModuleResultHistory _moduleHistory = new();

	private bool _thumbStripVisible;

	private string? _selectedThumbName;

	/// <summary>节点名 → 矢量标注形状（最近一次运行结果；随选中节点渲染到叠加层）。</summary>
	private Dictionary<string, IReadOnlyList<NodeShape>> _nodeAnnotations = new Dictionary<string, IReadOnlyList<NodeShape>>();

	private bool _roiDrawMode;

	private RecipeNode? _roiDrawNode;

	private NodeParamDialog? _roiDialog;

	private NodeParamDialog? _paramDialog;

	private Point? _roiDragStart;

	private Rectangle? _roiPreviewRect;

	private RoiHandle _roiDragHandle;

	private RoiRect _roiDragOrig;

	private (double X, double Y) _roiDragOriginImage;

	private Polygon? _roiActivePoly;

	private int _roiSelIndex = -1;

	/// <summary>检测项管理弹窗列表选中的 own_rois 索引（叠加层高亮用，-1=无）。</summary>
	private int _managerHighlightIndex = -1;

	/// <summary>打开中的检测项管理弹窗（图像点选反向同步列表选中）。</summary>
	private RoiManagerDialog? _roiManager;

	/// <summary>方案编辑历史（撤销/重做）：Recipe JSON 快照栈；_lastSnapshot=距上次快照点之前的状态（惰性捕获，变更入口调用 PushUndoSnapshot 时仍是变更前状态）。</summary>
	private readonly Stack<string> _undoStack = new();
	private readonly Stack<string> _redoStack = new();
	private string _lastSnapshot = "";
	private bool _restoringSnapshot;
	private const int MaxUndoSteps = 50;

	/// <summary>方案锁定（工具栏挂锁）：开启时 ROI 几何与节点结构不可修改（防误触），参数数值仍可调整。运行时状态，不持久化。</summary>
	private bool _uiLocked;

	/// <summary>连续执行图标几何（空闲=循环箭头 / 运行中=停止方块，UpdateButtonState 切换）。</summary>
	private const string IconContinuousData = "M21,4 L21,10 L15,10 M19.6,15 A8,8 0 1 1 17.7,6.6 L21,10";
	private const string IconStopData = "M7,7 L17,7 L17,17 L7,17 Z";

	/// <summary>重绘目标检测项索引（>=0 时下一次画框替换该条目几何、名字保留；画完自动清零）。</summary>
	private int _redrawTargetIndex = -1;

	private readonly List<(RecipeRoi Roi, Polygon Poly, bool Own, int OwnIndex)> _roiPolys;

	/// <summary>位置修正预览激活时各检测项修正后的几何（与 own_rois 同索引；命中测试用）。</summary>
	private readonly List<RoiRect> _previewHits = new();

	private readonly List<Rectangle> _roiHandleRects;

	private Ellipse? _roiRotateHandle;

	private const double RoiHandleTolerancePx = 7.0;

	private const double RoiRotateOffsetPx = 24.0;

	private static readonly Dictionary<string, string> NodeTypeLabels = new Dictionary<string, string>
	{
		["PatchCore"] = "PatchCore 检测",
		["Decision"] = "条件检测",
		["SaveImage"] = "输出图像",
		["ImageSource"] = "图像源",
		["Binarize"] = "图像二值化",
		["Geometry"] = "几何变换",
		["ColorTransform"] = "颜色变换",
		["CameraIo"] = "相机IO通信",
		["YOLO"] = "目标检测",
		["Seg"] = "实例分割",
		["SemanticSeg"] = "语义分割",
		["ContourMatch"] = "轮廓匹配",
		["PositionCorrection"] = "位置修正",
		["KeyControl"] = "按键控制",
		["OCR"] = "OCR 识别（未实现）"
	};

	private int _logLines;

	private StackPanel EditorPanel => _editorPanel ?? InspectorPanel;

	private (int W, int H)? DisplayedImageSize =>
		(ResultImage.Source is BitmapSource bmp && bmp.PixelWidth > 0) ? (bmp.PixelWidth, bmp.PixelHeight) : null;

	public MainWindow()
	{
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d3: Expected O, but got Unknown
		_pipelineDirty = true;
		_paramBoxes = new Dictionary<string, TextBox>();
		_lastRoiCheckAt = DateTime.MinValue;
		_pendingGate = new object();
		_thumbImages = new Dictionary<string, BitmapSource>();
		_roiDragHandle = RoiHandle.None;
		_roiPolys = new List<(RecipeRoi Roi, Polygon Poly, bool Own, int OwnIndex)>();
		_roiHandleRects = new List<Rectangle>();
		InitializeComponent();
		CheckCameraRuntime();
		_runner = new PipelineRunner();
		_runner.StateChanged += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				UpdateStatePanel();
				UpdateButtonState();
			}, Array.Empty<object>());
		};
		_runner.Completed += OnRunnerCompleted;
		_runner.Failed += delegate(string msg)
		{
			AppendLog("[执行] " + msg);
		};
		_renderTimer = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromMilliseconds(100.0)
		};
		_renderTimer.Tick += delegate
		{
			RenderPendingResult();
		};
		_renderTimer.Start();
		RoiCanvas.SizeChanged += delegate
		{
			RenderRoiOverlay();
		};
		LoadRecipe();
		UpdateStatePanel();
		UpdateButtonState();
	}

	private void CheckCameraRuntime()
	{
		var config = VisionMasterConfigResolver.ResolveFromProcessEnvironment(System.IO.Path.Combine(ConfigDir, "appsettings.json"));
		var result = EnvironmentCheckService.Check(config, Environment.Is64BitProcess, ConfigDir);
		var failed = result.Items.Where(i => !i.Ok).ToList();
		StatusSdk.Text = failed.Count == 0 ? "MVS: 就绪（无需 VisionMaster 加密狗）" : "MVS: 相机运行时缺失";
		if (failed.Count > 0)
		{
			AppendLog("[环境] 相机功能不可用：" + string.Join("；", failed.Select(i => $"{i.Name}: {i.Detail}")));
		}
		else
		{
			AppendLog("[环境] MVS 相机运行时检查通过（独立模式，无需 VisionMaster 加密狗）");
		}
	}

	private void OnRunnerCompleted(PipelineResult result)
	{
		PipelineResult pendingResult;
		lock (_pendingGate)
		{
			pendingResult = _pendingResult;
			_pendingResult = result;
		}
		if (pendingResult == null)
		{
			return;
		}
		// 跳过未渲染的上一轮结果：释放其节点图。Mat 可能与渲染侧共享，逐个容错，
		// 防止后台线程释放时与 UI 渲染竞态导致 ObjectDisposedException 崩掉回调。
		foreach (Mat value in pendingResult.NodeImages.Values)
		{
			try
			{
				if (!value.IsDisposed)
				{
					value.Dispose();
				}
			}
			catch (ObjectDisposedException)
			{
			}
		}
		pendingResult.NodeImages.Clear();
	}

	private void RenderPendingResult()
	{
		PipelineResult pendingResult;
		lock (_pendingGate)
		{
			pendingResult = _pendingResult;
			_pendingResult = null;
		}
		if (pendingResult != null)
		{
			try
			{
				ShowExecutionResult(pendingResult);
			}
			catch (ObjectDisposedException ex)
			{
				// 渲染链路中访问到已被释放的 Mat：丢弃本轮剩余渲染，记日志，不允许异常打断渲染定时器
				AppendLog("[显示] 渲染结果时图像已被释放，本轮已跳过: " + ex.Message);
			}
		}
	}

	private void LoadRecipe()
	{
		try
		{
			_recipe = RecipeStore.Load(ConfigDir);
		}
		catch (Exception ex)
		{
			_recipe = null;
			AppendLog("[警告] 方案加载失败: " + ex.Message);
		}
		if (_recipe == null)
		{
			_recipe = Recipe.CreateDefault();
		}
		_recipe.BaseDir = ConfigDir;
		MigrateLegacyRois(_recipe);
		RecipeCombo.Text = _recipe.Name;
		RefreshNodeTree();
		AppendLog("方案已加载: " + _recipe.Name);
		ResetEditHistory();
		string environmentVariable = Environment.GetEnvironmentVariable("DEPLOY_MODEL_DIR");
		if (!string.IsNullOrWhiteSpace(environmentVariable))
		{
			AppendLog("[警告] 检测到 DEPLOY_MODEL_DIR 环境变量，模型路径将被覆盖为: " + environmentVariable + "（保存的模型路径不生效）");
		}
	}

	/// <summary>旧单 ROI（roi 数值参数）→ 本节点私有 ROI（own_rois，一次性迁移，保留检测语义）。</summary>
	private static void MigrateLegacyRois(Recipe recipe)
	{
		foreach (var n in recipe.Nodes.Where((RecipeNode n) => n.Type == "PatchCore"))
		{
			var hasOwn = !string.IsNullOrWhiteSpace(n.Params.GetValueOrDefault("own_rois"));
			var legacy = n.Params.GetValueOrDefault("roi");
			if (hasOwn || string.IsNullOrWhiteSpace(legacy)) continue;
			var rect = RoiRect.Parse(legacy);
			if (!rect.HasValue) continue;
			n.Params["own_rois"] = NodeRois.SerializeOwn(new List<(string, RoiRect)> { ("默认ROI", rect.Value) });
			n.Params["roi"] = "";
		}
	}

	private ICameraController CreateCamera()
	{
		VisionMasterConfig visionMasterConfig = VisionMasterConfigResolver.ResolveFromProcessEnvironment(System.IO.Path.Combine(ConfigDir, "appsettings.json"));
		return new MvCameraControlCameraController(visionMasterConfig.MvsRuntimeDir);
	}

	private void Camera_FrameReceived(object? sender, CameraFrameEventArgs e)	{
		PipelineRunner runner = _runner;
		if (runner != null && runner.IsRunning)
		{
			return;
		}
		CameraFrame frame = e.Frame;
		byte[] array = new byte[frame.DataLength];
		Marshal.Copy(frame.Data, array, 0, frame.DataLength);
		FrameSnapshot snapshot = new FrameSnapshot(array, frame.Width, frame.Height, frame.PixelFormat);
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			using Mat mat = CameraFrameMatConverter.ToBgrMat(snapshot.Data, snapshot.Width, snapshot.Height, snapshot.PixelFormat);
			ResultImage.Source = MatToBitmap(mat);
			ResultCaption.Text = "相机预览";
			RenderRoiOverlay();
		}, Array.Empty<object>());
	}

	private void BtnRunOnce_Click(object sender, RoutedEventArgs e)
	{
		RunOnceAsync();
	}

	private async Task RunOnceAsync()
	{
		if (_recipe == null || !(await TryPrepareExecutionAsync()))
		{
			return;
		}
		try
		{
			await _runner.RunOnceAsync(_pipeline, $"EXEC_{DateTime.Now:yyyyMMdd_HHmmss_fff}", CameraIoOutputOrNull());
		}
		catch (Exception ex)
		{
			AppendLog("单次执行失败: " + ex.Message);
		}
	}

	private async void BtnRunContinuous_Click(object sender, RoutedEventArgs e)
	{
		PipelineRunner runner = _runner;
		if (runner != null && runner.IsRunning && runner.IsContinuous)
		{
			_runner.StopAsync();
		}
		else
		{
			if (_recipe == null || !(await TryPrepareExecutionAsync()))
			{
				return;
			}
			try
			{
				_runner.StartContinuous(_pipeline, () => $"EXEC_{DateTime.Now:yyyyMMdd_HHmmss_fff}", CameraIoOutputOrNull());
				AppendLog("连续执行已开始，点「停止执行」结束");
			}
			catch (Exception ex)
			{
				AppendLog("连续执行启动失败: " + ex.Message);
			}
		}
	}

	private async Task<bool> TryPrepareExecutionAsync()
	{
		if (_runner?.IsRunning ?? false)
		{
			AppendLog("[执行] 已有执行在进行中，请先停止");
			return false;
		}
		RecipeNode imageSourceNode = _recipe.Nodes.FirstOrDefault(delegate(RecipeNode recipeNode)
		{
			bool enabled = recipeNode.Enabled;
			bool flag = enabled;
			if (flag)
			{
				string type = recipeNode.Type;
				bool flag2 = ((type == "ImageSource" || type == "ImageLoad") ? true : false);
				flag = flag2;
			}
			return flag;
		});
		if (imageSourceNode == null)
		{
			AppendLog("[执行] 方案中没有启用的图像源节点，请先添加「图像源」作为流程起点");
			return false;
		}
		if (_pipeline == null || _pipelineDirty)
		{
			Recipe recipe = _recipe;
			_pipeline?.Dispose();
			_pipeline = null;
			Pipeline built;
			try
			{
				built = await Task.Run(() => new Pipeline(recipe, delegate(string msg)
				{
					AppendLog(msg);
				}));
			}
			catch (Exception ex)
			{
				_pipelineDirty = true;
				AppendLog("[执行] 流水线构建失败: " + ex.Message + "（请检查各节点参数，如 PatchCore 的「模型路径」）");
				return false;
			}
			_pipeline = built;
			_pipelineDirty = false;
			AppendLog($"[执行] 流水线已就绪: {_pipeline.Nodes.Count((IModelNode modelNode) => modelNode.Enabled)} 个启用节点");
		}
		foreach (IModelNode n in _pipeline.Nodes)
		{
			if (n is ImageSourceNode src)
			{
				src.FrameProvider = (int ms) => GetFrameGrabber()?.Grab(ms, AppendLog);
			}
		}
		foreach (IModelNode n2 in _pipeline.Nodes.Where((IModelNode modelNode) => modelNode.Enabled))
		{
			if (n2.ParamDefs.Any((ParamDef d) => d.Key == "source") && n2.Params.TryGetValue("source", out string srcVal) && srcVal == "@input")
			{
				AppendLog("[校验] " + n2.Name + ": 图像来源为 @input（执行时为空图），请在参数里选择上游节点");
			}
			srcVal = null;
		}
		return true;
	}

	/// <summary>相机 IO 输出执行器：相机已连接时返回真实执行器；未连接返回 null（CameraIo 节点将 ERROR 停线，防止静默漏输出）。</summary>
	private Action<IoCommunicationSettings>? CameraIoOutputOrNull()
	{
		ICameraController? camera = _camera;
		return (camera != null && camera.State == CameraConnectionState.Connected) ? CameraIoOutput : null;
	}

	private void CameraIoOutput(IoCommunicationSettings settings)
	{
		_camera?.PulseNgOutputAsync(settings).GetAwaiter().GetResult();
	}

	private CameraFrameGrabber? GetFrameGrabber()
	{
		if (_camera == null)
		{
			return null;
		}
		if (_frameGrabber == null)
		{
			_frameGrabber = new CameraFrameGrabber(_camera);
		}
		return _frameGrabber;
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
		FinalVerdictText.Text = result.Decision;
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
		// 同一 Mat 实例可能在多个节点键下共享（透传/复用）：按引用去重，转换一次、多次复用、最后只释放一次；
		// 单个 Mat 失败（已被释放等）只跳过该图，不中断整轮渲染
		var convertedMats = new System.Collections.Generic.Dictionary<Mat, BitmapSource?>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
		foreach (var (key, mat2) in result.NodeImages)
		{
			try
			{
				if (mat2.IsDisposed)
				{
					continue;
				}
				if (!convertedMats.TryGetValue(mat2, out var bitmapSource))
				{
					bitmapSource = MatToBitmap(mat2);
					convertedMats[mat2] = bitmapSource;
				}
				if (bitmapSource != null)
				{
					_thumbImages[key] = bitmapSource;
				}
			}
			catch (ObjectDisposedException)
			{
			}
		}
		foreach (var mat2 in convertedMats.Keys)
		{
			try
			{
				if (!mat2.IsDisposed)
				{
					mat2.Dispose();
				}
			}
			catch (ObjectDisposedException)
			{
			}
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

	private void BtnThumbToggle_Click(object sender, RoutedEventArgs e)
	{
		_thumbStripVisible = BtnThumbToggle.IsChecked == true;
		ThumbStripHost.Visibility = ((!_thumbStripVisible) ? Visibility.Collapsed : Visibility.Visible);
		if (_thumbStripVisible)
		{
			RefreshThumbStrip();
		}
	}

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
				&& n.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch");
		}
		if (string.IsNullOrWhiteSpace(_selectedThumbName))
		{
			return null;
		}
		return _recipe.Nodes.FirstOrDefault((RecipeNode n) =>
			string.Equals(n.Name, _selectedThumbName, StringComparison.OrdinalIgnoreCase)
			&& n.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch");
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

	/// <summary>节点类型对应的检测项默认阈值（节点级总阈值已移除，新建检测项预填；PatchCore 用模型训练阈值）。</summary>
	internal string DefaultThresholdFor(RecipeNode rn) => rn.Type switch
	{
		"YOLO" => YoloNode.DefaultConf,
		"Seg" => SegNode.DefaultPercent,
		"SemanticSeg" => SemanticSegNode.DefaultPercent,
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
		AppendLog($"[ROI] 节点 {rn.Name}: 检测项「{items[index].Name}」→ {(meta.Enabled ? "启用" : "停用")} / {(NodeRois.Judges(meta) ? "参与判定" : "仅观察")} / 阈值 {(meta.Threshold.Length > 0 ? meta.Threshold : "默认")} / {(meta.SaveImage ? "存图" : "不存图")}");
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
		_roiHandleRects.Clear();
		_roiActivePoly = null;
		_roiRotateHandle = null;
		_roiPreviewRect = null;
		if (_roiDragHandle != RoiHandle.None)
		{
			return;
		}
		var displayedImageSize = DisplayedImageSize;
		if (!displayedImageSize.HasValue || _recipe == null)
		{
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

	private void AddAnnotationLabel(string text, Brush brush, double x, double y)
	{
		TextBlock label = new TextBlock
		{
			Text = text,
			FontSize = 11.0,
			Foreground = brush,
			Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
			Padding = new Thickness(1.0, 0.0, 1.0, 0.0),
			IsHitTestVisible = false
		};
		Canvas.SetLeft(label, x);
		Canvas.SetTop(label, Math.Max(0.0, y - 15.0));
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

	/// <summary>当前方案（弹窗访问用；调用前方案已加载）。</summary>
	internal Recipe CurrentRecipe => _recipe!;

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
			ResultImage.Source = value;
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
		PipelineRunner? runner = _runner;
		string text;
		string text2;
		Brush fill;
		if (runner == null || !runner.IsRunning)
		{
			SolidColorBrush gray = Brushes.Gray;
			text = "未执行";
			text2 = "空闲";
			fill = gray;
		}
		else
		{
			SolidColorBrush orange = Brushes.Orange;
			string text3 = (_runner.IsContinuous ? "连续执行中（点「停止执行」结束）" : "单次执行中");
			text = text3;
			text2 = "执行中";
			fill = orange;
		}
		StateDot.Fill = fill;
		StateText.Text = text2;
		StateDetailText.Text = text;
		StatusTrigger.Text = "触发: -";
	}

	private void UpdateButtonState()
	{
		bool flag6 = _runner?.IsRunning ?? false;
		bool flag7 = flag6 && _runner.IsContinuous;
		bool isEnabled = _recipe != null && HasEnabledImageSource() && !flag6;
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

	private void RecipeNew_Click(object sender, RoutedEventArgs e)
	{
		if (ConfirmStop("新建方案将停止当前服务。继续？"))
		{
			_recipe = Recipe.CreateDefault();
			_recipe.BaseDir = ConfigDir;
			RecipeCombo.Text = _recipe.Name;
			RefreshNodeTree();
			ResetEditHistory();
			AppendLog("已新建默认方案");
		}
	}

	private void RecipeOpen_Click(object sender, RoutedEventArgs e)
	{
		if (!ConfirmStop("打开方案将停止当前服务。继续？"))
		{
			return;
		}
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = "打开方案",
			Filter = "方案文件 (*.json)|*.json|所有文件 (*.*)|*.*",
			InitialDirectory = ConfigDir
		};
		if (openFileDialog.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			_recipe = RecipeStore.LoadFile(openFileDialog.FileName);
			_recipe.BaseDir = System.IO.Path.GetDirectoryName(openFileDialog.FileName) ?? ConfigDir;
			RecipeCombo.Text = _recipe.Name;
			RefreshNodeTree();
			ResetEditHistory();
			AppendLog("已打开方案: " + openFileDialog.FileName);
			if (!HasEnabledImageSource())
			{
				AppendLog("[提示] 方案中没有启用的图像源节点，请添加并勾选「图像源」后才能执行");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void RecipeSave_Click(object sender, RoutedEventArgs e)
	{
		if (_recipe == null)
		{
			return;
		}
		_recipe.Name = RecipeCombo.Text.Trim();
		try
		{
			RecipeStore.SaveAsDefault(_recipe, ConfigDir);
			AppendLog("方案已保存: " + _recipe.Name);
		}
		catch (Exception ex)
		{
			MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void RecipeSaveAs_Click(object sender, RoutedEventArgs e)
	{
		if (_recipe == null)
		{
			return;
		}
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Title = "方案另存为",
			Filter = "方案文件 (*.json)|*.json",
			FileName = _recipe.Name + ".json",
			InitialDirectory = ConfigDir
		};
		if (saveFileDialog.ShowDialog(this) != true)
		{
			return;
		}
		_recipe.Name = RecipeCombo.Text.Trim();
		try
		{
			RecipeStore.Save(_recipe, saveFileDialog.FileName);
			AppendLog("方案已另存为: " + saveFileDialog.FileName);
		}
		catch (Exception ex)
		{
			MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	// ===== 方案锁定 =====

	/// <summary>锁定守卫：锁定中返回 false 并记日志（在变更写入前调用）。</summary>
	private bool EnsureUnlocked(string what)
	{
		if (!_uiLocked) return true;
		AppendLog($"[锁定] 方案已锁定，无法{what}（点击工具栏「锁定」按钮解锁）");
		return false;
	}

	private void BtnLock_Click(object sender, RoutedEventArgs e)
	{
		_uiLocked = BtnLock.IsChecked == true;
		if (_uiLocked)
		{
			if (_roiDrawMode) ExitRoiDrawMode();
			_roiSelIndex = -1;
			_managerHighlightIndex = -1;
			AppendLog("[锁定] 方案已锁定：ROI 与节点结构不可修改（防误触；参数数值仍可调整）");
		}
		else
		{
			AppendLog("[锁定] 已解锁方案编辑");
		}
		RenderRoiOverlay();
	}

	// ===== 方案编辑历史（撤销/重做）=====

	/// <summary>当前方案 JSON 快照。</summary>
	private string SnapshotRecipe() =>
		_recipe is null ? "" : System.Text.Json.JsonSerializer.Serialize(_recipe, Recipe.JsonOptions);

	/// <summary>重置历史（新建/打开方案后调用：基线快照，清空撤销/重做栈与模块结果历史）。</summary>
	private void ResetEditHistory()
	{
		_undoStack.Clear();
		_redoStack.Clear();
		_moduleHistory.Clear();
		_lastSnapshot = SnapshotRecipe();
		UpdateUndoRedoState();
		RefreshInspectorModuleResult();
	}

	/// <summary>
	/// 变更入口调用：把「上一个快照点」压入撤销栈（惰性捕获——_lastSnapshot 是本次变更前的状态，
	/// 即使 Params 已被写入也能取到变更前状态）；状态未变则跳过；任何新撤销点清空重做栈。
	/// </summary>
	private void PushUndoSnapshot()
	{
		if (_restoringSnapshot || _recipe is null) return;
		var current = SnapshotRecipe();
		if (current == _lastSnapshot) return;
		_undoStack.Push(_lastSnapshot);
		while (_undoStack.Count > MaxUndoSteps)
		{
			var keep = _undoStack.Take(MaxUndoSteps).ToArray(); // Stack 枚举顺序=栈顶→栈底
			_undoStack.Clear();
			for (var i = keep.Length - 1; i >= 0; i--) _undoStack.Push(keep[i]);
		}
		_redoStack.Clear();
		_lastSnapshot = current;
		UpdateUndoRedoState();
	}

	private void UndoRecipe()
	{
		if (_undoStack.Count == 0 || _recipe is null) return;
		var current = SnapshotRecipe();
		var snap = _undoStack.Pop();
		_redoStack.Push(current);
		RestoreRecipeSnapshot(snap, "撤销");
	}

	private void RedoRecipe()
	{
		if (_redoStack.Count == 0 || _recipe is null) return;
		var current = SnapshotRecipe();
		var snap = _redoStack.Pop();
		_undoStack.Push(current);
		RestoreRecipeSnapshot(snap, "恢复");
	}

	/// <summary>把快照灌回 UI：替换 _recipe、关掉绑定旧节点实例的弹窗、RefreshNodeTree 统一失效（标脏流水线/状态/叠加层）。</summary>
	private void RestoreRecipeSnapshot(string json, string verb)
	{
		try
		{
			var r = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json, Recipe.JsonOptions);
			if (r is null) return;
			_restoringSnapshot = true;
			try
			{
				r.BaseDir = _recipe.BaseDir;
				_recipe = r;
				if (_roiDrawMode) ExitRoiDrawMode();
				_roiSelIndex = -1;
				_managerHighlightIndex = -1;
				_redrawTargetIndex = -1;
				_roiManager?.Close(); // 弹窗绑定的是旧 RecipeNode 实例
				_roiDialog = null;
				RecipeCombo.Text = r.Name;
				RefreshNodeTree();
				AppendLog($"[编辑] {verb}：方案已恢复到上一步（可撤销 {_undoStack.Count} 步 / 可恢复 {_redoStack.Count} 步）");
			}
			finally
			{
				_restoringSnapshot = false;
			}
		}
		catch (Exception ex)
		{
			AppendLog($"[编辑] {verb}失败: {ex.Message}");
		}
		_lastSnapshot = SnapshotRecipe();
		UpdateUndoRedoState();
	}

	private void UpdateUndoRedoState()
	{
		BtnUndo.IsEnabled = _undoStack.Count > 0;
		BtnRedo.IsEnabled = _redoStack.Count > 0;
	}

	private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoRecipe();

	private void BtnRedo_Click(object sender, RoutedEventArgs e) => RedoRecipe();

	// ===== 工具栏入口：相机管理弹窗（设备级采集/触发）；IO 输出配置在「相机IO通信」节点弹窗 =====

	private async void BtnCameraManager_Click(object sender, RoutedEventArgs e) => await OpenCameraManagerAsync();

	/// <summary>工具栏入口：通信管理弹窗（设备级通信工具，TCP 客户端联机调试）。生产运行中拒绝操作。</summary>
	private void BtnCommManager_Click(object sender, RoutedEventArgs e)
	{
		if (_runner.IsRunning)
		{
			MessageBox.Show("生产检测运行中，通信设备暂不可配置", "通信管理", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}

		new CommManagementDialog(
			this,
			new CommDeviceStore(System.IO.Path.Combine(ConfigDir, "comm.json")),
			AppendLog).ShowDialog();
	}

	private async Task OpenCameraManagerAsync()
	{
		// 确保控制器存在；首次枚举一次作为弹窗初始列表（弹窗内可随时重新刷新）
		if (_camera == null)
		{
			_camera = CreateCamera();
			_camera.FrameReceived += Camera_FrameReceived;
		}

		// 先确保配置存储存在：弹窗内连接后要按已保存参数应用，应用参数后要落盘
		_store ??= new CameraParameterStore(System.IO.Path.Combine(ConfigDir, "camera.json"));
		IReadOnlyList<CameraInfo> cameras = [];
		try
		{
			AppendLog("正在枚举相机...");
			cameras = await _camera.EnumerateAsync();
			AppendLog($"枚举完成，发现 {cameras.Count} 台相机");
			StatusCamera.Text = ((cameras.Count == 0) ? "相机: 未发现相机" : $"相机: {cameras.Count} 台");
		}
		catch (Exception ex)
		{
			AppendLog("枚举相机失败: " + ex.Message + "（可在弹窗内点「刷新相机」重试）");
		}

		new CameraManagementDialog(
			this,
			_camera,
			cameras,
			_selectedCamera,
			_store,
			camera =>
			{
				_selectedCamera = camera;
				UpdateButtonState();
			},
			AppendLog,
			(parameters, trigger) =>
			{
				_store.Save(parameters);
				_store.SaveTriggerSettings(trigger);
			},
			() => !(_runner?.IsRunning ?? false)).Show();
	}

	// ===== 菜单/快捷键命令（复用工具栏同名处理）=====

	private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

	private void CmdRecipeNew_Executed(object sender, ExecutedRoutedEventArgs e) => RecipeNew_Click(sender, e);

	private void CmdRecipeOpen_Executed(object sender, ExecutedRoutedEventArgs e) => RecipeOpen_Click(sender, e);

	private void CmdRecipeSave_Executed(object sender, ExecutedRoutedEventArgs e) => RecipeSave_Click(sender, e);

	private void CmdUndo_Executed(object sender, ExecutedRoutedEventArgs e) => UndoRecipe();

	private void CmdRedo_Executed(object sender, ExecutedRoutedEventArgs e) => RedoRecipe();

	private void CmdUndo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
	{
		e.CanExecute = _undoStack.Count > 0;
		e.Handled = true;
	}

	private void CmdRedo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
	{
		e.CanExecute = _redoStack.Count > 0;
		e.Handled = true;
	}

	private bool ConfirmStop(string msg)
	{
		PipelineRunner runner = _runner;
		if (runner == null || !runner.IsRunning)
		{
			return true;
		}
		return MessageBox.Show(msg, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
	}

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
		_pipelineDirty = true;
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

	/// <summary>
	/// 扫描方案里启用的「按键控制」节点，重绑触发键位（集合不变则跳过）。
	/// 在节点树刷新/键位参数变更后调用。
	/// </summary>
	private void RefreshKeyBindings()
	{
		var keys = new HashSet<Key>();
		if (_recipe != null)
		{
			foreach (RecipeNode node in _recipe.Nodes)
			{
				if (!node.Enabled || node.Type != "KeyControl")
				{
					continue;
				}

				Key? parsed = KeyControlNode.ParseKey(node.Params.GetValueOrDefault("trigger_key"));
				if (parsed.HasValue)
				{
					keys.Add(parsed.Value);
				}
			}
		}

		if (keys.Count == _keyTriggerKeys.Count && keys.SetEquals(_keyTriggerKeys))
		{
			return;
		}

		_keyTriggerKeys.Clear();
		foreach (Key k in keys)
		{
			_keyTriggerKeys.Add(k);
		}
		AppendLog(keys.Count > 0
			? "[按键触发] 已绑定键位: " + string.Join("、", keys.Select(KeyControlNode.KeyName)) + "（空闲时按下 = 执行一次检测流程）"
			: "[按键触发] 当前无绑定键位");
	}

	private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
	{
		Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
		if (_keyTriggerKeys.Count == 0 || !_keyTriggerKeys.Contains(key))
		{
			return;
		}

		// 输入控件聚焦时不触发（参数输入框/下拉框里打字或按空格不能误跑流程）
		if (Keyboard.FocusedElement is TextBoxBase or ComboBox)
		{
			return;
		}

		e.Handled = true;
		if (_runner?.IsRunning ?? false)
		{
			AppendLog("[按键触发] 已有执行在进行中，忽略本次按键");
			return;
		}

		AppendLog("[按键触发] 按下「" + KeyControlNode.KeyName(key) + "」→ 执行一次检测流程");
		_ = RunOnceAsync();
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
		RecipeNode recipeNode = _recipe?.Nodes.FirstOrDefault((RecipeNode n) => n.Enabled && (n.Type == "PatchCore" || n.Type == "YOLO" || n.Type == "Seg" || n.Type == "SemanticSeg"));
		if (recipeNode == null)
		{
			return false;
		}
		if (!recipeNode.Params.TryGetValue("crop_dir", out string dir) || string.IsNullOrWhiteSpace(dir))
		{
			return false;
		}
		var names = (recipeNode.Params.TryGetValue("rois", out string roisRaw) ? roisRaw : "")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
			// 最新切图引用的 ROI 名不在当前节点引用列表中 → 说明参数变了
			bool flag = !names.Contains(value2.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
			if (flag && _lastRoiMismatch != true)
			{
				AppendLog("[校验] 切图目录 " + dir + " 最新切图引用的 ROI(" + value2.GetString() + ") 与当前节点引用(" + string.Join(",", names) + ") 不一致");
			}
			return flag;
		}
		catch
		{
			return false;
		}
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

	private void AddNode_Click(object sender, RoutedEventArgs e)
	{
		if (_recipe == null || !EnsureUnlocked("添加模块"))
		{
			return;
		}
		if (!ConfirmStop("添加模块将停止当前服务。继续？"))
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
					List<SpeakerVisionInspection.Models.Condition> list2 = new List<SpeakerVisionInspection.Models.Condition>(num2);
					CollectionsMarshal.SetCount(list2, num2);
					CollectionsMarshal.AsSpan(list2)[0] = new SpeakerVisionInspection.Models.Condition
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
				if (_runner?.IsRunning ?? false)
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
		if (rn.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch")
		{
			// 轮廓匹配：只有「绘制搜索区域」一种 ROI 功能（无检测项管理）；ROI 库已移除，所有 ROI 均为本节点私有
			bool isContour = rn.Type == "ContourMatch";
			var roiNames = NodeRois.ParseOwn(rn.Params.GetValueOrDefault("own_rois")).Select(r => r.Name).ToList();
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
			if (rn.Type == "ContourMatch")
			{
				EditorPanel.Children.Add(new Button
				{
					Content = "创建模板（轮廓建模）",
					Height = 26.0,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
					HorizontalAlignment = HorizontalAlignment.Left,
					ToolTip = "打开建模弹窗：已有模板会自动回填模板图/框选区域/基准点/参数，直接调参（滤波Sigma/边缘阈值/层数）→ 提取预览 → 保存即可更新模板"
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
					WriteOwnRois(rn, new List<(string, RoiRect)>());
					AppendLog("[ROI] " + rn.Name + ": 已清除本节点 ROI（恢复全图检测）");
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
			c.Click += delegate
			{
				if (!EnsureUnlocked("删除节点")) return;
				if (ConfirmStop("删除节点将停止当前服务。继续？"))
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
				foreach (SpeakerVisionInspection.Models.Condition condition in rule.Conditions)
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

	private void MoveNode(RecipeNode rn, int delta)
	{
		if (_recipe == null || !EnsureUnlocked("调整节点顺序"))
		{
			return;
		}
		int num = _recipe.Nodes.IndexOf(rn);
		int num2 = num + delta;
		if (num < 0 || num2 < 0 || num2 >= _recipe.Nodes.Count || !ConfirmStop("调整顺序将停止当前服务。继续？"))
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
			readOnlyList = ContourMatchNode.StaticParamDefs;
			break;
		case "PositionCorrection":
			readOnlyList = PositionCorrectionNode.StaticParamDefs;
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
			case "choice":
				ShowChoiceCombo(rn, item);
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
			.Where(n => n.Type == "ContourMatch") // 定位来源=输出 loc_* 契约键的节点（当前=轮廓匹配）
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
				Text = "上游暂无定位节点（轮廓匹配）；请先添加轮廓匹配并放到本节点之前",
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
			.Where(n => n.Type is "PatchCore" or "YOLO" or "Seg" or "SemanticSeg" or "ContourMatch")
			.ToList();
		if (downstream.Count == 0)
		{
			EditorPanel.Children.Add(new TextBlock
			{
				Text = "下游暂无检测节点（PatchCore/YOLO/实例分割/轮廓匹配）",
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
			if (!(await TryPrepareExecutionAsync()))
			{
				return;
			}
			PatchCoreNode runtimeNode = _pipeline.Nodes.OfType<PatchCoreNode>().FirstOrDefault((PatchCoreNode n) => string.Equals(n.Name, rn.Name, StringComparison.OrdinalIgnoreCase));
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
						PipelineResult result = _pipeline.Run(bgr, System.IO.Path.GetFileName(file), null, null, overrides);
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
		IModelNode modelNode = _pipeline?.Nodes.FirstOrDefault((IModelNode n) => string.Equals(n.Name, rn.Name, StringComparison.OrdinalIgnoreCase));
		if (modelNode != null)
		{
			modelNode.Enabled = rn.Enabled;
		}
	}

	internal void HotApplyParam(RecipeNode rn, string key, string value)
	{
		PushUndoSnapshot(); // 惰性捕获：_lastSnapshot 保存的是本参数写入前的状态
		if (key == "trigger_key")
		{
			RefreshKeyBindings(); // 按键控制节点键位变更 → 立即重绑
		}
		if (_pipeline == null)
		{
			return;
		}
		IModelNode modelNode = _pipeline.Nodes.FirstOrDefault((IModelNode n) => string.Equals(n.Name, rn.Name, StringComparison.OrdinalIgnoreCase));
		if (modelNode != null)
		{
			if ((modelNode is PatchCoreNode || modelNode is YoloNode || modelNode is SegNode || modelNode is SemanticSegNode || modelNode is ContourMatchNode) && key == "model_dir")
			{
				_pipelineDirty = true;
			}
			else
			{
				_pipeline.ApplyNodeParam(rn.Name, key, value);
			}
		}
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
			if (def.Key == "threshold" && _pipeline != null && _pipeline.ApplyNodeParam(rn.Name, "threshold", box.Text))
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
			List<SpeakerVisionInspection.Models.Condition> list3 = new List<SpeakerVisionInspection.Models.Condition>(num);
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

	private SpeakerVisionInspection.Models.Condition CreateDefaultDecisionCondition(RecipeNode rn)
	{
		RecipeNode recipeNode = GetUpstreamNodes(rn).FirstOrDefault();
		return new SpeakerVisionInspection.Models.Condition
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
		SpeakerVisionInspection.Models.Condition cond = rule.Conditions[conditionIndex];
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

	private static BitmapSource? MatToBitmap(Mat mat)
	{
		if (mat.IsDisposed || mat.Empty())
		{
			return null;
		}
		using Mat mat2 = mat.Clone();
		if (mat2.Channels() == 1)
		{
			Cv2.CvtColor(mat2, mat2, ColorConversionCodes.GRAY2BGR);
		}
		BitmapSource bitmapSource = BitmapSource.Create(mat2.Width, mat2.Height, 96.0, 96.0, PixelFormats.Bgr24, null, mat2.Data, mat2.Width * mat2.Height * 3, mat2.Width * 3);
		((Freezable)bitmapSource).Freeze();
		return bitmapSource;
	}

	/// <summary>弹窗写日志入口（线程安全）。</summary>
	internal void LogUi(string msg) => AppendLog(msg);

	private void AppendLog(string msg)
	{
		string line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
		if (((DispatcherObject)this).Dispatcher.CheckAccess())
		{
			AppendLogOnUiThread(line);
			return;
		}
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			AppendLogOnUiThread(line);
		}, Array.Empty<object>());
	}

	private void AppendLogOnUiThread(string line)
	{
		LogBox.AppendText(line + "\r\n");
		if (++_logLines > 800)
		{
			string text = LogBox.Text;
			int num = text.IndexOf('\n', text.Length / 2);
			if (num > 0)
			{
				LogBox.Text = text.Substring(num + 1);
				_logLines = 400;
			}
			else
			{
				LogBox.Clear();
				_logLines = 0;
			}
		}
		LogBox.ScrollToEnd();
	}

	private void Window_Closed(object? sender, EventArgs e)
	{
		// 相机最先释放（预览/硬触发激活时也必须关闭设备）：后台线程 + 超时上限，
		// 防「UI 线程同步等待 + 内部 await 回投」死锁把进程挂死（症状：弹窗残留桌面、相机被占用）。
		ICameraController? camera = _camera;
		if (camera != null)
		{
			try
			{
				Task.Run(() => camera.Dispose()).Wait(TimeSpan.FromSeconds(5));
			}
			catch (Exception exception)
			{
				AppLog.Warn("退出时释放相机失败", exception);
			}
		}

		try
		{
			_runner?.Dispose();
			_pipeline?.Dispose();
			if (_recipe != null)
			{
				_recipe.Name = RecipeCombo.Text.Trim();
				RecipeStore.SaveAsDefault(_recipe, ConfigDir);
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("退出时释放资源失败", exception);
		}
	}
}
