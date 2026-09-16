using VisionInspection.Detection;
using Xunit;

namespace VisionInspection.Tests;

/// <summary>
/// 语义分割（SemanticSeg）ONNX 输出契约：
/// nc==1 导出二值图 {0=非该类(背景), 1=该类}（SemanticSegment head y.squeeze(1) &gt; 0），
/// nc&gt;1 导出 argmax 索引 = classes.txt 行序。RemapSingleClass 统一为内部契约（行索引 + 255=忽略）。
/// </summary>
public class SemanticSegNodeTests
{
    [Fact]
    public void ParamDefs_ExposeDetectionCropSaveOption()
    {
        var save = Assert.Single(SemanticSegNode.StaticParamDefs, p => p.Key == "save_mode");
        Assert.Equal("检测项切图保存类型", save.Label);
        Assert.Equal(new[] { "全部", "仅OK", "仅NG", "不保存" }, save.Choices);
        Assert.Contains(SemanticSegNode.StaticParamDefs, p =>
            p.Key == "crop_dir" && p.Label.Contains("检测项ROI"));
    }

    [Fact]
    public void RemapSingleClass_BinaryOutput_MapsValue1ToClass0_Value0ToIgnore()
    {
        byte[] map = [1, 0, 1, 1, 0];
        SemanticSegNode.RemapSingleClass(map, classCount: 1);

        Assert.Equal((byte)0, map[0]);      // 1 = 唯一类别 → 行 0
        Assert.Equal((byte)255, map[1]);    // 0 = 背景 → 忽略
        Assert.Equal((byte)0, map[2]);
        Assert.Equal((byte)0, map[3]);
        Assert.Equal((byte)255, map[4]);
    }

    [Fact]
    public void RemapSingleClass_MultiClass_Unchanged()
    {
        byte[] map = [0, 1, 2, 255, 3];
        SemanticSegNode.RemapSingleClass(map, classCount: 2);

        Assert.Equal(new byte[] { 0, 1, 2, 255, 3 }, map);
    }

    [Fact]
    public void RemapSingleClass_IgnoreRegions_StayIgnored()
    {
        byte[] map = [1, 255, 0, 255];
        SemanticSegNode.RemapSingleClass(map, classCount: 1);

        Assert.Equal((byte)0, map[0]);
        Assert.Equal((byte)255, map[1]);
        Assert.Equal((byte)255, map[2]);
        Assert.Equal((byte)255, map[3]);
    }

    [Fact]
    public void PositiveClasses_RoiNameMatchesModelClass_IgnoreSpacesAndCase()
    {
        var classNames = new[] { "泡棉 1", "背景" };

        // 检测项名与类名忽略空格匹配
        Assert.Equal(new[] { "泡棉 1" }, PositiveClasses.MatchClassesForRoi("泡棉1", classNames));
        Assert.Equal(new[] { "泡棉 1" }, PositiveClasses.MatchClassesForRoi("泡棉 1", classNames));
        // 未命中 → 空表（按全部非背景类别判定）
        Assert.Empty(PositiveClasses.MatchClassesForRoi("ROI", classNames));
        Assert.Empty(PositiveClasses.MatchClassesForRoi("", classNames));

        // MatchesRoi：空集合 = 全部类别；非空 = 命中（忽略空格）才计数
        Assert.True(PositiveClasses.MatchesRoi(Array.Empty<string>(), "泡棉 1"));
        Assert.True(PositiveClasses.MatchesRoi(new[] { "泡棉1" }, "泡棉 1"));
        Assert.False(PositiveClasses.MatchesRoi(new[] { "泡棉1" }, "背景"));
        Assert.False(PositiveClasses.MatchesRoi(new[] { "泡棉1" }, "类别 2"));
    }
}
