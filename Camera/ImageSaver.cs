using System.IO;
using System.Windows.Media.Imaging;

namespace SpeakerVisionInspection.Camera;

/// <summary>图像保存：PNG 编码 + 时间戳文件名（独占创建，不覆盖已有文件）。</summary>
public sealed class ImageSaver
{
    private readonly string _outputDirectory;

    public ImageSaver(string outputDirectory)
    {
        _outputDirectory = outputDirectory;
    }

    public string OutputDirectory => _outputDirectory;

    /// <summary>生成文件名：IMG_yyyyMMdd_HHmmss_fff.png。</summary>
    public static string BuildFileName(DateTime now, string prefix = "IMG", string extension = "png")
        => $"{prefix}_{now:yyyyMMdd_HHmmss_fff}.{extension}";

    public async Task<string> SaveAsync(BitmapSource image, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CreateUniqueFile();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Open(path, FileMode.Open, FileAccess.Write);
            encoder.Save(stream);
            return path;
        }, cancellationToken);
    }

    /// <summary>独占创建文件（CreateNew 原子性防并发覆盖）；已存在则递增序号。</summary>
    private string CreateUniqueFile()
    {
        var stamp = DateTime.Now;
        for (var i = 0; ; i++)
        {
            var name = BuildFileName(stamp);
            var path = Path.Combine(_outputDirectory, name);
            try
            {
                File.Open(path, FileMode.CreateNew).Dispose();
                return path;
            }
            catch (IOException) when (i < 999)
            {
                stamp = stamp.AddMilliseconds(1);
            }
        }
    }
}
