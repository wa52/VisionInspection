namespace SpeakerVisionInspection.Models;

public sealed record DetectionResult(
    string Image,
    double Score,
    double? Threshold,
    string Decision, // OK | NG | ERROR
    string ProcessedAt,
    string? Error = null)
{
    /// <summary>每节点输出明细（节点名 → 值字典），UI 展示多模型 score 用。</summary>
    public Dictionary<string, Dictionary<string, string>> NodeDetails { get; init; } = new();

    public string ToPlain() => Decision;

    public string ToCsv(string sep = ",")
    {
        var th = Threshold is null ? "" : Threshold.Value.ToString("F4");
        return $"{Decision}{sep}{Score:F4}{sep}{th}";
    }

    public string ToJson()
    {
        return $"{{\"decision\":\"{Decision}\",\"score\":{Score:F4},\"threshold\":{Threshold?.ToString("F4") ?? "null"}}}";
    }

    public string FormatForVm(string format, string sep = ",") => format.ToLowerInvariant() switch
    {
        "json" => ToJson(),
        "csv" => ToCsv(sep),
        _ => ToPlain(), // plain = 只发 OK/NG 一个词（最稳，VM 字符比较）
    };
}
