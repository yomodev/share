using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace NxtUI.Core.Filtering;

/// <summary>
/// Translates a <see cref="FilterNode"/> AST into a MongoDB
/// <see cref="FilterDefinition{TDocument}"/> for <see cref="BsonDocument"/> collections.
/// No filter-string parsing happens here — the AST is the contract.
/// </summary>
public static class MongoFilterBuilder
{
    private static readonly FilterDefinitionBuilder<BsonDocument> B =
        Builders<BsonDocument>.Filter;

    /// <summary>
    /// Builds a filter from the given AST node.
    /// Returns <see cref="FilterDefinition{TDocument}.Empty"/> for a null node.
    /// </summary>
    public static FilterDefinition<BsonDocument> Build(FilterNode? node) =>
        node switch
        {
            null => B.Empty,
            AndNode and => B.And(Build(and.Left), Build(and.Right)),
            OrNode or => B.Or(Build(or.Left), Build(or.Right)),
            NotNode not => B.Not(Build(not.Operand)),
            FieldTermNode t => BuildTerm(t),
            _ => B.Empty,
        };

    // Collection-name search (MongoDashboard): a `listCollections` result document has
    // exactly one field worth searching — "name" — so a bare term always means "name"
    // with no ambiguity, unlike the arbitrary-schema document browser.
    private static readonly FilterParser CollectionNameParser = new(
        searchableFields: ["name"],
        aliases: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "name",
            ["collection"] = "name",
        });

    /// <summary>
    /// Builds a filter for a MongoDB <c>listCollections</c> result document (shape:
    /// <c>{ name, type, options, info }</c>) using the same shared grammar (glob, NOT,
    /// OR, quoting) as every other filter box, instead of passing the raw input straight
    /// through as a regex — which let unescaped regex metacharacters (e.g. <c>.</c>, <c>+</c>)
    /// leak into the match and gave <c>!</c>/<c>*</c> regex meanings instead of NOT/glob.
    /// Falls back to a literal (escaped) contains match on <c>name</c> if the input can't
    /// be parsed by the grammar, so the search box never just breaks on odd input.
    /// </summary>
    public static FilterDefinition<BsonDocument> BuildCollectionNameFilter(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return B.Empty;

        var trimmed = search.Trim();
        try
        {
            var node = CollectionNameParser.Parse(trimmed);
            return Build(node);
        }
        catch (FilterParseException)
        {
            return B.Regex("name", ContainsRegex(trimmed, caseSensitive: false));
        }
    }

    // ── Field term ─────────────────────────────────────────────────────────

    private static FilterDefinition<BsonDocument> BuildTerm(FieldTermNode t) =>
        (t.MatchType, t.Value) switch
        {
            // ── Null check ─────────────────────────────────────────────────
            (MatchType.IsNull, _) =>
                B.Or(B.Eq(t.Field, BsonNull.Value), B.Not(B.Exists(t.Field))),

            // ── String matches ─────────────────────────────────────────────
            (MatchType.Contains, StringValue sv) =>
                B.Regex(t.Field, ContainsRegex(sv.Value, t.CaseSensitive)),

            (MatchType.Contains, NumberValue nv) =>
                B.Regex(t.Field, ContainsRegex(nv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), false)),

            (MatchType.Glob, StringValue sv) =>
                B.Regex(t.Field, GlobRegex(sv.Value, t.CaseSensitive)),

            (MatchType.Exact, StringValue sv) =>
                B.Regex(t.Field, ExactRegex(sv.Value, t.CaseSensitive)),

            // A bare number/date with no operator parses to Exact (equality) —
            // see FilterParser's "Number or Date with no comparison operator".
            (MatchType.Exact, NumberValue nv) => B.Eq(t.Field, nv.Value),
            (MatchType.Exact, DateValue dv) => B.Eq(t.Field, dv.Value),

            // Booleans only ever parse to Exact, but ParseBareTerm's global-term
            // expansion can produce Contains for a bare "true"/"false" — both mean
            // the same thing for a boolean value, so both map to equality.
            (MatchType.Exact, BoolValue bv) => B.Eq(t.Field, bv.Value),
            (MatchType.Contains, BoolValue bv) => B.Eq(t.Field, bv.Value),

            // ── Numeric comparisons ────────────────────────────────────────
            (MatchType.GreaterThan, NumberValue nv) => B.Gt(t.Field, nv.Value),
            (MatchType.GreaterThanOrEqual, NumberValue nv) => B.Gte(t.Field, nv.Value),
            (MatchType.LessThan, NumberValue nv) => B.Lt(t.Field, nv.Value),
            (MatchType.LessThanOrEqual, NumberValue nv) => B.Lte(t.Field, nv.Value),

            // ── Date comparisons ───────────────────────────────────────────
            (MatchType.GreaterThan, DateValue dv) => B.Gt(t.Field, dv.Value),
            (MatchType.GreaterThanOrEqual, DateValue dv) => B.Gte(t.Field, dv.Value),
            (MatchType.LessThan, DateValue dv) => B.Lt(t.Field, dv.Value),
            (MatchType.LessThanOrEqual, DateValue dv) => B.Lte(t.Field, dv.Value),

            // ── Time-of-day comparisons ($expr + $mod on epoch ms) ─────────
            (MatchType.GreaterThan, TimeOfDayValue tv) => TimeOfDayExpr(t.Field, "$gt", tv.Seconds),
            (MatchType.GreaterThanOrEqual, TimeOfDayValue tv) => TimeOfDayExpr(t.Field, "$gte", tv.Seconds),
            (MatchType.LessThan, TimeOfDayValue tv) => TimeOfDayExpr(t.Field, "$lt", tv.Seconds),
            (MatchType.LessThanOrEqual, TimeOfDayValue tv) => TimeOfDayExpr(t.Field, "$lte", tv.Seconds),

            // ── Range (..) ─────────────────────────────────────────────────
            (MatchType.Between, RangeValue rv) => BuildRange(t.Field, rv),

            _ => B.Empty,
        };

    // ── Range ──────────────────────────────────────────────────────────────

    private static FilterDefinition<BsonDocument> BuildRange(string field, RangeValue range)
    {
        var filters = new List<FilterDefinition<BsonDocument>>(2);

        switch (range.Low)
        {
            case NumberValue ln: filters.Add(B.Gte(field, ln.Value)); break;
            case DateValue ld: filters.Add(B.Gte(field, ld.Value)); break;
            case TimeOfDayValue lt: filters.Add(TimeOfDayExpr(field, "$gte", lt.Seconds)); break;
        }

        switch (range.High)
        {
            case NumberValue hn: filters.Add(B.Lte(field, hn.Value)); break;
            case DateValue hd: filters.Add(B.Lte(field, hd.Value)); break;
            case TimeOfDayValue ht: filters.Add(TimeOfDayExpr(field, "$lte", ht.Seconds)); break;
        }

        return filters.Count switch
        {
            0 => B.Empty,
            1 => filters[0],
            _ => B.And(filters),
        };
    }

    // Compares the time-of-day portion of a BSON date field.
    // Uses $expr: { $op: [{ $mod: [{ $toLong: "$field" }, 86400000] }, secondsMs] }
    // where secondsMs is the filter time in milliseconds.
    private static FilterDefinition<BsonDocument> TimeOfDayExpr(string field, string op, int seconds)
    {
        long ms = (long)seconds * 1000;
        var expr = new BsonDocument("$expr", new BsonDocument(op, new BsonArray
        {
            new BsonDocument("$mod", new BsonArray
            {
                new BsonDocument("$toLong", $"${field}"),
                86_400_000L,
            }),
            ms,
        }));
        return new BsonDocumentFilterDefinition<BsonDocument>(expr);
    }

    // ── Regex helpers ──────────────────────────────────────────────────────

    private static string RegexFlags(bool caseSensitive) =>
        caseSensitive ? "" : "i";

    private static BsonRegularExpression ContainsRegex(string value, bool caseSensitive) =>
        new(Regex.Escape(value), RegexFlags(caseSensitive));

    private static BsonRegularExpression ExactRegex(string value, bool caseSensitive) =>
        new($"^{Regex.Escape(value)}$", RegexFlags(caseSensitive));

    /// <summary>
    /// Converts a glob pattern (* = any chars, ? = single char) to an anchored
    /// regex. Patterns with no leading * are anchored at the start; patterns
    /// with no trailing * are anchored at the end.
    ///
    /// Examples:
    ///   word*   → ^word.*$
    ///   *word   → ^.*word$
    ///   d?g     → ^d.g$
    ///   Lo?d*   → ^Lo.d.*$
    ///   *word*  → ^.*word.*$  (equivalent to a contains match)
    /// </summary>
    private static BsonRegularExpression GlobRegex(string pattern, bool caseSensitive)
    {
        var sb = new System.Text.StringBuilder("^");

        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        sb.Append('$');
        return new BsonRegularExpression(sb.ToString(), RegexFlags(caseSensitive));
    }

    /// <summary>Returns true if the string contains a <c>*</c> or <c>?</c> wildcard.</summary>
    internal static bool HasWildcard(string value) =>
        value.Contains('*') || value.Contains('?');
}
