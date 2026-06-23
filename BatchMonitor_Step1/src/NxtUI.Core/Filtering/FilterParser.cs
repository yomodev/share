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
            ["run"]       = "RunId",
            ["runid"]     = "RunId",
            ["name"]      = "Name",
            ["type"]      = "Type",
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
    public FilterNode? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var tokens = Tokenize(input);
        var ctx    = new ParseContext(tokens);
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
        while (i < len && Array.IndexOf(WordTerminators, input[i]) < 0)
        {
            // Stop at '..' but keep a single '.' inside words (e.g. "3.14").
            if (input[i] == '.' && i + 1 < len && input[i + 1] == '.') break;
            i++;
        }
        return new(TK.Word, input[start..i], '\0');
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PARSE CONTEXT
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class ParseContext(List<Token> tokens)
    {
        private int _pos;
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
            return (InterpretWord(tok.Text), false);
        }

        return (null, false);
    }

    private static FilterValue InterpretWord(string text)
    {
        // null keyword
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
            return new NullValue();

        // Number
        if (TryParseNumber(text, out var num))
            return new NumberValue(num);

        // Date / time
        if (TryParseDate(text, out var dt))
            return new DateValue(dt);

        // String (glob or plain)
        return new StringValue(text);
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
        new(@"^-(\d+)([mhdw])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeOnly =
        new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?z?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsoDate =
        new(@"^(\d{4}-\d{2}-\d{2})(T\d{2}:\d{2}(:\d{2})?)?z?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParseDate(string text, out DateTime result)
    {
        result = default;
        var textLower = text.ToLowerInvariant();

        // Named dates (z suffix is a no-op for now — server parser always uses UTC).
        switch (textLower)
        {
            case "now" or "nowz":
                result = DateTime.UtcNow;
                return true;
            case "today" or "todayz":
                result = DateTime.UtcNow.Date;
                return true;
            case "yesterday" or "yesterdayz":
                result = DateTime.UtcNow.Date.AddDays(-1);
                return true;
        }

        // Relative offset: -7d, -2h, -30m, -1w
        var relMatch = RelativeDate.Match(text);
        if (relMatch.Success)
        {
            int amount = int.Parse(relMatch.Groups[1].Value);
            result = relMatch.Groups[2].Value.ToLower() switch
            {
                "m" => DateTime.UtcNow.AddMinutes(-amount),
                "h" => DateTime.UtcNow.AddHours(-amount),
                "d" => DateTime.UtcNow.AddDays(-amount),
                "w" => DateTime.UtcNow.AddDays(-amount * 7),
                _   => DateTime.UtcNow,
            };
            return true;
        }

        // Time-only: 9:30 or 09:30:00 (or with z)
        var timeMatch = TimeOnly.Match(text);
        if (timeMatch.Success && !text.StartsWith('-'))
        {
            int h = int.Parse(timeMatch.Groups[1].Value);
            int m = int.Parse(timeMatch.Groups[2].Value);
            int s = timeMatch.Groups[3].Success ? int.Parse(timeMatch.Groups[3].Value) : 0;
            result = DateTime.UtcNow.Date.AddHours(h).AddMinutes(m).AddSeconds(s);
            return true;
        }

        // ISO date (with or without time, with or without z)
        var isoMatch = IsoDate.Match(text);
        if (isoMatch.Success)
        {
            // Normalise: replace lowercase 't' separator and ensure 'Z' suffix for parsing.
            var normalised = text.TrimEnd('z', 'Z').Replace('t', 'T') + "Z";
            if (DateTime.TryParse(normalised,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out result))
                return true;
        }

        return false;
    }

    // ── Alias resolution ───────────────────────────────────────────────────

    private string ResolveAlias(string raw) =>
        _aliases.TryGetValue(raw, out var resolved) ? resolved : raw;
}

/// <summary>Thrown by <see cref="FilterParser"/> when the input is syntactically invalid.</summary>
public sealed class FilterParseException(string message) : Exception(message);
