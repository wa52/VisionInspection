namespace VisionInspection;

/// <summary>
/// 方案编辑历史（撤销/重做）：Recipe JSON 快照栈。纯逻辑、无 UI 依赖（调用方负责快照/恢复与状态刷新）。
/// 惰性捕获：基线快照是「上一个快照点」的状态，即使 Params 已被写入也能取到变更前状态。
/// </summary>
public sealed class RecipeEditHistory
{
    /// <summary>撤销/重做栈深度上限（超出丢最旧）。</summary>
    public const int MaxSteps = 50;

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _lastSnapshot = "";

    /// <summary>可撤销步数。</summary>
    public int UndoCount => _undo.Count;

    /// <summary>可恢复步数。</summary>
    public int RedoCount => _redo.Count;

    /// <summary>重置历史（新建/打开方案后调用）：建立基线快照，清空撤销/重做栈。</summary>
    public void Reset(string baselineJson)
    {
        _undo.Clear();
        _redo.Clear();
        _lastSnapshot = baselineJson ?? "";
    }

    /// <summary>
    /// 变更入口调用：把「上一个快照点」压入撤销栈；状态未变返回 false（去重）；任何新撤销点清空重做栈。
    /// </summary>
    public bool Push(string currentJson)
    {
        if (currentJson == _lastSnapshot) return false;
        _undo.Push(_lastSnapshot);
        while (_undo.Count > MaxSteps)
        {
            var keep = _undo.Take(MaxSteps).ToArray(); // Stack 枚举顺序=栈顶→栈底
            _undo.Clear();
            for (var i = keep.Length - 1; i >= 0; i--) _undo.Push(keep[i]);
        }
        _redo.Clear();
        _lastSnapshot = currentJson;
        return true;
    }

    /// <summary>撤销：弹出上一个快照点，把当前状态压入重做栈。空栈返回 false。</summary>
    public bool TryUndo(string currentJson, out string snapshot)
    {
        if (_undo.Count == 0)
        {
            snapshot = "";
            return false;
        }
        snapshot = _undo.Pop();
        _redo.Push(currentJson);
        return true;
    }

    /// <summary>重做：弹出重做栈快照，把当前状态压回撤销栈。空栈返回 false。</summary>
    public bool TryRedo(string currentJson, out string snapshot)
    {
        if (_redo.Count == 0)
        {
            snapshot = "";
            return false;
        }
        snapshot = _redo.Pop();
        _undo.Push(currentJson);
        return true;
    }

    /// <summary>快照恢复完成后重新基线化（把恢复后的状态作为新「上一个快照点」）。</summary>
    public void Rebaseline(string currentJson) => _lastSnapshot = currentJson ?? "";
}
