using System.IO;
using OpenCvSharp;

namespace VisionInspection.Detection;

/// <summary>单个字模样本：字符标签 + 归一化二值像素（0/255，行优先 NormW×NormH）。</summary>
public sealed record CharSample(string Label, byte[] Pixels);

/// <summary>
/// 字模库（字符模板库）目录读写。目录契约：&lt;字模库目录&gt;/&lt;字符&gt;/&lt;n&gt;.png ——
/// 每字符一个子目录（目录名 = 字符标签；含路径非法字符时整串 URL 转义），内为归一化二值样本 PNG
/// （32×48 CV_8UC1，前景 255）。每字符可多采样（1.png/2.png/…），识别时取与样本最高相似度。
/// </summary>
public static class CharTemplateLibrary
{
    /// <summary>样本归一化尺寸（宽×高）。</summary>
    public const int NormW = 32;
    public const int NormH = 48;

    /// <summary>扫描字模库 → 全部样本（目录不存在返回空表；单个损坏样本跳过不阻断）。</summary>
    public static List<CharSample> Load(string dir)
    {
        var result = new List<CharSample>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return result;
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var label = Uri.UnescapeDataString(Path.GetFileName(sub));
            foreach (var file in Directory.GetFiles(sub, "*.png").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    using var mat = Cv2.ImRead(file, ImreadModes.Grayscale);
                    if (mat.Empty() || mat.Width != NormW || mat.Height != NormH) continue;
                    if (!mat.GetArray(out byte[] buf)) continue; // 只读拷贝
                    // 兼容手放的灰度样本：≥128 统一为前景 255
                    for (var i = 0; i < buf.Length; i++)
                    {
                        buf[i] = buf[i] >= 128 ? (byte)255 : (byte)0;
                    }
                    result.Add(new CharSample(label, buf));
                }
                catch
                {
                    // 单个样本损坏/不可读：跳过
                }
            }
        }
        return result;
    }

    /// <summary>保存一个字模样本（同字符自动递增序号，取已有最大序号+1）；返回落盘路径。</summary>
    public static string SaveSample(string dir, string label, byte[] pixels)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("字符标签不能为空", nameof(label));
        }
        if (pixels.Length != NormW * NormH)
        {
            throw new ArgumentException($"像素数组长度应为 {NormW * NormH}", nameof(pixels));
        }
        var sub = Path.Combine(dir, EscapeLabel(label));
        Directory.CreateDirectory(sub);
        var next = Directory.GetFiles(sub, "*.png")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var i) ? i : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        using var mat = new Mat(NormH, NormW, MatType.CV_8UC1);
        mat.SetArray(pixels); // 写入托管数组必须 SetArray 落回 Mat
        var path = Path.Combine(sub, next + ".png");
        if (!Cv2.ImWrite(path, mat))
        {
            throw new IOException($"字模样本写入失败: {path}");
        }
        return path;
    }

    /// <summary>删除字模样本文件（不存在时静默）。</summary>
    public static void DeleteSample(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>字模样本清单（训练弹窗列表/删除用）：按字符目录升序、样本序号升序。</summary>
    public static List<(string Label, string Path)> ListSamples(string dir)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return result;
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var label = Uri.UnescapeDataString(Path.GetFileName(sub));
            foreach (var file in Directory.GetFiles(sub, "*.png").OrderBy(p => p, StringComparer.Ordinal))
            {
                result.Add((label, file));
            }
        }
        return result;
    }

    /// <summary>字符 → 目录名：含路径非法字符（:?* 等）时整串 URL 转义，否则原样（中文可直接作目录名）。</summary>
    internal static string EscapeLabel(string label) =>
        label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? Uri.EscapeDataString(label) : label;
}
