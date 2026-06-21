using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BatchMonitor.Filtering;

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
        null            => true,
        AndNode and     => Evaluate(and.Left, obj) && Evaluate(and.Right, obj),
        OrNode or       => Evaluate(or.Left, obj) || Evaluate(or.Right, obj),
        NotNode not     => !Evaluate(not.Operand, obj),
        FieldTermNode t => EvalField(t, obj),
        _               => true,
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

            (MatchType.Glob, StringValue sv) =>
                GlobMatch(raw.ToString() ?? "", sv.Value, t.CaseSensitive),

            (MatchType.GreaterThan,        NumberValue nv) => ToDouble(raw) >  nv.Value,
            (MatchType.GreaterThanOrEqual, NumberValue nv) => ToDouble(raw) >= nv.Value,
            (MatchType.LessThan,           NumberValue nv) => ToDouble(raw) <  nv.Value,
            (MatchType.LessThanOrEqual,    NumberValue nv) => ToDouble(raw) <= nv.Value,

            (MatchType.GreaterThan,        DateValue dv) => ToDateTime(raw) >  dv.Value,
            (MatchType.GreaterThanOrEqual, DateValue dv) => ToDateTime(raw) >= dv.Value,
            (MatchType.LessThan,           DateValue dv) => ToDateTime(raw) <  dv.Value,
            (MatchType.LessThanOrEqual,    DateValue dv) => ToDateTime(raw) <= dv.Value,

            (MatchType.Between, RangeValue rv) => EvalBetween(raw, rv),

            _ => false,
        };
    }

    private static bool EvalBetween(object raw, RangeValue rv) => (rv.Low, rv.High) switch
    {
        (NumberValue ln, NumberValue hn) => ToDouble(raw)   >= ln.Value && ToDouble(raw)   <= hn.Value,
        (DateValue   ld, DateValue   hd) => ToDateTime(raw) >= ld.Value && ToDateTime(raw) <= hd.Value,
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

    private static object? GetProperty(object obj, string field) =>
        obj.GetType()
           .GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
           ?.GetValue(obj);

    private static double ToDouble(object? v) => v switch
    {
        double d   => d,
        float  f   => f,
        int    i   => i,
        long   l   => l,
        TimeSpan t => t.TotalSeconds,
        _          => double.TryParse(v?.ToString(), out var r) ? r : 0,
    };

    private static DateTime ToDateTime(object? v) => v switch
    {
        DateTime        dt  => dt,
        DateTimeOffset  dto => dto.UtcDateTime,
        _                   => DateTime.TryParse(v?.ToString(), out var r) ? r : default,
    };
}
