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
	private void CheckCameraRuntime()
	{
		var config = VisionMasterConfigResolver.ResolveFromProcessEnvironment(System.IO.Path.Combine(ConfigDir, "appsettings.json"));
		var result = EnvironmentCheckService.Check(config, Environment.Is64BitProcess, ConfigDir);
		var failed = result.Items.Where(i => !i.Ok).ToList();
		// 自检通过不打日志（避免启动噪音）；缺失时日志给出可操作提示
		if (failed.Count > 0)
		{
			AppendLog("[环境] 相机功能不可用：" + string.Join("；", failed.Select(i => $"{i.Name}: {i.Detail}")));
		}
	}

	private ICameraController CreateCamera()
	{
		VisionMasterConfig visionMasterConfig = VisionMasterConfigResolver.ResolveFromProcessEnvironment(System.IO.Path.Combine(ConfigDir, "appsettings.json"));
		return new MvCameraControlCameraController(visionMasterConfig.MvsRuntimeDir);
	}

	private void Camera_FrameReceived(object? sender, CameraFrameEventArgs e)	{
		if (_execution.IsRunning || IsTriggerDriven)
		{
			// 软件执行中 / 硬触发驱动检测中：预览不抢帧（检测结果由各自链路回显）
			return;
		}
		CameraFrame frame = e.Frame;
		byte[] array = new byte[frame.DataLength];
		Marshal.Copy(frame.Data, array, 0, frame.DataLength);
		FrameSnapshot snapshot = new FrameSnapshot(array, frame.Width, frame.Height, frame.PixelFormat);
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			using Mat mat = CameraFrameMatConverter.ToBgrMat(snapshot.Data, snapshot.Width, snapshot.Height, snapshot.PixelFormat);
			ResultImage.Source = UiExtensions.MatToBitmap(mat);
			ResultCaption.Text = "相机预览";
			RenderRoiOverlay();
		}, Array.Empty<object>());
	}

	// ===== 工具栏入口：相机管理弹窗（设备级采集/触发）；IO 输出配置在「相机IO通信」节点弹窗 =====

	private async void BtnCameraManager_Click(object sender, RoutedEventArgs e) => await OpenCameraManagerAsync();

	/// <summary>工具栏入口：通信管理弹窗（设备级通信工具，TCP 客户端联机调试）。生产运行中拒绝操作。</summary>
	private void BtnCommManager_Click(object sender, RoutedEventArgs e)
	{
		if (_execution.IsRunning)
		{
			MessageBox.Show("生产检测运行中，通信设备暂不可配置", "通信管理", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}

		new CommManagementDialog(
			this,
			new CommDeviceStore(System.IO.Path.Combine(ConfigDir, "comm.json")),
			AppendLog,
			_commRuntime).ShowDialog();
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
			() => !_execution.IsRunning).Show();
	}
}
