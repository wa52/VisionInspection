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
	private void LoadRecipe()
	{
		var lastPath = TryReadLastRecipePath();
		var loadPath = lastPath != null && File.Exists(lastPath)
			? lastPath
			: (File.Exists(System.IO.Path.Combine(ConfigDir, RecipeStore.DefaultRecipeName))
				? System.IO.Path.Combine(ConfigDir, RecipeStore.DefaultRecipeName)
				: null);
		if (loadPath != null)
		{
			try
			{
				_recipe = RecipeStore.LoadFile(loadPath);
				_recipeFilePath = loadPath;
				RememberRecipePath(loadPath);
			}
			catch (Exception ex)
			{
				_recipe = null;
				_recipeFilePath = null;
				AppendLog("[警告] 上次方案加载失败，已打开空白新建方案: " + ex.Message);
			}
		}
		else
		{
			_recipe = null;
			_recipeFilePath = null;
		}
		if (_recipe == null)
		{
			_recipe = Recipe.CreateDefault();
		}
		_recipe.BaseDir = _recipeFilePath != null
			? (System.IO.Path.GetDirectoryName(_recipeFilePath) ?? ConfigDir)
			: ConfigDir;
		MigrateLegacyRois(_recipe);
		SetRecipeComboName(GetRecipeFileDisplayName());
		RefreshNodeTree();
		AppendLog("方案已加载: " + GetRecipeFileDisplayName());
		ResetEditHistory();
		string environmentVariable = Environment.GetEnvironmentVariable("DEPLOY_MODEL_DIR");
		if (!string.IsNullOrWhiteSpace(environmentVariable))
		{
			AppendLog("[警告] 检测到 DEPLOY_MODEL_DIR 环境变量，模型路径将被覆盖为: " + environmentVariable + "（保存的模型路径不生效）");
		}
	}

	private static string? TryReadLastRecipePath()
	{
		try
		{
			var path = File.Exists(LastRecipePathFile) ? File.ReadAllText(LastRecipePathFile).Trim() : "";
			return path.Length == 0 ? null : System.IO.Path.GetFullPath(path);
		}
		catch
		{
			return null;
		}
	}

	private static void RememberRecipePath(string path)
	{
		try
		{
			File.WriteAllText(LastRecipePathFile, System.IO.Path.GetFullPath(path));
		}
		catch
		{
			// 最近文件记录失败不应阻止方案打开或保存。
		}
	}

	private void SetRecipeComboName(string? name)
	{
		// ComboBox 当前没有绑定列表，清除旧选中项后再写文本，避免打开方案后仍显示旧名称。
		RecipeCombo.SelectedItem = null;
		RecipeCombo.Text = name ?? "未命名方案";
		RecipeCombo.UpdateLayout();
	}

	private string GetRecipeFileDisplayName()
	{
		if (string.IsNullOrWhiteSpace(_recipeFilePath))
		{
			return "新建方案";
		}
		return System.IO.Path.GetFileNameWithoutExtension(_recipeFilePath);
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

	private async void RecipeNew_Click(object sender, RoutedEventArgs e)
	{
		if (await ConfirmStop("新建方案将停止当前服务。继续？"))
		{
			_recipe = Recipe.CreateDefault();
			_recipeFilePath = null;
			_recipe.BaseDir = ConfigDir;
			ClearExecutionResults();
			SetRecipeComboName(GetRecipeFileDisplayName());
			RefreshNodeTree();
			ResetEditHistory();
			AppendLog("已新建默认方案");
		}
	}

	private async void RecipeOpen_Click(object sender, RoutedEventArgs e)
	{
		if (!await ConfirmStop("打开方案将停止当前服务。继续？"))
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
			_recipeFilePath = openFileDialog.FileName;
			RememberRecipePath(openFileDialog.FileName);
			_recipe.BaseDir = System.IO.Path.GetDirectoryName(openFileDialog.FileName) ?? ConfigDir;
			ClearExecutionResults();
			SetRecipeComboName(GetRecipeFileDisplayName());
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
		try
		{
			var path = _recipeFilePath ?? System.IO.Path.Combine(ConfigDir, RecipeStore.DefaultRecipeName);
			RecipeStore.Save(_recipe, path);
			_recipeFilePath = path;
			RememberRecipePath(path);
			SetRecipeComboName(GetRecipeFileDisplayName());
			AppendLog("方案已保存: " + GetRecipeFileDisplayName());
		}
		catch (Exception ex)
		{
			MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	/// <summary>参数热编辑的无提示持久化，避免只依赖退出事件导致修改丢失。</summary>
	private void SaveRecipeSilently()
	{
		if (_recipe == null)
		{
			return;
		}

		try
		{
			var path = _recipeFilePath ?? System.IO.Path.Combine(ConfigDir, RecipeStore.DefaultRecipeName);
			RecipeStore.Save(_recipe, path);
			_recipeFilePath = path;
			RememberRecipePath(path);
		}
		catch (Exception ex)
		{
			AppLog.Warn("参数自动保存失败", ex);
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
			FileName = GetRecipeFileDisplayName() + ".json",
			InitialDirectory = ConfigDir
		};
		if (saveFileDialog.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			RecipeStore.Save(_recipe, saveFileDialog.FileName);
			_recipeFilePath = saveFileDialog.FileName;
			RememberRecipePath(saveFileDialog.FileName);
			SetRecipeComboName(GetRecipeFileDisplayName());
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
		_editHistory.Reset(SnapshotRecipe());
		_moduleHistory.Clear();
		UpdateUndoRedoState();
		RefreshInspectorModuleResult();
	}

	/// <summary>变更入口调用：把「上一个快照点」压入撤销栈（惰性捕获）；状态未变则跳过；任何新撤销点清空重做栈。</summary>
	private void PushUndoSnapshot()
	{
		if (_restoringSnapshot || _recipe is null) return;
		if (_editHistory.Push(SnapshotRecipe())) UpdateUndoRedoState();
	}

	private void UndoRecipe()
	{
		if (_recipe is null || !_editHistory.TryUndo(SnapshotRecipe(), out var snap)) return;
		RestoreRecipeSnapshot(snap, "撤销");
	}

	private void RedoRecipe()
	{
		if (_recipe is null || !_editHistory.TryRedo(SnapshotRecipe(), out var snap)) return;
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
				SetRecipeComboName(GetRecipeFileDisplayName());
				RefreshNodeTree();
				AppendLog($"[编辑] {verb}：方案已恢复到上一步（可撤销 {_editHistory.UndoCount} 步 / 可恢复 {_editHistory.RedoCount} 步）");
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
		_editHistory.Rebaseline(SnapshotRecipe());
		UpdateUndoRedoState();
	}

	private void UpdateUndoRedoState()
	{
		BtnUndo.IsEnabled = _editHistory.UndoCount > 0;
		BtnRedo.IsEnabled = _editHistory.RedoCount > 0;
	}

	private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoRecipe();

	private void BtnRedo_Click(object sender, RoutedEventArgs e) => RedoRecipe();

	private void CmdRecipeNew_Executed(object sender, ExecutedRoutedEventArgs e) => RecipeNew_Click(sender, e);

	private void CmdRecipeOpen_Executed(object sender, ExecutedRoutedEventArgs e) => RecipeOpen_Click(sender, e);

	private void CmdRecipeSave_Executed(object sender, ExecutedRoutedEventArgs e) => RecipeSave_Click(sender, e);

	private void CmdUndo_Executed(object sender, ExecutedRoutedEventArgs e) => UndoRecipe();

	private void CmdRedo_Executed(object sender, ExecutedRoutedEventArgs e) => RedoRecipe();

	private void CmdUndo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
	{
		e.CanExecute = _editHistory.UndoCount > 0;
		e.Handled = true;
	}

	private void CmdRedo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
	{
		e.CanExecute = _editHistory.RedoCount > 0;
		e.Handled = true;
	}
}
