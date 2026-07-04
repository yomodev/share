using System.Text;
using Microsoft.Data.SqlClient;

namespace NxtUI.Filtering;

/// <summary>
/// Translates a <see cref="FilterNode"/> AST into a parameterised SQL Server WHERE fragment.
/// Mirrors <see cref="MongoFilterBuilder"/> — same AST, different backend.
/// </summary>
public static class SqlFilterBuilder
{
    // Maps canonical field names (post alias-resolution) to qualified SQL column references.
    // RequestId is a real int column in the RunsTable — it's filter/sort-only, never
    // selected or shown in the UI grid (Description is the human-readable column there).
    private static readonly Dictionary<string, string> ColumnMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RunId"]       = "r.RunId",
            ["RequestId"]   = "r.RequestId",
            ["Status"]      = "r.Status",
            ["Type"]        = "r.Type",
            ["Description"] = "r.Description",
            ["StartTime"]   = "r.StartTime",
            ["EndTime"]     = "r.EndTime",
        };

    /// <summary>
    /// Resolves a canonical field name to its qualified SQL column, for use in contexts
    /// (like ORDER BY) that need an allow-listed column rather than a WHERE fragment.
    /// Returns false for anything not in <see cref="ColumnMap"/> — never interpolate
    /// unvalidated field names into SQL.
    /// </summary>
    public static bool TryGetColumn(string? field, out string column)
    {
        column = "";
        return field is not null && ColumnMap.TryGetValue(field, out column!);
    }

    /// <summary>
    /// Converts <paramref name="node"/> to a SQL WHERE fragment (no leading "WHERE").
    /// Returns an empty string and empty list for a null node.
    /// </summary>
    public static (string Sql, List<SqlParameter> Parameters) Build(FilterNode? node)
    {
        var ctx = new BuildContext();
        var sql = BuildNode(node, ctx);
        return (sql, ctx.Parameters);
    }

    /// <summary>
    /// True if the AST contains a term against <paramref name="field"/> (case-insensitive).
    /// Used to detect an explicit date-range filter so callers can skip an implicit default window.
    /// </summary>
    public static bool ReferencesField(FilterNode? node, string field) =>
        node switch
        {
            null                                                      => false,
            AndNode and                                                => ReferencesField(and.Left, field) || ReferencesField(and.Right, field),
            OrNode or                                                  => ReferencesField(or.Left, field)  || ReferencesField(or.Right, field),
            NotNode not                                                => ReferencesField(not.Operand, field),
            FieldTermNode t when t.Field.Equals(field, StringComparison.OrdinalIgnoreCase) => true,
            _                                                          => false,
        };

    private static string BuildNode(FilterNode? node, BuildContext ctx) =>
        node switch
        {
            null            => "",
            AndNode and     => Combine("AND", BuildNode(and.Left, ctx), BuildNode(and.Right, ctx)),
            OrNode or       => Combine("OR",  BuildNode(or.Left,  ctx), BuildNode(or.Right,  ctx)),
            NotNode not     => NotWrap(BuildNode(not.Operand, ctx)),
            FieldTermNode t => BuildTerm(t, ctx),
            _               => "",
        };

    private static string Combine(string op, string left, string right)
    {
        if (string.IsNullOrEmpty(left))  return right;
        if (string.IsNullOrEmpty(right)) return left;
        return $"({left} {op} {right})";
    }

    private static string NotWrap(string inner) =>
        string.IsNullOrEmpty(inner) ? "" : $"NOT ({inner})";

    private static string BuildTerm(FieldTermNode t, BuildContext ctx)
    {
        var col = ColumnMap.TryGetValue(t.Field, out var c) ? c : t.Field;

        return (t.MatchType, t.Value) switch
        {
            (MatchType.IsNull, _) =>
                $"({col} IS NULL)",

            (MatchType.Contains, StringValue sv) =>
                LikeTerm(col, $"%{EscapeLike(sv.Value)}%", ctx),

            (MatchType.Contains, NumberValue nv) =>
                LikeTerm(col, $"%{nv.Value}%", ctx),

            (MatchType.Glob, StringValue sv) =>
                LikeTerm(col, GlobToSqlLike(sv.Value), ctx),

            (MatchType.Exact, StringValue sv) =>
                EqualTerm(col, sv.Value, ctx),

            (MatchType.GreaterThan,        NumberValue nv) => CompareTerm(col, ">",  nv.Value, ctx),
            (MatchType.GreaterThanOrEqual, NumberValue nv) => CompareTerm(col, ">=", nv.Value, ctx),
            (MatchType.LessThan,           NumberValue nv) => CompareTerm(col, "<",  nv.Value, ctx),
            (MatchType.LessThanOrEqual,    NumberValue nv) => CompareTerm(col, "<=", nv.Value, ctx),

            (MatchType.GreaterThan,        DateValue dv) => CompareTerm(col, ">",  dv.Value, ctx),
            (MatchType.GreaterThanOrEqual, DateValue dv) => CompareTerm(col, ">=", dv.Value, ctx),
            (MatchType.LessThan,           DateValue dv) => CompareTerm(col, "<",  dv.Value, ctx),
            (MatchType.LessThanOrEqual,    DateValue dv) => CompareTerm(col, "<=", dv.Value, ctx),

            // Time-of-day: extract seconds-since-midnight from a datetime column
            (MatchType.GreaterThan,        TimeOfDayValue tv) => TimeOfDayTerm(col, ">",  tv.Seconds, ctx),
            (MatchType.GreaterThanOrEqual, TimeOfDayValue tv) => TimeOfDayTerm(col, ">=", tv.Seconds, ctx),
            (MatchType.LessThan,           TimeOfDayValue tv) => TimeOfDayTerm(col, "<",  tv.Seconds, ctx),
            (MatchType.LessThanOrEqual,    TimeOfDayValue tv) => TimeOfDayTerm(col, "<=", tv.Seconds, ctx),

            (MatchType.Between, RangeValue rv) => BuildRange(col, rv, ctx),

            _ => "",
        };
    }

    // ── Clause builders ────────────────────────────────────────────────────────

    private static string LikeTerm(string col, string pattern, BuildContext ctx)
    {
        var p = ctx.Add(pattern);
        return $"({col} LIKE {p} ESCAPE '\\')";
    }

    private static string EqualTerm(string col, string value, BuildContext ctx)
    {
        var p = ctx.Add(value);
        return $"({col} = {p})";
    }

    private static string CompareTerm(string col, string op, object value, BuildContext ctx)
    {
        var p = ctx.Add(value);
        return $"({col} {op} {p})";
    }

    // Compares the time-of-day portion (seconds since midnight) of a datetime column.
    private static string TimeOfDayTerm(string col, string op, int seconds, BuildContext ctx)
    {
        var p = ctx.Add(seconds);
        return $"((DATEPART(HOUR, {col}) * 3600 + DATEPART(MINUTE, {col}) * 60 + DATEPART(SECOND, {col})) {op} {p})";
    }

    private static string BuildRange(string col, RangeValue range, BuildContext ctx)
    {
        var low = range.Low switch
        {
            NumberValue    ln => CompareTerm(col, ">=", ln.Value, ctx),
            DateValue      ld => CompareTerm(col, ">=", ld.Value, ctx),
            TimeOfDayValue lt => TimeOfDayTerm(col, ">=", lt.Seconds, ctx),
            _                 => "",
        };
        var high = range.High switch
        {
            NumberValue    hn => CompareTerm(col, "<=", hn.Value, ctx),
            DateValue      hd => CompareTerm(col, "<=", hd.Value, ctx),
            TimeOfDayValue ht => TimeOfDayTerm(col, "<=", ht.Seconds, ctx),
            _                 => "",
        };
        return Combine("AND", low, high);
    }

    // ── Pattern helpers ────────────────────────────────────────────────────────

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string GlobToSqlLike(string pattern)
    {
        var sb = new StringBuilder();
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*'  => "%",
                '?'  => "_",
                '%'  => "\\%",
                '_'  => "\\_",
                '\\' => "\\\\",
                _    => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    // ── Parameter context ──────────────────────────────────────────────────────

    private sealed class BuildContext
    {
        private int _idx;
        public List<SqlParameter> Parameters { get; } = [];

        public string Add(object value)
        {
            var name = $"@fp{_idx++}";
            Parameters.Add(new SqlParameter(name, value));
            return name;
        }
    }
}
