namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 检测项（ROI）名 ↔ 模型类名匹配：检测项名字能对上哪个模型类，该项就只判哪个类；对不上则按全部非背景类别判定。
/// 忽略空格/制表符（Halcon 等转换工具生成的类名常带空格，如「泡棉 1」；手填/改名时容易多写或少写）。
/// </summary>
internal static class PositiveClasses
{
    public static string Normalize(string s) =>
        (s ?? "").Replace(" ", "").Replace("\u3000", "").Replace("\t", "");

    /// <summary>
    /// 检测项名匹配的模型类别集合（忽略空格/大小写）；空名或未命中返回空表（语义 = 按全部非背景类别判定）。
    /// </summary>
    public static IReadOnlyList<string> MatchClassesForRoi(string roiName, IReadOnlyList<string> classNames)
    {
        var target = Normalize(roiName);
        if (target.Length == 0 || classNames.Count == 0)
        {
            return Array.Empty<string>();
        }
        return classNames
            .Where(c => string.Equals(Normalize(c), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>cls 是否算该检测项的缺陷：roiClasses 空 = 全部类别；否则命中（忽略空格/大小写）才计数。</summary>
    public static bool MatchesRoi(IReadOnlyList<string> roiClasses, string cls)
    {
        if (roiClasses.Count == 0) return true;
        var normalized = Normalize(cls);
        foreach (var c in roiClasses)
        {
            if (string.Equals(Normalize(c), normalized, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
