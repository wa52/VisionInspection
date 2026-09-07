using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SpeakerVisionInspection;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices(services =>
            {
                services.AddTransient<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        finally
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }

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
