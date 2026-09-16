using System.IO;
using System.Text.Json;

namespace VisionInspection.Detection;

/// <summary>单层轮廓点：相对基准点的坐标（该层像素系）+ 归一化梯度方向 (Dx,Dy)。</summary>
public readonly record struct TemplatePoint(float X, float Y, float Dx, float Dy);

/// <summary>
/// 轮廓匹配模板：分层边缘点集（位置+梯度方向）+ 基准点 + 建模参数。
/// 层级约定：LevelPoints[0] = 原始分辨率，LevelPoints[i] = 1/2^i 缩放层（pyrDown 链）。
/// 点坐标相对基准点（该层内缩放后的基准点），匹配时输出基准点在搜索图中的位姿。
/// 持久化：模板目录 shape_template.json。
/// </summary>
public sealed class ShapeTemplate
{
    /// <summary>基准点（原始分辨率模板图像素坐标）。</summary>
    public double ReferenceX { get; set; }

    public double ReferenceY { get; set; }

    /// <summary>建模参数：平滑 sigma。</summary>
    public double Sigma { get; set; } = 1.0;

    /// <summary>建模参数：边缘对比度阈值。</summary>
    public double MinContrast { get; set; } = 30.0;

    /// <summary>每层点数上限。</summary>
    public int MaxPointsPerLevel { get; set; } = 2000;

    /// <summary>建模时框选的模板 ROI（原始分辨率，便于复现/重建模）。</summary>
    public double RoiX { get; set; }
    public double RoiY { get; set; }
    public double RoiW { get; set; }
    public double RoiH { get; set; }

    /// <summary>分层点集，[i] = 1/2^i 层。</summary>
    public List<List<TemplatePoint>> LevelPoints { get; set; } = new();

    public int Levels => LevelPoints.Count;

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        File.WriteAllText(Path.Combine(dir, "shape_template.json"), json);
    }

    public static ShapeTemplate Load(string dir)
    {
        var path = Path.Combine(dir, "shape_template.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"模板文件不存在: {path}");
        }
        var template = JsonSerializer.Deserialize<ShapeTemplate>(File.ReadAllText(path));
        return template ?? throw new InvalidDataException($"模板文件解析失败: {path}");
    }
}
