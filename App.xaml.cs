using System.Windows;
using System.Windows.Threading;

namespace SpeakerVisionInspection;

public partial class App : Application
{
    /// <summary>UI 线程兜底异常处理：记录日志并提示，不允许显示层异常杀死整个进程。</summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.AppLog.Error("UI 线程未处理异常", e.Exception);
        MessageBox.Show(
            $"发生未处理异常，程序已拦截（详情见 logs 日志）:\n{e.Exception.Message}",
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
