using VisionInspection;
using Xunit;

namespace VisionInspection.Tests;

public class RecipeEditHistoryTests
{
    [Fact]
    public void Reset_ClearsStacks_AndBaselines()
    {
        var h = new RecipeEditHistory();
        h.Reset("A");
        h.Push("B");
        h.Reset("C");
        Assert.Equal(0, h.UndoCount);
        Assert.Equal(0, h.RedoCount);
        Assert.False(h.Push("C")); // 与基线相同：去重
    }

    [Fact]
    public void Push_SameState_Skips()
    {
        var h = new RecipeEditHistory();
        h.Reset("A");
        Assert.False(h.Push("A"));
        Assert.Equal(0, h.UndoCount);
    }

    [Fact]
    public void Undo_Redo_RoundTrip()
    {
        var h = new RecipeEditHistory();
        h.Reset("A");
        Assert.True(h.Push("B"));
        Assert.Equal(1, h.UndoCount);

        // 撤销：弹出 A，当前 B 进重做栈
        Assert.True(h.TryUndo("B", out var snap));
        Assert.Equal("A", snap);
        Assert.Equal(1, h.RedoCount);
        Assert.Equal(0, h.UndoCount);

        // 重做：弹出 B，当前 A 回撤销栈
        Assert.True(h.TryRedo("A", out var redoSnap));
        Assert.Equal("B", redoSnap);
        Assert.Equal(1, h.UndoCount);
        Assert.Equal(0, h.RedoCount);
    }

    [Fact]
    public void TryUndo_EmptyStack_ReturnsFalse()
    {
        var h = new RecipeEditHistory();
        h.Reset("A");
        Assert.False(h.TryUndo("A", out var snap));
        Assert.Equal("", snap);
        Assert.False(h.TryRedo("A", out _));
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var h = new RecipeEditHistory();
        h.Reset("A");
        h.Push("B");
        h.TryUndo("B", out _);
        Assert.Equal(1, h.RedoCount);
        h.Push("C"); // 新撤销点 → 重做栈清空
        Assert.Equal(0, h.RedoCount);
        Assert.Equal(1, h.UndoCount);
        Assert.True(h.TryUndo("C", out var snap));
        Assert.Equal("B", snap);
    }

    [Fact]
    public void Push_TrimsToMaxSteps_DroppingOldest()
    {
        var h = new RecipeEditHistory();
        h.Reset("S0");
        for (var i = 1; i <= RecipeEditHistory.MaxSteps + 1; i++)
        {
            h.Push($"S{i}");
        }
        Assert.Equal(RecipeEditHistory.MaxSteps, h.UndoCount);
        // 最旧的 S0 已被丢弃：连续撤销到底，最后一个快照是 S1
        var last = "";
        while (h.TryUndo($"S{h.UndoCount + 1}", out var snap))
        {
            last = snap;
        }
        Assert.Equal("S1", last);
    }

    [Fact]
    public void Rebaseline_AfterRestore_PushSameStateSkips()
    {
        var h = new RecipeEditHistory();
        h.Reset("A");
        h.Push("B");
        h.TryUndo("B", out var restored);
        Assert.Equal("A", restored);
        h.Rebaseline("A2"); // 恢复后的实际状态
        Assert.False(h.Push("A2"));
        Assert.True(h.Push("B2"));
        Assert.Equal(1, h.UndoCount);
    }
}
