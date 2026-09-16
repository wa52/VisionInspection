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

public partial class MainWindow
{
	private void BtnRunOnce_Click(object sender, RoutedEventArgs e)
	{
		if (IsTriggerDriven)
		{
			AppendLog("[执行] 硬触发检测进行中（打一次跑一次），请先把相机触发模式切离硬触发");
			return;
		}
		_ = _execution.RunOnceAsync();
	}

	private async void BtnRunContinuous_Click(object sender, RoutedEventArgs e)
	{
		if (IsTriggerDriven)
		{
			AppendLog("[执行] 硬触发检测进行中（打一次跑一次），请先把相机触发模式切离硬触发");
			return;
		}
		await _execution.ToggleContinuousAsync();
	}

	/// <summary>图像源节点的帧提供器工厂：闭包延迟解析相机（连接后抓帧自动生效），相机未连接时抓帧返回 null。</summary>
	private Func<int, Mat?> FrameProviderFactory() =>
		ms => GetFrameGrabber()?.Grab(ms, AppendLog);

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

	// ===== 硬触发驱动检测（无独立开关：相机触发模式=硬触发即自动生效，打一次跑一次）=====

	/// <summary>当前是否处于硬触发驱动的自动检测状态。</summary>
	internal bool IsTriggerDriven => _triggerDriven != null;

	/// <summary>由渲染定时器每拍调用：按「相机已连接且触发模式=硬触发」自动装配，离开该状态自动拆卸。</summary>
	internal void EnsureTriggerDrivenState()
	{
		var wanted = _camera is { State: CameraConnectionState.Connected, TriggerMode: CameraTriggerMode.Hardware };
		if (wanted == (_triggerDriven != null))
		{
			return;
		}
		if (wanted && (DateTime.Now - _lastTriggerDrivenFailAt).TotalSeconds < 5)
		{
			return; // 装配失败后 5s 内不重试，防日志刷屏
		}
		if (Interlocked.Exchange(ref _triggerDrivenBusy, 1) == 1)
		{
			return;
		}
		_ = wanted ? AssembleTriggerDrivenAsync() : TearDownTriggerDrivenAsync();
	}

	private async Task AssembleTriggerDrivenAsync()
	{
		try
		{
			var camera = _camera!;
			if (!(await _execution.PrepareAsync()))
			{
				_lastTriggerDrivenFailAt = DateTime.Now;
				AppendLog("[触发检测] 流水线未就绪，暂不启用硬触发检测（修复后自动重试）");
				return;
			}
			var pipeline = _execution.CurrentPipeline!;
			_store ??= new CameraParameterStore(System.IO.Path.Combine(ConfigDir, "camera.json"));
			var trigger = _store.LoadTriggerSettings();

			_plcChannel = CreatePlcChannel();
			var service = new CameraInspectionService(
				camera,
				_plcChannel,
				pipeline,
				() => trigger,
				System.IO.Path.Combine(ConfigDir, "results"),
				AppendLog,
				_commRuntime);
			service.Result += OnTriggerDrivenResult;
			service.Preview += OnTriggerDrivenPreview;
			service.StateChanged += OnTriggerDrivenStateChanged;
			_triggerDriven = service;

			await camera.ApplyTriggerSettingsAsync(trigger);
			service.Arm();
			await camera.StartHardTriggerAsync();
			AppendLog("[触发检测] 硬触发模式已生效：PLC 触发一次 → 检测一次；结果经相机 IO 电平回传（CameraIo 节点按输出类型/输出线/有效电平发脉冲，如 NG→IO2、OK→IO1）");
			UpdateStatePanel();
			UpdateButtonState();
		}
		catch (Exception ex)
		{
			_lastTriggerDrivenFailAt = DateTime.Now;
			AppendLog($"[触发检测] 启用失败: {ex.Message}（将自动重试）");
			await TearDownTriggerDrivenCoreAsync();
		}
		finally
		{
			Interlocked.Exchange(ref _triggerDrivenBusy, 0);
		}
	}

	private Task TearDownTriggerDrivenAsync() => TearDownTriggerDrivenCoreAsync();

	private async Task TearDownTriggerDrivenCoreAsync()
	{
		try
		{
			var service = _triggerDriven;
			_triggerDriven = null;
			if (service != null)
			{
				service.Result -= OnTriggerDrivenResult;
				service.Preview -= OnTriggerDrivenPreview;
				service.StateChanged -= OnTriggerDrivenStateChanged;
				service.Disarm();
			}
			if (_camera is { State: CameraConnectionState.Connected } && _camera.IsHardTriggering)
			{
				await _camera.StopHardTriggerAsync();
			}
			service?.Dispose();
			_plcChannel?.Dispose();
			_plcChannel = null;
			if (service != null)
			{
				AppendLog("[触发检测] 硬触发检测已停止（触发模式已切离硬触发或相机断开）");
				UpdateStatePanel();
				UpdateButtonState();
			}
		}
		catch (Exception ex)
		{
			AppLog.Warn("停止硬触发检测失败", ex);
		}
		finally
		{
			Interlocked.Exchange(ref _triggerDrivenBusy, 0);
		}
	}

	/// <summary>PLC 状态记录器：PLC 只读电平信号（检测结果由相机 IO 电平回传，见「相机IO通信」节点），状态流转仅记日志便于联调。</summary>
	private IPlcClient CreatePlcChannel() =>
		new LoggingPlcClient(msg => AppendLog("[PLC状态] " + msg));

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
		if (_execution.IsRunning || IsTriggerDriven)
		{
			AppendLog("[按键触发] 已有执行/硬触发检测在进行中，忽略本次按键");
			return;
		}

		AppendLog("[按键触发] 按下「" + KeyControlNode.KeyName(key) + "」→ 执行一次检测流程");
		_ = _execution.RunOnceAsync();
	}
}
