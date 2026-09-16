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
using VisionInspection.Plc;
using VisionInspection.Production;
using VisionInspection.Services;
using WpfWindow = System.Windows.Window;
using ModelsCondition = VisionInspection.Models.Condition;

namespace VisionInspection;

public partial class MainWindow : WpfWindow
{
	private static readonly string ConfigDir = AppContext.BaseDirectory;
	private static readonly string LastRecipePathFile = System.IO.Path.Combine(ConfigDir, "last_recipe_path.txt");

	private Recipe? _recipe;

	/// <summary>当前打开的方案文件路径；顶部方案栏显示其文件名而非 JSON 内部 Name。</summary>
	private string? _recipeFilePath;

	/// <summary>方案执行编排（应用层）：Pipeline 生命周期 + 单次/连续运行；相机能力经委托注入。</summary>
	private readonly RecipeExecutionService _execution;

	/// <summary>硬触发驱动检测（相机触发模式=硬触发时自动装配：PLC 触发→检测→IO发波+PLC结果；无独立生产模式开关）。</summary>
	private CameraInspectionService? _triggerDriven;

	/// <summary>生产通信运行时（发送数据/接收数据节点收发文本；与通信管理弹窗互斥守卫经 IsHeld）。</summary>
	private readonly CommRuntimeService _commRuntime;

	private IPlcClient? _plcChannel;

	/// <summary>装配/拆卸防重入（0=空闲）。</summary>
	private int _triggerDrivenBusy;

	/// <summary>最近一次装配失败时间（失败后 5s 内不重试，防日志刷屏）。</summary>
	private DateTime _lastTriggerDrivenFailAt = DateTime.MinValue;

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

	private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

	/// <summary>执行结果待渲染队列（后台投递、UI 定时器消费；跳帧时释放被跳过结果的节点图）。</summary>
	private readonly PendingResultQueue _pendingQueue = new();

	private readonly Dictionary<string, BitmapSource> _thumbImages;

	/// <summary>最近一次执行的节点结果值（位置修正「设置基准位姿」读取 loc_* 用）。</summary>
	private IReadOnlyDictionary<string, Dictionary<string, string>>? _lastNodeValues;

	/// <summary>模块结果历史（海康式：每节点最近 N 次执行记录，供模块弹窗「模块结果/当前结果/历史结果」展示）。</summary>
	private readonly ModuleResultHistory _moduleHistory = new();

	private bool _thumbStripVisible;

	private string? _selectedThumbName;

	private readonly ScaleTransform _imageScale = new(1, 1);

	private readonly TranslateTransform _imagePan = new();

	private Point _imagePanStart;

	private Vector _imagePanOrigin;

	private bool _isPanningImage;

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

	/// <summary>方案编辑历史（撤销/重做）：Recipe JSON 快照栈（纯逻辑 RecipeEditHistory）；_restoringSnapshot 守卫恢复期间的 Push 重入。</summary>
	private readonly RecipeEditHistory _editHistory = new();
	private bool _restoringSnapshot;

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

	/// <summary>圆查找节点圆环叠加层（当前显示节点是 CircleFind 时与 _roiPolys 互斥使用）。</summary>
	private readonly List<(string Name, RoiRing Ring, Ellipse Outer, Ellipse Inner, int RingIndex)> _ringPolys = new();

	/// <summary>位置修正预览激活时各圆环修正后的几何（与 own_rings 同索引；命中测试用）。</summary>
	private readonly List<RoiRing> _ringPreviewHits = new();

	/// <summary>圆环拖拽起始几何（归一化；矩形拖拽用 _roiDragOrig，圆环用本字段）。</summary>
	private RoiRing _ringDragOrig;

	private Ellipse? _ringPreviewOuter;

	private Ellipse? _ringPreviewInner;

	private const double RoiHandleTolerancePx = 7.0;

	private const double RoiRotateOffsetPx = 24.0;

