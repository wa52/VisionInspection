using System.IO;
using System.Text;

namespace SpeakerVisionInspection.Services;

public static class AppLog
{
    private static readonly object Sync = new();
    private static string _directory = Path.Combine(AppContext.BaseDirectory, "logs");

    public static string Directory
    {
        get => _directory;
        set
        {
            lock (Sync)
            {
                _directory = value;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        lock (Sync)
        {
            try
            {
                var dir = _directory;
                System.IO.Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"app_{DateTime.Now:yyyyMMdd}.log");
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(level).Append("] ");
                sb.AppendLine(message);
                if (exception is not null)
                {
                    sb.AppendLine(exception.ToString());
                }

                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }
}
