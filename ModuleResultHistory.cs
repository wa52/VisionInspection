namespace SpeakerVisionInspection;

/// <summary>单个模块（节点）一次执行的运行记录（海康式：执行序号 + 时间 + 模块数据）。</summary>
public sealed record ModuleRunRecord(int Seq, DateTime Time, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>模块数据摘要（k=v 空格连接，供「历史结果」表格一列显示）。</summary>
    public string Summary => Values.Count == 0 ? "（无输出值）" : string.Join("  ", Values.Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>
/// 模块结果历史（海康式「历史结果」）：节点名 → 最近 N 次执行记录（环形上限，超出丢最旧）。
/// 纯逻辑、无 UI 依赖；调用方（MainWindow）需在 UI 线程使用（不加锁）。
/// </summary>
public sealed class ModuleResultHistory
{
    /// <summary>每节点保留的最大记录数。</summary>
    public const int MaxRecords = 100;

    private readonly Dictionary<string, List<ModuleRunRecord>> _byNode = new(StringComparer.OrdinalIgnoreCase);
    private int _seq;

    /// <summary>全局执行序号（跨方案/跨节点递增，语义对齐 VM 的执行序号：只增不复位）。</summary>
    public int LastSeq => _seq;

    /// <summary>记录一次节点执行；values 会被拷贝快照，调用方后续修改不影响已存记录。</summary>
    public ModuleRunRecord Record(string nodeName, IReadOnlyDictionary<string, string> values, DateTime? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        var record = new ModuleRunRecord(++_seq, time ?? DateTime.Now, new Dictionary<string, string>(values));
        if (!_byNode.TryGetValue(nodeName, out var list))
        {
            list = new List<ModuleRunRecord>();
            _byNode[nodeName] = list;
        }
        list.Add(record);
        while (list.Count > MaxRecords)
        {
            list.RemoveAt(0);
        }
        return record;
    }

    /// <summary>某节点的全部历史（旧→新；无记录返回空表）。</summary>
    public IReadOnlyList<ModuleRunRecord> GetHistory(string nodeName) =>
        _byNode.TryGetValue(nodeName, out var list) ? list.ToArray() : Array.Empty<ModuleRunRecord>();

    /// <summary>某节点最近一次执行记录（无则 null）。</summary>
    public ModuleRunRecord? GetLatest(string nodeName)
    {
        var list = _byNode.GetValueOrDefault(nodeName);
        return list is { Count: > 0 } ? list[^1] : null;
    }

    /// <summary>节点重命名后迁移历史（新名已有历史则按执行序号合并，保持时间顺序）。</summary>
    public void RenameNode(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;
        var old = _byNode.GetValueOrDefault(oldName);
        if (old is null || old.Count == 0) return;
        _byNode.Remove(oldName);
        if (_byNode.TryGetValue(newName, out var existing))
        {
            existing.AddRange(old);
            existing.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            while (existing.Count > MaxRecords)
            {
                existing.RemoveAt(0);
            }
        }
        else
        {
            _byNode[newName] = old;
        }
    }

    /// <summary>清空全部历史（新建/打开方案后）；执行序号保持递增不复位。</summary>
    public void Clear() => _byNode.Clear();
}