	private static readonly Dictionary<string, string> NodeTypeLabels = new Dictionary<string, string>
	{
		["PatchCore"] = "PatchCore 检测",
		["Decision"] = "条件检测",
		["SaveImage"] = "输出图像",
		["OverlayDisplay"] = "叠加显示",
		["ImageSource"] = "图像源",
		["Binarize"] = "图像二值化",
		["Geometry"] = "几何变换",
		["ColorTransform"] = "颜色变换",
		["CameraIo"] = "相机IO通信",
		["YOLO"] = "目标检测",
		["Seg"] = "实例分割",
		["SemanticSeg"] = "语义分割",
		["ContourMatch"] = "轮廓匹配",
		["FastMatch"] = "快速匹配",
		["PositionCorrection"] = "位置修正",
		["KeyControl"] = "按键控制",
		["CharRec"] = "字符识别",
		["Blob"] = "Blob分析",
		["SendData"] = "发送数据",
		["ReceiveData"] = "接收数据",
		["LineFind"] = "直线查找",
		["CircleFind"] = "圆查找",
		["LineLineMeasure"] = "线线测量",
		["LineCircleMeasure"] = "线圆测量",
		["CircleCircleMeasure"] = "圆圆测量",
		["PointCircleMeasure"] = "点圆测量"
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
		_paramBoxes = new Dictionary<string, TextBox>();
		_lastRoiCheckAt = DateTime.MinValue;
		_thumbImages = new Dictionary<string, BitmapSource>();
		_roiDragHandle = RoiHandle.None;
		_roiPolys = new List<(RecipeRoi Roi, Polygon Poly, bool Own, int OwnIndex)>();
		_roiHandleRects = new List<Rectangle>();
		InitializeComponent();
		DataContext = new MainWindowViewModel();
		// 预热线程池：匹配节点按角度×行带/候选分块并行，短时突发任务依赖池内现成线程——
		// 不预热的话首帧会因线程池缓慢注入而多耗几十毫秒
		System.Threading.ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);
		CheckCameraRuntime();
		_commRuntime = new CommRuntimeService(
			new CommDeviceStore(System.IO.Path.Combine(ConfigDir, "comm.json")),
			AppendLog);
		_execution = new RecipeExecutionService(
			() => _recipe,
			AppendLog,
			FrameProviderFactory,
			CameraIoOutputOrNull,
			() => _commRuntime);
		_execution.StateChanged += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				UpdateStatePanel();
				UpdateButtonState();
			}, Array.Empty<object>());
		};
		_execution.Completed += OnRunnerCompleted;
		_execution.Failed += delegate(string msg)
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
			EnsureTriggerDrivenState();
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

	/// <summary>当前方案（弹窗访问用；调用前方案已加载）。</summary>
	internal Recipe CurrentRecipe => _recipe!;

	// ===== 菜单/快捷键命令（复用工具栏同名处理）=====

	private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();


	private async Task<bool> ConfirmStop(string msg)
	{
		if (IsTriggerDriven)
		{
			if (MessageBox.Show(msg + "\n当前硬触发检测将停止。", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				return false;
			}
			await TearDownTriggerDrivenCoreAsync();
			return true;
		}
		if (!_execution.IsRunning)
		{
			return true;
		}
		return MessageBox.Show(msg, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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
		// 硬触发驱动检测最先停止（订阅了相机事件，必须先于相机释放）。
		try
		{
			var service = _triggerDriven;
			_triggerDriven = null;
			service?.Disarm();
			service?.Dispose();
			_plcChannel?.Dispose();
			_plcChannel = null;
		}
		catch (Exception exception)
		{
			AppLog.Warn("退出时停止触发检测失败", exception);
		}

		// 通信运行时先于相机释放：节点链路里无 UI 亲和，但同样要避免退出时还挂着收发线程
		try
		{
			_commRuntime.Dispose();
		}
		catch (Exception exception)
		{
			AppLog.Warn("退出时释放通信运行时失败", exception);
		}

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
			_execution.Dispose();
		}
		catch (Exception exception)
		{
			AppLog.Warn("退出时释放资源失败", exception);
		}

		// 保存与资源释放分开，避免 Dispose 异常导致节点参数无法持久化。
		SaveRecipeSilently();
	}
}
