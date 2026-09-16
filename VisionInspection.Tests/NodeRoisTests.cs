using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;

namespace VisionInspection.Tests;

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
    public void CreateMask_RotatedCornersAreExcluded()
    {
        using var mask = NodeRois.CreateMask(new RoiRect(0.5, 0.5, 0.5, 0.25, 45), 200, 200);

        Assert.Equal(255, mask.At<byte>(100, 100));
        Assert.Equal(0, mask.At<byte>(35, 35));
        Assert.Equal(0, mask.At<byte>(165, 35));
    }

    [Fact]
    public void IntersectsPixelBox_UsesRotatedGeometry()
    {
        var roi = new RoiRect(0.5, 0.5, 0.5, 0.25, 45);

        Assert.True(NodeRois.IntersectsPixelBox(roi, 95, 95, 10, 10, 200, 200));
        Assert.False(NodeRois.IntersectsPixelBox(roi, 20, 20, 10, 10, 200, 200));
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

    [Fact]
    public void ParseOwnFull_TargetField_RoundTrip_AndEscaping()
    {
        // 目标字符（x=）往返：普通文本 + 含分隔符的文本（, ; | =）URL 转义后无损
        var raw = NodeRois.SerializeOwnFull(new List<RoiItem>
        {
            new("SN", new RoiRect(0.5, 0.5, 0.3, 0.2, 0), new RoiMeta(Target: "A0")),
            new("含分隔", new RoiRect(0.3, 0.3, 0.2, 0.2, 0), new RoiMeta(Target: "a,b;c|d=e")),
        });
        var parsed = NodeRois.ParseOwnFull(raw);
        Assert.Equal("A0", parsed[0].Meta.Target);
        Assert.Equal("a,b;c|d=e", parsed[1].Meta.Target); // 转义往返无损
        Assert.Equal(raw, NodeRois.SerializeOwnFull(parsed)); // 序列化稳定

        // 全默认（无目标字符）不写 x=，保持旧格式干净；旧格式缺省 → Target 空
        var plain = NodeRois.SerializeOwnFull(new List<RoiItem> { new("A", new RoiRect(0.5, 0.5, 0.2, 0.2, 0), new RoiMeta()) });
        Assert.DoesNotContain("x=", plain);
        Assert.Equal("", NodeRois.ParseOwnFull("A;0.5,0.5,0.2,0.2,0")[0].Meta.Target);
        Assert.True(NodeRois.ParseOwnFull(plain)[0].Meta.IsDefault);
    }

    // ===== 圆环集（own_rings，圆查找节点专用） =====

    [Fact]
    public void Rings_ParseSerialize_RoundTrip()
    {
        var rings = new List<(string, RoiRing)>
        {
            ("标称环", new RoiRing(0.5, 0.4, 0.28, 0.22)),
            ("圆环1_2", new RoiRing(0.7, 0.6, 0.15, 0.1)),
        };
        var raw = NodeRings.Serialize(rings);
        Assert.Equal("标称环;0.5,0.4,0.28,0.22|圆环1_2;0.7,0.6,0.15,0.1", raw);

        var parsed = NodeRings.Parse(raw);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("标称环", parsed[0].Name);
        Assert.Equal(0.5, parsed[0].Ring.CenterX, 4);
        Assert.Equal(0.22, parsed[0].Ring.RInner, 4);
        Assert.Equal("圆环1_2", parsed[1].Name);
        Assert.Equal(0.15, parsed[1].Ring.ROuter, 4);

        // 空参数 → 空；非法条目跳过（ri≥ro / 格式错）
        Assert.Empty(NodeRings.Parse(null));
        Assert.Empty(NodeRings.Parse(""));
        Assert.Empty(NodeRings.Parse("A;0.5,0.5,0.2,0.25|B;残缺|C;0.5,0.5"));
        Assert.Single(NodeRings.Parse("A;0.5,0.5,0.2,0.1|B;0.5,0.5,0.2,0.25"));
    }

    [Fact]
    public void Rings_ToPixels_RadiusByImageWidth()
    {
        // 半径按图像宽换算（各向同性），圆心分别按宽/高
        var (cx, cy, ro, ri) = new RoiRing(0.5, 0.5, 0.25, 0.1).ToPixels(800, 600);
        Assert.Equal(400, cx, 4);
        Assert.Equal(300, cy, 4);
        Assert.Equal(200, ro, 4);
        Assert.Equal(80, ri, 4);

        // 环宽钳制 ≥4px：ri 贴到 ro−4
        var (_, _, ro2, ri2) = new RoiRing(0.5, 0.5, 0.1, 0.099).ToPixels(800, 600);
        Assert.Equal(80, ro2, 4);
        Assert.Equal(76, ri2, 4);
    }

    [Fact]
    public void Rings_ApplyPoseCorrection_MovesCenter()
    {
        // 目标节点命中：基准位姿 (100,100,0) → 当前 (150,110,0)（纯平移）→ 圆心平移、半径不变
        var rings = new List<(string Name, RoiRing Ring)> { ("环1", new RoiRing(0.25, 0.25, 100.0 / 800, 60.0 / 800)) };
        var correction = new PoseCorrection("定位", ["圆查找1"], 100, 100, 0, 150, 110, 0);
        var mapped = NodeRings.ApplyPoseCorrection(rings, "圆查找1", 800, 600, correction);
        var (cx, cy, ro, ri) = mapped[0].Ring.ToPixels(800, 600);
        Assert.Equal(250, cx, 4);
        Assert.Equal(160, cy, 4);
        Assert.Equal(100, ro, 4);
        Assert.Equal(60, ri, 4);

        // 非目标节点 → 原样返回
        var untouched = NodeRings.ApplyPoseCorrection(rings, "别的节点", 800, 600, correction);
        Assert.Equal(rings[0].Ring, untouched[0].Ring);
    }
}
