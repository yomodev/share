using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NxtUI.Core.Filtering;

/// <summary>
/// Evaluates a <see cref="FilterNode"/> AST against a plain C# object using
/// case-insensitive reflection for field lookup.  Mirrors the JS evaluate()
/// function in filter.js so in-memory C# filtering behaves identically to
/// client-side JS filtering.
/// </summary>
public static class FilterEvaluator
{
    public static bool Evaluate(FilterNode? node, object obj) => node switch
    {
        null => true,
        AndNode and => Evaluate(and.Left, obj) && Evaluate(and.Right, obj),
        OrNode or => Evaluate(or.Left, obj) || Evaluate(or.Right, obj),
        NotNode not => !Evaluate(not.Operand, obj),
        FieldTermNode t => EvalField(t, obj),
        _ => true,
    };

    private static bool EvalField(FieldTermNode t, object obj)
    {
        var raw = GetProperty(obj, t.Field);

        if (t.MatchType == MatchType.IsNull)
            return raw is null || (raw is string s && s.Length == 0);

        if (raw is null) return false;

        return (t.MatchType, t.Value) switch
        {
            (MatchType.Contains, StringValue sv) =>
                (raw.ToString() ?? "").Contains(sv.Value,
                    t.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),

            (MatchType.Contains, NumberValue nv) =>
                (raw.ToString() ?? "").Contains(nv.Value.ToString(CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase),

            (MatchType.Exact, StringValue sv) =>
                string.Equals(raw.ToString(), sv.Value,
                    t.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),

            // A bare number with no comparison operator (e.g. "thread:5") means exact equality.
            (MatchType.Exact, NumberValue nv) => ToDouble(raw) == nv.Value,

            // Booleans only ever parse to Exact, but ParseBareTerm's global-term
            // expansion can produce Contains for a bare "true"/"false" — both mean
            // the same thing for a boolean value, so both map to equality.
            (MatchType.Exact, BoolValue bv) => ToBool(raw) == bv.Value,
            (MatchType.Contains, BoolValue bv) => ToBool(raw) == bv.Value,

            (MatchType.Glob, StringValue sv) =>
                GlobMatch(raw.ToString() ?? "", sv.Value, t.CaseSensitive),

            (MatchType.GreaterThan, NumberValue nv) => ToDouble(raw) > nv.Value,
            (MatchType.GreaterThanOrEqual, NumberValue nv) => ToDouble(raw) >= nv.Value,
            (MatchType.LessThan, NumberValue nv) => ToDouble(raw) < nv.Value,
            (MatchType.LessThanOrEqual, NumberValue nv) => ToDouble(raw) <= nv.Value,

            (MatchType.GreaterThan, DateValue dv) => ToDateTime(raw) > dv.Value,
            (MatchType.GreaterThanOrEqual, DateValue dv) => ToDateTime(raw) >= dv.Value,
            (MatchType.LessThan, DateValue dv) => ToDateTime(raw) < dv.Value,
            (MatchType.LessThanOrEqual, DateValue dv) => ToDateTime(raw) <= dv.Value,

            (MatchType.GreaterThan, TimeOfDayValue tv) => ToTimeSeconds(raw) > tv.Seconds,
            (MatchType.GreaterThanOrEqual, TimeOfDayValue tv) => ToTimeSeconds(raw) >= tv.Seconds,
            (MatchType.LessThan, TimeOfDayValue tv) => ToTimeSeconds(raw) < tv.Seconds,
            (MatchType.LessThanOrEqual, TimeOfDayValue tv) => ToTimeSeconds(raw) <= tv.Seconds,

            (MatchType.Between, RangeValue rv) => EvalBetween(raw, rv),

            _ => false,
        };
    }

    private static bool EvalBetween(object raw, RangeValue rv) => (rv.Low, rv.High) switch
    {
        (NumberValue ln, NumberValue hn) => ToDouble(raw) >= ln.Value && ToDouble(raw) <= hn.Value,
        (DateValue ld, DateValue hd) => ToDateTime(raw) >= ld.Value && ToDateTime(raw) <= hd.Value,
        (TimeOfDayValue lt, TimeOfDayValue ht) => ToTimeSeconds(raw) >= lt.Seconds && ToTimeSeconds(raw) <= ht.Seconds,
        _ => false,
    };

    private static bool GlobMatch(string input, string pattern, bool caseSensitive)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var ch in pattern)
            sb.Append(ch switch { '*' => ".*", '?' => ".", _ => Regex.Escape(ch.ToString()) });
        sb.Append('$');
        return Regex.IsMatch(input, sb.ToString(),
            caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    // FilterParser's canonical field names (e.g. "StartTime") mirror SQL/Mongo column
    // names, which don't always match the in-memory model's property name
    // (RunSummary.Start, not RunSummary.StartTime). Try the canonical name first,
    // then these synonyms, before giving up.
    private static readonly Dictionary<string, string> PropertySynonyms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["StartTime"] = "Start",
            ["EndTime"] = "End",
        };

    private static object? GetProperty(object obj, string field)
    {
        var direct = obj.GetType()
            .GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(obj);

        if (direct is not null) return direct;

        if (PropertySynonyms.TryGetValue(field, out var synonym))
        {
            var viaSynonym = obj.GetType()
                .GetProperty(synonym, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?.GetValue(obj);
            if (viaSynonym is not null) return viaSynonym;
        }

        // Fallback: if the object has a JsonPayload string, search inside it.
        // This lets payload field filters (e.g. orderId:>0) work on deserialized proto messages.
        var jsonPayload = obj.GetType()
            .GetProperty("JsonPayload", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(obj) as string;

        if (string.IsNullOrEmpty(jsonPayload)) return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            if (doc.RootElement.TryGetProperty(field, out var elem))
                return ExtractJsonValue(elem);

            // Also try camelCase variant
            var camel = char.ToLowerInvariant(field[0]) + field[1..];
            if (doc.RootElement.TryGetProperty(camel, out var camelElem))
                return ExtractJsonValue(camelElem);
        }
        catch { }

        return null;
    }

    private static object? ExtractJsonValue(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.Number when elem.TryGetDouble(out var d) => d,
        JsonValueKind.String => elem.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => elem.GetRawText(),
    };

    private static bool ToBool(object? v) => v switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => bool.TryParse(s, out var b2) ? b2 : s == "1",
        _ => false,
    };

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        TimeSpan t => t.TotalSeconds,
        _ => double.TryParse(v?.ToString(), out var r) ? r : 0,
    };

    private static DateTime ToDateTime(object? v) => v switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        _ => DateTime.TryParse(v?.ToString(), out var r) ? r : default,
    };

    private static int ToTimeSeconds(object? v)
    {
        var dt = ToDateTime(v);
        if (dt == default)
        {
            // Try to extract hh:mm:ss from a string like "2024-01-15 19:06:24"
            var s = v?.ToString() ?? "";
            var m = Regex.Match(s, @"(\d{1,2}):(\d{2})(?::(\d{2}))?");
            if (!m.Success) return -1;
            return int.Parse(m.Groups[1].Value) * 3600
                 + int.Parse(m.Groups[2].Value) * 60
                 + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }
        return (int)dt.TimeOfDay.TotalSeconds;
    }
}
