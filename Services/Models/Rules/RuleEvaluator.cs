namespace ManageComingSoon.Services.Rules
{
    using ManageComingSoon.Model.Rules;
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;

    public static class RuleEvaluator
    {
        public static bool Matches(JsonElement movie, RuleNode node)
        {
            if (node == null) return true;

            bool result = node.Kind == RuleNodeKind.Group
                ? EvaluateGroup(movie, node)
                : EvaluateCondition(movie, node);

            return node.Not ? !result : result;
        }

        private static bool EvaluateGroup(JsonElement movie, RuleNode group)
        {
            if (group.Children == null || group.Children.Count == 0)
                return true;

            return group.LogicOperator == RuleLogicOperator.And
                ? group.Children.All(c => Matches(movie, c))
                : group.Children.Any(c => Matches(movie, c));
        }

        private static bool EvaluateCondition(JsonElement movie, RuleNode condition)
        {
            var segments = (condition.Field ?? string.Empty).Split('.');
            if (segments.Length == 0 || segments[0].Length == 0) return false;

            if (!TryResolve(movie, segments, 0, out var resolved, out var remainingSegments))
                return false; // field path not present — fails closed

            // Resolution stopped at an array with leftover path segments:
            // project each array element through the remaining sub-path.
            if (resolved.ValueKind == JsonValueKind.Array && remainingSegments.Length > 0)
            {
                if (condition.Operator != RuleOperator.CONTAINS && condition.Operator != RuleOperator.NOTCONTAINS)
                    return false; // sub-property array projection only supports CONTAINS/NOTCONTAINS

                bool anyMatch = resolved.EnumerateArray().Any(item =>
                {
                    if (!TryResolve(item, remainingSegments, 0, out var subValue, out var subRemaining))
                        return false;
                    if (subRemaining.Length > 0) return false; // nested arrays-of-arrays not supported
                    return string.Equals(ScalarToString(subValue), condition.Value, StringComparison.OrdinalIgnoreCase);
                });

                return condition.Operator == RuleOperator.CONTAINS ? anyMatch : !anyMatch;
            }

            return Compare(resolved, condition.Operator, condition.Value);
        }

        // Walks segments against `current`. Stops early (returning the
        // remaining unconsumed segments) if it hits an array before the
        // path is fully consumed — arrays can't be indexed by a plain
        // dotted path, so the leftover segments are handed back for the
        // caller to project per-element instead.
        private static bool TryResolve(JsonElement current, string[] segments, int startIndex, out JsonElement result, out string[] remaining)
        {
            result = current;
            remaining = Array.Empty<string>();

            for (int i = startIndex; i < segments.Length; i++)
            {
                if (result.ValueKind == JsonValueKind.Array)
                {
                    remaining = segments.Skip(i).ToArray();
                    return true;
                }

                if (result.ValueKind != JsonValueKind.Object ||
                    !result.TryGetProperty(segments[i], out var next))
                {
                    result = default;
                    return false;
                }

                result = next;
            }

            return true;
        }

        private static bool Compare(JsonElement actual, RuleOperator op, string expected)
        {
            switch (actual.ValueKind)
            {
                case JsonValueKind.Array:
                    // Flat array of scalars (genres, etc.)
                    bool anyMatch = actual.EnumerateArray().Any(el =>
                        string.Equals(ScalarToString(el), expected, StringComparison.OrdinalIgnoreCase));
                    if (op == RuleOperator.CONTAINS) return anyMatch;
                    if (op == RuleOperator.NOTCONTAINS) return !anyMatch;
                    return false;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    bool actualBool = actual.GetBoolean();
                    bool.TryParse(expected, out var expectedBool);
                    if (op == RuleOperator.EQ) return actualBool == expectedBool;
                    if (op == RuleOperator.NEQ) return actualBool != expectedBool;
                    return false;

                case JsonValueKind.Number:
                    double actualNum = actual.GetDouble();
                    if (!double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNum))
                        return false;
                    return CompareOrdered(actualNum.CompareTo(expectedNum), op);

                case JsonValueKind.String:
                    if (DateTime.TryParse(actual.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var actualDate) &&
                        DateTime.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expectedDate) &&
                        (op == RuleOperator.LT || op == RuleOperator.LTE || op == RuleOperator.GT || op == RuleOperator.GTE))
                    {
                        return CompareOrdered(actualDate.CompareTo(expectedDate), op);
                    }

                    string actualStr = actual.GetString() ?? string.Empty;
                    switch (op)
                    {
                        case RuleOperator.EQ: return string.Equals(actualStr, expected, StringComparison.OrdinalIgnoreCase);
                        case RuleOperator.NEQ: return !string.Equals(actualStr, expected, StringComparison.OrdinalIgnoreCase);
                        case RuleOperator.CONTAINS: return actualStr.IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                        case RuleOperator.NOTCONTAINS: return actualStr.IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0;
                        default: return false;
                    }

                default:
                    return false;
            }
        }
        // Resolves a dotted field path to a human-readable display string
        // for a given movie — used by the preview table (not by matching
        // itself). Reuses the same TryResolve/ScalarToString machinery as
        // Compare() above so display values stay consistent with what
        // matching actually evaluated against.
        public static string ResolveDisplayValue(JsonElement movie, string field)
        {
            var segments = (field ?? string.Empty).Split('.');
            if (segments.Length == 0 || segments[0].Length == 0) return string.Empty;

            if (!TryResolve(movie, segments, 0, out var resolved, out var remaining))
                return string.Empty;

            if (resolved.ValueKind == JsonValueKind.Array)
            {
                if (remaining.Length > 0)
                {
                    var projected = resolved.EnumerateArray()
                        .Select(item =>
                        {
                            if (TryResolve(item, remaining, 0, out var sub, out var subRemaining) && subRemaining.Length == 0)
                                return ScalarToString(sub);
                            return null;
                        })
                        .Where(v => v != null);
                    return string.Join(", ", projected);
                }

                return string.Join(", ", resolved.EnumerateArray().Select(ScalarToString));
            }

            return ScalarToString(resolved);
        }
        private static string ScalarToString(JsonElement el) =>
            el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();

        private static bool CompareOrdered(int cmp, RuleOperator op)
        {
            switch (op)
            {
                case RuleOperator.LT: return cmp < 0;
                case RuleOperator.LTE: return cmp <= 0;
                case RuleOperator.GT: return cmp > 0;
                case RuleOperator.GTE: return cmp >= 0;
                case RuleOperator.EQ: return cmp == 0;
                case RuleOperator.NEQ: return cmp != 0;
                default: return false;
            }
        }
    }
}