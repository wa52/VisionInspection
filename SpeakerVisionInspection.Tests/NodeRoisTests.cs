using SpeakerVisionInspection.Detection;
using SpeakerVisionInspection.Models;
using Xunit;

namespace SpeakerVisionInspection.Tests;

/// <summary>节点私有 ROI（own_rois）解析/序列化的纯逻辑测试（方案级 ROI 库已移除）。</summary>
public class NodeRoisTests
{
    [Fact]
    public void ParseOwn_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(NodeRois.ParseOwn(null));
        Assert.Empty(NodeRois.ParseOwn(""));
        Assert.Empty(NodeRois.ParseOwn("  |  "));
    }

    [Fact]
    public void ParseOwn_SerializeOwn_RoundTrip()
    {
        var rois = new List<(string, RoiRect)>
        {
            ("缺陷区", new RoiRect(0.3, 0.5, 0.4, 0.5, 45)),
            ("ROI_2", new RoiRect(0.7, 0.2, 0.2, 0.2, 0)),
        };
        var raw = NodeRois.SerializeOwn(rois);
        Assert.Equal("缺陷区;0.3,0.5,0.4,0.5,45|ROI_2;0.7,0.2,0.2,0.2,0", raw);

        var parsed = NodeRois.ParseOwn(raw);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("缺陷区", parsed[0].Name);
        Assert.Equal(0.3, parsed[0].Rect.CenterX, 4);
        Assert.Equal(45, parsed[0].Rect.AngleDeg, 4);
        Assert.Equal("ROI_2", parsed[1].Name);
        Assert.Equal(0.2, parsed[1].Rect.H, 4);
    }

    [Fact]
    public void ParseOwn_SkipsInvalidEntries()
    {
        // 无分号 / 几何非法 / 空名 → 跳过；合法条目保留
        var parsed = NodeRois.ParseOwn("badentry|;0.5,0.5,0.2,0.2,0|ok;0.1,0.1,0.2,0.2,0");
        Assert.Single(parsed);
        Assert.Equal("ok", parsed[0].Name);
    }

    [Fact]
    public void UniqueOwnName_IncrementsWithinOwnSet()
    {
        var rois = new List<(string, RoiRect)>
        {
            ("ROI", new RoiRect(0.1, 0.1, 0.2, 0.2, 0)),
            ("ROI_2", new RoiRect(0.2, 0.2, 0.2, 0.2, 0)),
        };
        Assert.Equal("ROI_3", NodeRois.UniqueOwnName(rois, "ROI"));
        Assert.Equal("区域", NodeRois.UniqueOwnName(rois, "区域"));
    }

    [Fact]
    public void OwnRois_ReadFromParamsOnly()
    {
        // own_rois 参数即唯一 ROI 来源（roi_mode 等旧参数残留不影响解析）
        var node = new RecipeNode
        {
            Name = "02 YOLO",
            Type = "YOLO",
            Params = new Dictionary<string, string>
            {
                ["roi_mode"] = "继承方案ROI",
                ["own_rois"] = NodeRois.SerializeOwn(new List<(string, RoiRect)> { ("mine", new RoiRect(0.5, 0.5, 0.3, 0.3, 0)) }),
            },
        };
        var own = NodeRois.ParseOwn(node.Params.GetValueOrDefault("own_rois"));
        Assert.Single(own);
        Assert.Equal("mine", own[0].Name);
    }

    [Fact]
    public void ParseOwnFull_MetaRoundTrip_AndLegacyCompat()
    {
        // 旧格式（无元数据段）→ 全默认（启用/参与判定/无阈值/存图）
        var legacy = NodeRois.ParseOwnFull("A;0.5,0.5,0.2,0.2,0");
        var legacyItem = Assert.Single(legacy);
        Assert.True(legacyItem.Meta.IsDefault);
        Assert.True(NodeRois.Judges(legacyItem.Meta));
        Assert.Null(legacyItem.Meta.ThresholdValue);

        // 新格式：默认项序列化保持旧格式干净；停用+观察+阈值+不存图写入第 3 段
        var raw = NodeRois.SerializeOwnFull(new List<RoiItem>
        {
            new("A", new RoiRect(0.5, 0.5, 0.2, 0.2, 0), new RoiMeta()),
            new("B", new RoiRect(0.6, 0.6, 0.1, 0.1, 15), new RoiMeta(false, "仅观察", "0.8", false)),
        });
        Assert.StartsWith("A;0.5,0.5,0.2,0.2,0|B;", raw);
        Assert.Contains("e=0,d=仅观察,t=0.8,s=0", raw);

        var parsed = NodeRois.ParseOwnFull(raw);
        Assert.Equal(2, parsed.Count);
        Assert.True(parsed[0].Meta.IsDefault);
        Assert.False(parsed[1].Meta.Enabled);
        Assert.Equal("仅观察", parsed[1].Meta.Judge);
        Assert.Equal(0.8, parsed[1].Meta.ThresholdValue);
        Assert.False(parsed[1].Meta.SaveImage);
        Assert.False(NodeRois.Judges(parsed[1].Meta));
        Assert.Equal(15, parsed[1].Rect.AngleDeg, 4);

        // 往返稳定
        Assert.Equal(raw, NodeRois.SerializeOwnFull(parsed));
    }
}
