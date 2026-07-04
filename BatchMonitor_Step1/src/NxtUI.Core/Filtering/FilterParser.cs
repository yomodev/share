using System.Globalization;
using System.Text.RegularExpressions;

namespace NxtUI.Filtering;

/// <summary>
/// Parses a filter string into a <see cref="FilterNode"/> AST.
///
/// <para>
/// Global (field-less) terms are immediately expanded into an <see cref="OrNode"/>
/// across the supplied <paramref name="searchableFields"/> list, so the resulting
/// AST contains only concrete field names — matching what the JS parser produces
/// before serialising to JSON for the server.
/// </para>
///
/// <para>
/// Date/time literals without a <c>z</c> suffix are resolved against UTC now,
/// because the C# parser runs server-side and has no access to the browser's
/// local timezone. Pass dates with a <c>z</c> suffix or use
/// <c>DateTimeOffset.UtcNow</c>-anchored relative values for predictable results.
/// </para>
/// </summary>
public sealed class FilterParser
{
    private readonly string[]                          _searchableFields;
    private readonly IReadOnlyDictionary<string, string> _aliases;

    // ── Standard alias table (both short and full forms resolve to the same name) ──

    private static readonly Dictionary<string, string> DefaultAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // PerformanceEvent
            ["svc"]       = "Service",
            ["service"]   = "Service",
            ["pipe"]      = "Pipeline",
            ["pipeline"]  = "Pipeline",
            ["srv"]       = "Server",
            ["server"]    = "Server",
            ["pid"]       = "ProcessId",
            ["processid"] = "ProcessId",
            ["src"]       = "Source",
            ["source"]    = "Source",
            ["chunk"]     = "ChunkId",
            ["chunkid"]   = "ChunkId",
            // RunSummary
            ["run"]         = "RunId",
            ["runid"]       = "RunId",
            ["requestid"]   = "RequestId",
            ["reqid"]       = "RequestId",
            ["type"]        = "Type",
            ["status"]      = "Status",
            ["desc"]        = "Description",
            ["description"] = "Description",
            ["start"]       = "StartTime",
            ["started"]     = "StartTime",
            ["end"]         = "EndTime",
            ["ended"]       = "EndTime",
            ["finish"]      = "EndTime",
            ["finished"]    = "EndTime",
            // ServiceStatus
            ["svcname"]   = "ServiceName",
            ["servicename"] = "ServiceName",
            ["host"]      = "HostName",
            ["hostname"]  = "HostName",
            ["ram"]       = "RamMb",
            ["mem"]       = "RamMb",
            ["memory"]    = "RamMb",
            ["peak"]      = "PeakMb",
            ["updated"]   = "UpdatedDateTime",
            ["update"]    = "UpdatedDateTime",
        };

    public FilterParser(
        string[] searchableFields,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        _searchableFields = searchableFields;
        _aliases          = aliases ?? DefaultAliases;
    }

    /// <summary>Parses <paramref name="input"/> and returns the AST root, or null for empty input.</summary>
    /// <param name="useUtc">
    /// When false, date/time literals without a Z suffix are treated as server-local time
    /// and converted to UTC for comparison. Literals ending in Z are always UTC.
    /// </param>
    public FilterNode? Parse(string input, bool useUtc = true)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var tokens = Tokenize(input);
        var ctx    = new ParseContext(tokens, useUtc);
        var node   = ParseOr(ctx);
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TOKENIZER
    // ═══════════════════════════════════════════════════════════════════════

    private enum TK
    {
        Word, Quoted,
        Not, Comma, LParen, RParen,
        Colon, ColonEq,
        Gt, Gte, Lt, Lte,
        DotDot,
        Eof,
    }

    private readonly record struct Token(TK Kind, string Text, char QuoteChar = '\0');

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0, len = input.Length;

        while (i < len)
        {
            // Skip whitespace — multiple spaces still just mean AND.
            if (char.IsWhiteSpace(input[i])) { i++; continue; }

            char c = input[i];

            switch (c)
            {
                case '!':  tokens.Add(new(TK.Not,    "!", '\0')); i++; break;
                case ',':  tokens.Add(new(TK.Comma,  ",", '\0')); i++; break;
                case '(':  tokens.Add(new(TK.LParen, "(", '\0')); i++; break;
                case ')':  tokens.Add(new(TK.RParen, ")", '\0')); i++; break;

                case '"':
                case '\'':
                {
                    char q   = c; i++;
                    int  start = i;
                    while (i < len && input[i] != q) i++;
                    tokens.Add(new(TK.Quoted, input[start..i], q));
                    if (i < len) i++; // consume closing quote
                    break;
                }

                case ':':
                    if (i + 1 < len && input[i + 1] == '=')
                    { tokens.Add(new(TK.ColonEq, ":=", '\0')); i += 2; }
                    else
                    { tokens.Add(new(TK.Colon,   ":",  '\0')); i++; }
                    break;

                case '>':
                    if (i + 1 < len && input[i + 1] == '=')
                    { tokens.Add(new(TK.Gte, ">=", '\0')); i += 2; }
                    else
                    { tokens.Add(new(TK.Gt,  ">",  '\0')); i++; }
                    break;

                case '<':
                    if (i + 1 < len && input[i + 1] == '=')
                    { tokens.Add(new(TK.Lte, "<=", '\0')); i += 2; }
                    else
                    { tokens.Add(new(TK.Lt,  "<",  '\0')); i++; }
                    break;

                case '.':
                    if (i + 1 < len && input[i + 1] == '.')
                    { tokens.Add(new(TK.DotDot, "..", '\0')); i += 2; }
                    else
                    { tokens.Add(ReadWord(input, ref i, len)); }
                    break;

                default:
                    tokens.Add(ReadWord(input, ref i, len));
                    break;
            }
        }

        tokens.Add(new(TK.Eof, "", '\0'));
        return tokens;
    }

    // Word terminators: whitespace and any character handled as a distinct token above.
    private static readonly char[] WordTerminators = [' ', '\t', '\r', '\n', ',', '(', ')', '!', ':', '"', '\'', '>', '<'];

    private static Token ReadWord(string input, ref int i, int len)
    {
        int start = i;
        while (i < len)
        {
            // Stop at '..' but keep a single '.' inside words (e.g. "3.14").
            if (input[i] == '.' && i + 1 < len && input[i + 1] == '.') break;
            // Allow ':' inside a word when it looks like a time literal (digit:digit).
            if (input[i] == ':' && i > start && char.IsDigit(input[i - 1]) && i + 1 < len && char.IsDigit(input[i + 1]))
            { i++; continue; }
            if (Array.IndexOf(WordTerminators, input[i]) >= 0) break;
            i++;
        }
        return new(TK.Word, input[start..i], '\0');
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PARSE CONTEXT
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class ParseContext(List<Token> tokens, bool useUtc = true)
    {
        private int _pos;
        public bool UseUtc => useUtc;
        public Token Current => tokens[_pos];
        public Token Peek(int offset = 1) =>
            _pos + offset < tokens.Count ? tokens[_pos + offset] : tokens[^1];
        public Token Consume() => tokens[_pos++];
        public Token Expect(TK kind)
        {
            var t = Current;
            if (t.Kind == kind) { _pos++; return t; }
            throw new FilterParseException($"Expected {kind} but got '{t.Text}'");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RECURSIVE DESCENT
    // ═══════════════════════════════════════════════════════════════════════

    // or_expr  = and_expr (',' and_expr)*
    private FilterNode? ParseOr(ParseContext ctx)
    {
        var left = ParseAnd(ctx);
        while (ctx.Current.Kind == TK.Comma)
        {
            ctx.Consume();
            var right = ParseAnd(ctx);
            if (left is null)        left = right;
            else if (right is not null) left = new OrNode(left, right);
        }
        return left;
    }

    // and_expr = not_expr (not_expr)*   — space is AND
    private FilterNode? ParseAnd(ParseContext ctx)
    {
        FilterNode? left = null;

        while (ctx.Current.Kind is not (TK.Comma or TK.RParen or TK.Eof))
        {
            var right = ParseNot(ctx);
            if (right is null) break;
            left = left is null ? right : new AndNode(left, right);
        }

        return left;
    }

    // not_expr = '!' not_expr | primary
    private FilterNode? ParseNot(ParseContext ctx)
    {
        if (ctx.Current.Kind != TK.Not) return ParsePrimary(ctx);
        ctx.Consume();
        var operand = ParseNot(ctx);
        return operand is null ? null : new NotNode(operand);
    }

    // primary = '(' expression ')' | term
    private FilterNode? ParsePrimary(ParseContext ctx)
    {
        if (ctx.Current.Kind == TK.LParen)
        {
            ctx.Consume();
            var inner = ParseOr(ctx);
            ctx.Expect(TK.RParen);
            return inner;
        }
        return ParseTerm(ctx);
    }

    // term = field_term | bare_term
    private FilterNode? ParseTerm(ParseContext ctx)
    {
        // Field-targeted: word or quoted, followed by ':' or ':='
        if (ctx.Current.Kind == TK.Word)
        {
            var next = ctx.Peek();
            if (next.Kind is TK.Colon or TK.ColonEq)
                return ParseFieldTerm(ctx);
        }

        return ParseBareTerm(ctx);
    }

    // field_term = name (':' | ':=') ('(' field_expr ')' | field_value)
    private FilterNode? ParseFieldTerm(ParseContext ctx)
    {
        var fieldRaw = ctx.Consume().Text;
        var field    = ResolveAlias(fieldRaw);
        bool exact   = ctx.Current.Kind == TK.ColonEq;
        ctx.Consume(); // consume ':' or ':='

        // Field-scoped group: svc:(Loader, Transformer)
        if (!exact && ctx.Current.Kind == TK.LParen)
        {
            ctx.Consume();
            var group = ParseFieldScopedOr(ctx, field);
            ctx.Expect(TK.RParen);
            return group;
        }

        return ParseFieldValue(ctx, field, exact);
    }

    // Field-scoped OR inside parens: (Loader, Transformer) with implicit `field:`
    private FilterNode? ParseFieldScopedOr(ParseContext ctx, string field)
    {
        var left = ParseFieldScopedAnd(ctx, field);
        while (ctx.Current.Kind == TK.Comma)
        {
            ctx.Consume();
            var right = ParseFieldScopedAnd(ctx, field);
            if (left is null)           left = right;
            else if (right is not null) left = new OrNode(left, right);
        }
        return left;
    }

    private FilterNode? ParseFieldScopedAnd(ParseContext ctx, string field)
    {
        FilterNode? left = null;
        while (ctx.Current.Kind is not (TK.Comma or TK.RParen or TK.Eof))
        {
            var right = ParseFieldValue(ctx, field, exact: false);
            if (right is null) break;
            left = left is null ? right : new AndNode(left, right);
        }
        return left;
    }

    // Parses a single field value: [cmp_op] value_atom ['..' value_atom]
    private FilterNode? ParseFieldValue(ParseContext ctx, string field, bool exact)
    {
        // Optional comparison operator.
        MatchType? cmpOp = ctx.Current.Kind switch
        {
            TK.Gt  => MatchType.GreaterThan,
            TK.Gte => MatchType.GreaterThanOrEqual,
            TK.Lt  => MatchType.LessThan,
            TK.Lte => MatchType.LessThanOrEqual,
            _      => null,
        };
        if (cmpOp.HasValue) ctx.Consume();

        var (value, caseSensitive) = ParseValueAtom(ctx);
        if (value is null) return null;

        // Validate: := with wildcard is a parse error.
        if (exact && value is StringValue sv && MongoFilterBuilder.HasWildcard(sv.Value))
            throw new FilterParseException($"Wildcards are not allowed with exact match (:=): '{sv.Value}'");

        // Validate: booleans only support equality — comparison operators and ranges
        // ("field:>true", "field:true..false") have no meaning for a true/false value.
        if (value is BoolValue && (cmpOp.HasValue || ctx.Current.Kind == TK.DotDot))
            throw new FilterParseException("Comparison operators and ranges are not supported for true/false values");

        // Range: value '..' value
        if (!cmpOp.HasValue && !exact && ctx.Current.Kind == TK.DotDot)
        {
            ctx.Consume();
            var (high, _) = ParseValueAtom(ctx);
            if (high is null) throw new FilterParseException("Expected value after '..'");
            return new FieldTermNode(field, MatchType.Between, false, new RangeValue(value, high));
        }

        if (cmpOp.HasValue)
            return new FieldTermNode(field, cmpOp.Value, false, value);

        if (exact)
            return new FieldTermNode(field, MatchType.Exact, caseSensitive, value);

        if (value is NullValue)
            return new FieldTermNode(field, MatchType.IsNull, false, value);

        if (value is StringValue strVal)
        {
            var matchType = MongoFilterBuilder.HasWildcard(strVal.Value) ? MatchType.Glob : MatchType.Contains;
            return new FieldTermNode(field, matchType, caseSensitive, value);
        }

        // Number or Date with no comparison operator → exact equality.
        return new FieldTermNode(field, MatchType.Exact, false, value);
    }

    // Parses a bare term (no field prefix) and expands it across searchable fields.
    private FilterNode? ParseBareTerm(ParseContext ctx)
    {
        var (value, caseSensitive) = ParseValueAtom(ctx);
        if (value is null) return null;

        if (_searchableFields.Length == 0) return null;

        MatchType matchType = value switch
        {
            NullValue              => MatchType.IsNull,
            StringValue sv when MongoFilterBuilder.HasWildcard(sv.Value)
                               => MatchType.Glob,
            StringValue        => MatchType.Contains,
            _                  => MatchType.Contains, // numbers/dates fall through
        };

        FilterNode? result = null;
        foreach (var f in _searchableFields)
        {
            FilterNode term = new FieldTermNode(f, matchType, caseSensitive, value);
            result = result is null ? term : new OrNode(result, term);
        }
        return result;
    }

    // ── Value atom parser ──────────────────────────────────────────────────

    private (FilterValue? value, bool caseSensitive) ParseValueAtom(ParseContext ctx)
    {
        var tok = ctx.Current;

        if (tok.Kind == TK.Quoted)
        {
            ctx.Consume();
            bool cs = tok.QuoteChar == '"';
            return (new StringValue(tok.Text), cs);
        }

        if (tok.Kind == TK.Word)
        {
            ctx.Consume();
            return (InterpretWord(tok.Text, ctx.UseUtc), false);
        }

        return (null, false);
    }

    private static FilterValue InterpretWord(string text, bool useUtc = true)
    {
        // null keyword
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
            return new NullValue();

        // Bare true/false keyword
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
            return new BoolValue(true);
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
            return new BoolValue(false);

        // Number
        if (TryParseNumber(text, out var num))
            return new NumberValue(num);

        // Absolute date / relative offset (includes hh:mm → today + time)
        if (TryParseDate(text, useUtc, out var dt))
            return new DateValue(dt);

        // String (glob or plain)
        return new StringValue(text);
    }

    private static bool TryParseTimeOfDay(string text, out TimeOfDayValue result)
    {
        result = default!;
        var m = TimeOnly.Match(text);
        if (!m.Success || text.StartsWith('-')) return false;
        int h = int.Parse(m.Groups[1].Value);
        int min = int.Parse(m.Groups[2].Value);
        int s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        result = new TimeOfDayValue(h * 3600 + min * 60 + s);
        return true;
    }

    private static bool TryParseNumber(string text, out double result)
    {
        // Leading-zero strings (e.g. "0114") are identifiers, not numbers.
        if (text.Length > 1 && text[0] == '0' && char.IsDigit(text[1]))
        { result = 0; return false; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    // ── Date parsing ───────────────────────────────────────────────────────

    private static readonly Regex RelativeDate =
        new(@"^(-?)(\d+)([smhdw])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeOnly =
        new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?z?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsoDate =
        new(@"^(\d{4}-\d{2}-\d{2})(T\d{2}:\d{2}(:\d{2})?)?z?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParseDate(string text, bool useUtc, out DateTime result)
    {
        result = default;
        var textLower = text.ToLowerInvariant();

        // Z suffix forces UTC interpretation regardless of the useUtc setting.
        bool hasZ  = textLower.EndsWith('z');
        bool asUtc = useUtc || hasZ;

        // Named dates — "now" is always UTC; today/yesterday respect setting.
        switch (textLower)
        {
            case "now" or "nowz":
                result = DateTime.UtcNow;
                return true;
            case "today" or "todayz":
                result = asUtc ? DateTime.UtcNow.Date : DateTime.Today.ToUniversalTime();
                return true;
            case "yesterday" or "yesterdayz":
                result = asUtc
                    ? DateTime.UtcNow.Date.AddDays(-1)
                    : DateTime.Today.AddDays(-1).ToUniversalTime();
                return true;
        }

        // Relative offset: -7d / -2h / -30m / -1w  → now − N  (always UTC-relative)
        //                   7d /  2h /  30m /  1w  → today + N
        var relMatch = RelativeDate.Match(text);
        if (relMatch.Success)
        {
            bool negative = relMatch.Groups[1].Value == "-";
            int  amount   = int.Parse(relMatch.Groups[2].Value);
            if (negative)
            {
                result = relMatch.Groups[3].Value.ToLower() switch
                {
                    "s" => DateTime.UtcNow.AddSeconds(-amount),
                    "m" => DateTime.UtcNow.AddMinutes(-amount),
                    "h" => DateTime.UtcNow.AddHours(-amount),
                    "d" => DateTime.UtcNow.AddDays(-amount),
                    "w" => DateTime.UtcNow.AddDays(-amount * 7),
                    _   => DateTime.UtcNow,
                };
            }
            else
            {
                var todayBase = asUtc ? DateTime.UtcNow.Date : DateTime.Today.ToUniversalTime();
                result = relMatch.Groups[3].Value.ToLower() switch
                {
                    "s" => todayBase.AddSeconds(amount),
                    "m" => todayBase.AddMinutes(amount),
                    "h" => todayBase.AddHours(amount),
                    "d" => todayBase.AddDays(amount),
                    "w" => todayBase.AddDays(amount * 7),
                    _   => todayBase,
                };
            }
            return true;
        }

        // Time-only (hh:mm or hh:mm:ss) — today's date at the given time.
        // With Z suffix or UTC setting: today UTC. Without: today local → UTC.
        var todMatch = TimeOnly.Match(text);
        if (todMatch.Success && !text.StartsWith('-'))
        {
            int th = int.Parse(todMatch.Groups[1].Value);
            int tm = int.Parse(todMatch.Groups[2].Value);
            int ts = todMatch.Groups[3].Success ? int.Parse(todMatch.Groups[3].Value) : 0;
            var todayBase = asUtc ? DateTime.UtcNow.Date : DateTime.Today;
            result = todayBase.AddHours(th).AddMinutes(tm).AddSeconds(ts);
            if (!asUtc) result = result.ToUniversalTime();
            return true;
        }

        // ISO date (with or without time, with or without z).
        var isoMatch = IsoDate.Match(text);
        if (isoMatch.Success)
        {
            var bare = text.TrimEnd('z', 'Z').Replace('t', 'T');
            if (asUtc)
            {
                if (DateTime.TryParse(bare + "Z",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out result))
                    return true;
            }
            else
            {
                // Treat as server-local time, convert to UTC.
                if (DateTime.TryParse(bare,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out result))
                {
                    result = result.ToUniversalTime();
                    return true;
                }
            }
        }

        return false;
    }

    // ── Alias resolution ───────────────────────────────────────────────────

    private string ResolveAlias(string raw) =>
        _aliases.TryGetValue(raw, out var resolved) ? resolved : raw;
}

/// <summary>Thrown by <see cref="FilterParser"/> when the input is syntactically invalid.</summary>
public sealed class FilterParseException(string message) : Exception(message);
