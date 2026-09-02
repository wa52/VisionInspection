using System.Globalization;
using SpeakerVisionInspection.Models;

namespace SpeakerVisionInspection.Detection;

/// <summary>
/// 判断模块求值：兼容旧多规则 OR，同时支持单规则内 all/any 条件组合。
/// 任一旧规则命中 → 返回该规则 Result；单规则未命中 → ElseResult（默认 OK）。
/// 条件引用的节点/字段缺失或值非法 → 该条件按不命中处理并记录原因（不崩溃）。
/// </summary>
public static class DecisionEvaluator
{
    /// <summary>求值。nodeValues: 节点名 → 该节点输出值字典（NodeResult.Values）。</summary>
    public static string Evaluate(
        RecipeDecision decision,
        IReadOnlyDictionary<string, Dictionary<string, string>> nodeValues,
        Action<string>? log = null)
    {
        if (decision.Rules.Count == 0)
        {
            return "OK";
        }

        for (var i = 0; i < decision.Rules.Count; i++)
        {
            var rule = decision.Rules[i];
            if (rule.Conditions.Count == 0) continue;
            var isAny = string.Equals(rule.MatchMode, "any", StringComparison.OrdinalIgnoreCase);
            var ruleHit = !isAny;
            foreach (var cond in rule.Conditions)
            {
                if (!TryEvaluateCondition(cond, nodeValues, out var hit, out var reason))
                {
                    log?.Invoke($"[判断] 条件未命中(缺字段/非法): {cond.Node}.{cond.Field} {cond.Op} {cond.Value} — {reason}");
                    hit = false;
                }
                if (isAny && hit)
                {
                    ruleHit = true;
                    break;
                }
                if (!isAny && !hit)
                {
                    ruleHit = false;
                    break;
                }
            }
            if (ruleHit)
            {
                return string.IsNullOrWhiteSpace(rule.Result) ? "NG" : rule.Result;
            }

            // 新 UI 的条件检测节点是单规则：未命中时直接输出 ElseResult。
            // 旧 recipe 可能有多条规则，仍保持“规则之间 OR，全部未命中 OK”的兼容行为。
            if (decision.Rules.Count == 1 && !string.IsNullOrWhiteSpace(rule.ElseResult))
            {
                return rule.ElseResult;
            }
        }

        return "OK";
    }

    private static bool TryEvaluateCondition(
        Condition cond,
        IReadOnlyDictionary<string, Dictionary<string, string>> nodeValues,
        out bool hit,
        out string? reason)
    {
        hit = false;
        reason = null;

        if (!nodeValues.TryGetValue(cond.Node, out var values))
        {
            reason = $"节点不存在: {cond.Node}";
            return false;
        }
        if (!values.TryGetValue(cond.Field, out var raw))
        {
            reason = $"字段不存在: {cond.Node}.{cond.Field}";
            return false;
        }

        // 条件值支持引用同节点其他字段：@field（如 "@threshold" 表示用该节点输出的 threshold 字段）
        var rhsRaw = cond.Value;
        if (rhsRaw.StartsWith('@'))
        {
            var fieldRef = rhsRaw[1..];
            if (!values.TryGetValue(fieldRef, out var fieldVal))
            {
                reason = $"字段不存在(引用): {cond.Node}.{fieldRef}";
                return false;
            }
            rhsRaw = fieldVal;
        }

        // 数值比较：字段与值都能解析为 double 才做数值比较；否则回退字符串比较
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lhs)
            && double.TryParse(rhsRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rhs))
        {
            hit = cond.Op switch
            {
                ">" => lhs > rhs,
                ">=" => lhs >= rhs,
                "<" => lhs < rhs,
                "<=" => lhs <= rhs,
                "=" or "==" => lhs == rhs,
                "!=" => lhs != rhs,
                _ => false,
            };
            return true;
        }

        // 字符串比较
        hit = cond.Op switch
        {
            "=" or "==" => string.Equals(raw.Trim(), rhsRaw.Trim(), StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(raw.Trim(), rhsRaw.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!hit && cond.Op is not "=" and not "==" and not "!=")
        {
            reason = $"字段值不是数值: {raw}";
            return false;
        }
        return true;
    }
}
