using OpenCvSharp;
using VisionInspection.Detection;
using VisionInspection.Models;
using Xunit;

namespace VisionInspection.Tests;

public class ModuleResultHistoryTests
{
    [Fact]
    public void Record_SnapshotsValues_IncrementsSeq()
    {
        var history = new ModuleResultHistory();
        var values = new Dictionary<string, string> { ["score"] = "0.9", ["decision"] = "OK" };
        var time = new DateTime(2026, 9, 4, 9, 0, 0);

        var r1 = history.Record("01 轮廓匹配", values, time);
        values["score"] = "0.1"; // 调用方后续修改不影响已存快照
        var r2 = history.Record("01 轮廓匹配", values);

        Assert.Equal(1, r1.Seq);
        Assert.Equal(time, r1.Time);
        Assert.Equal("0.9", r1.Values["score"]);
        Assert.Equal(2, r2.Seq);
        Assert.Equal("0.1", r2.Values["score"]);
        Assert.Equal(2, history.GetHistory("01 轮廓匹配").Count);
        Assert.Same(r2, history.GetLatest("01 轮廓匹配"));
    }

    [Fact]
    public void Record_BeyondCap_DropsOldest()
    {
        var history = new ModuleResultHistory();
        for (var i = 0; i < ModuleResultHistory.MaxRecords + 5; i++)
        {
            history.Record("节点", new Dictionary<string, string> { ["i"] = i.ToString() });
        }

        var list = history.GetHistory("节点");
        Assert.Equal(ModuleResultHistory.MaxRecords, list.Count);
        Assert.Equal("5", list[0].Values["i"]); // 最旧 5 条被丢弃
        Assert.Equal((ModuleResultHistory.MaxRecords + 4).ToString(), history.GetLatest("节点")!.Values["i"]); // 最后记录 i=104
    }

    [Fact]
    public void Record_CaseInsensitiveNodeName_SharesHistory()
    {
        var history = new ModuleResultHistory();
        history.Record("Abc", new Dictionary<string, string> { ["a"] = "1" });
        history.Record("aBc", new Dictionary<string, string> { ["a"] = "2" });

        Assert.Equal(2, history.GetHistory("ABC").Count);
    }

    [Fact]
    public void RenameNode_MovesHistory_MergesWithExisting()
    {
        var history = new ModuleResultHistory();
        history.Record("旧名", new Dictionary<string, string> { ["v"] = "1" });
        history.RenameNode("旧名", "新名");

        Assert.Empty(history.GetHistory("旧名"));
        Assert.Equal("1", history.GetLatest("新名")!.Values["v"]);

        history.Record("新名", new Dictionary<string, string> { ["v"] = "2" });
        history.RenameNode("其他", "新名"); // 与其他名合并：旧记录排前
        history.Record("其他", new Dictionary<string, string> { ["v"] = "3" });
        history.RenameNode("其他", "新名");

        var merged = history.GetHistory("新名");
        Assert.Equal(3, merged.Count);
        Assert.Equal("1", merged[0].Values["v"]);
        Assert.Equal("3", merged[^1].Values["v"]);
    }

    [Fact]
    public void Clear_EmitsNothing_ButSeqKeepsIncreasing()
    {
        var history = new ModuleResultHistory();
        var first = history.Record("A", new Dictionary<string, string>());
        history.Clear();

        Assert.Empty(history.GetHistory("A"));
        var next = history.Record("B", new Dictionary<string, string>());
        Assert.True(next.Seq > first.Seq); // 序号只增不复位（对齐 VM 执行序号语义）
    }

    [Fact]
    public void Summary_JoinsKeyValues()
    {
        var record = new ModuleRunRecord(1, DateTime.Now, new Dictionary<string, string> { ["模块状态"] = "1", ["分数"] = "0.9" });
        Assert.Equal("模块状态=1  分数=0.9", record.Summary);
        Assert.Equal("（无输出值）", new ModuleRunRecord(2, DateTime.Now, new Dictionary<string, string>()).Summary);
    }

    [Fact]
    public void DisplayName_TranslatesKnownAndPrefixedKeys()
    {
        Assert.Equal("判定", ModuleResultView.DisplayName("decision"));
        Assert.Equal("耗时(ms)", ModuleResultView.DisplayName("elapsed_ms"));
        Assert.Equal("最高置信度", ModuleResultView.DisplayName("max_conf"));
        Assert.Equal("检测项 划痕", ModuleResultView.DisplayName("roi_划痕"));
        Assert.Equal("匹配2·X", ModuleResultView.DisplayName("match_2_x"));
        Assert.Equal("匹配3·分数", ModuleResultView.DisplayName("match_3_score"));
        Assert.Equal("unknown_key", ModuleResultView.DisplayName("unknown_key"));
    }

    [Fact]
    public void Pipeline_EmitsElapsedMsPerNode()
    {
        var recipe = new Recipe
        {
            Nodes =
            [
                new RecipeNode { Name = "01 二值化", Type = "Binarize", Enabled = true },
            ],
        };
        using var pipeline = new Pipeline(recipe);
        using var img = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(128));
        var result = pipeline.Run(img, "t");

        Assert.True(result.NodeValues.TryGetValue("01 二值化", out var vals));
        Assert.True(double.TryParse(vals["elapsed_ms"], out var ms));
        Assert.True(ms >= 0);
        foreach (var m in result.NodeImages.Values) m.Dispose();
    }
}
