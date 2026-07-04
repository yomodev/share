using NxtUI.Filtering;
using AwesomeAssertions;

namespace NxtUI.Tests.Filtering;

public class FilterEvaluatorTests
{
    private static readonly string[] Fields = ["ChunkId", "Service", "Pipeline"];
    private static readonly FilterParser Parser = new(Fields);

    private static bool Eval(string filter, object obj) =>
        FilterEvaluator.Evaluate(Parser.Parse(filter), obj);

    private record Evt(string ChunkId = "", string Service = "", string Pipeline = "", int Count = 0);

    // ── Null node ──────────────────────────────────────────────────────────

    [Fact]
    public void Null_node_matches_everything()
    {
        FilterEvaluator.Evaluate(null, new Evt()).Should().BeTrue();
    }

    // ── Contains ───────────────────────────────────────────────────────────

    [Fact]
    public void Contains_matches_substring_case_insensitively()
    {
        Eval("ChunkId:0114", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
    }

    [Fact]
    public void Contains_does_not_match_absent_substring()
    {
        Eval("ChunkId:9999", new Evt(ChunkId: "chk-0114")).Should().BeFalse();
    }

    [Fact]
    public void Leading_zero_string_matches_as_substring()
    {
        Eval("0114", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
    }

    [Fact]
    public void Plain_number_matches_as_substring_in_string_field()
    {
        // "114" is parsed as NumberValue(114) but Contains must still do string match
        Eval("114", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
    }

    [Fact]
    public void Bare_term_matches_any_searchable_field()
    {
        Eval("svcA", new Evt(Service: "svcA")).Should().BeTrue();
        Eval("svcA", new Evt(Pipeline: "svcA-pipeline")).Should().BeTrue();
        Eval("svcA", new Evt(ChunkId: "svcA-001")).Should().BeTrue();
    }

    // ── Exact ──────────────────────────────────────────────────────────────

    [Fact]
    public void Exact_requires_full_value_match()
    {
        Eval("ChunkId:=chk-0114", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
        Eval("ChunkId:=chk",      new Evt(ChunkId: "chk-0114")).Should().BeFalse();
    }

    [Fact]
    public void Exact_is_case_insensitive_by_default()
    {
        Eval("ChunkId:=CHK-0114", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
    }

    // ── Glob ───────────────────────────────────────────────────────────────

    [Fact]
    public void Glob_star_matches_any_suffix()
    {
        Eval("ChunkId:chk-*", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
        Eval("ChunkId:chk-*", new Evt(ChunkId: "other-0114")).Should().BeFalse();
    }

    [Fact]
    public void Glob_question_mark_matches_single_char()
    {
        Eval("ChunkId:chk-011?", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
        Eval("ChunkId:chk-011?", new Evt(ChunkId: "chk-01145")).Should().BeFalse();
    }

    // ── IsNull ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsNull_matches_empty_string()
    {
        Eval("ChunkId:null", new Evt(ChunkId: "")).Should().BeTrue();
    }

    [Fact]
    public void IsNull_does_not_match_non_empty_string()
    {
        Eval("ChunkId:null", new Evt(ChunkId: "abc")).Should().BeFalse();
    }

    // ── Numeric comparisons ────────────────────────────────────────────────

    private record NumObj(int Count);
    private static readonly FilterParser NumParser = new(["Count"]);

    [Theory]
    [InlineData(">5",   6, true)]
    [InlineData(">5",   5, false)]
    [InlineData(">=5",  5, true)]
    [InlineData("<5",   4, true)]
    [InlineData("<5",   5, false)]
    [InlineData("<=5",  5, true)]
    public void Numeric_comparisons(string expr, int value, bool expected)
    {
        var ast = NumParser.Parse($"Count:{expr}");
        FilterEvaluator.Evaluate(ast, new NumObj(value)).Should().Be(expected);
    }

    [Fact]
    public void Between_matches_inclusive_range()
    {
        var ast = NumParser.Parse("Count:1..10");
        FilterEvaluator.Evaluate(ast, new NumObj(1)).Should().BeTrue();
        FilterEvaluator.Evaluate(ast, new NumObj(10)).Should().BeTrue();
        FilterEvaluator.Evaluate(ast, new NumObj(11)).Should().BeFalse();
    }

    // ── Boolean operators ──────────────────────────────────────────────────

    [Fact]
    public void AND_requires_both_conditions()
    {
        // Space between terms is implicit AND
        Eval("Service:svcA Pipeline:pipe1",
            new Evt(Service: "svcA", Pipeline: "pipe1")).Should().BeTrue();

        Eval("Service:svcA Pipeline:pipe1",
            new Evt(Service: "svcA", Pipeline: "other")).Should().BeFalse();
    }

    [Fact]
    public void OR_requires_at_least_one_condition()
    {
        // Comma separates OR alternatives
        Eval("Service:svcA, Service:svcB",
            new Evt(Service: "svcB")).Should().BeTrue();

        Eval("Service:svcA, Service:svcB",
            new Evt(Service: "svcC")).Should().BeFalse();
    }

    [Fact]
    public void NOT_negates_condition()
    {
        Eval("!Service:svcA", new Evt(Service: "svcB")).Should().BeTrue();
        Eval("!Service:svcA", new Evt(Service: "svcA")).Should().BeFalse();
    }

    [Fact]
    public void Double_NOT_matches_same_as_original()
    {
        Eval("!!Service:svcA", new Evt(Service: "svcA")).Should().BeTrue();
        Eval("!!Service:svcA", new Evt(Service: "svcB")).Should().BeFalse();
    }

    [Fact]
    public void NOT_applies_only_to_its_operand_in_AND_chain()
    {
        // "!Service:svcA Pipeline:b" = (NOT Service:svcA) AND Pipeline:b
        Eval("!Service:svcA Pipeline:pipe1", new Evt(Service: "svcB", Pipeline: "pipe1")).Should().BeTrue();
        Eval("!Service:svcA Pipeline:pipe1", new Evt(Service: "svcA", Pipeline: "pipe1")).Should().BeFalse();
        Eval("!Service:svcA Pipeline:pipe1", new Evt(Service: "svcB", Pipeline: "other")).Should().BeFalse();
    }

    [Fact]
    public void AND_binds_tighter_than_OR_in_evaluation()
    {
        // "Service:a Pipeline:b, Service:c" = (a AND b) OR c
        Eval("Service:a Pipeline:b, Service:c", new Evt(Service: "a", Pipeline: "b")).Should().BeTrue();
        Eval("Service:a Pipeline:b, Service:c", new Evt(Service: "c")).Should().BeTrue();
        Eval("Service:a Pipeline:b, Service:c", new Evt(Service: "a", Pipeline: "x")).Should().BeFalse();
    }

    // ── Case sensitivity ───────────────────────────────────────────────────

    [Fact]
    public void Double_quoted_Contains_is_case_sensitive()
    {
        Eval("ChunkId:\"ABC\"", new Evt(ChunkId: "abc")).Should().BeFalse();
        Eval("ChunkId:\"abc\"", new Evt(ChunkId: "abc")).Should().BeTrue();
        Eval("ChunkId:\"abc\"", new Evt(ChunkId: "ABC")).Should().BeFalse();
    }

    [Fact]
    public void Single_quoted_Contains_is_case_insensitive()
    {
        Eval("ChunkId:'ABC'", new Evt(ChunkId: "abc")).Should().BeTrue();
        Eval("ChunkId:'abc'", new Evt(ChunkId: "ABC")).Should().BeTrue();
    }

    [Fact]
    public void Double_quoted_Exact_is_case_sensitive()
    {
        Eval("ChunkId:=\"CHK-0114\"", new Evt(ChunkId: "chk-0114")).Should().BeFalse();
        Eval("ChunkId:=\"chk-0114\"", new Evt(ChunkId: "chk-0114")).Should().BeTrue();
    }

    // ── IsNull on actual null ──────────────────────────────────────────────

    private record WithNull(string? Tag);
    private static readonly FilterParser NullParser = new(["Tag"]);

    [Fact]
    public void IsNull_matches_actual_null_property_value()
    {
        var ast = NullParser.Parse("Tag:null");
        FilterEvaluator.Evaluate(ast, new WithNull(Tag: null)).Should().BeTrue();
        FilterEvaluator.Evaluate(ast, new WithNull(Tag: "value")).Should().BeFalse();
    }

    // ── Unknown field ──────────────────────────────────────────────────────

    [Fact]
    public void Unknown_field_returns_false()
    {
        Eval("UnknownField:anything", new Evt(ChunkId: "anything")).Should().BeFalse();
    }

    // ── DateTime property comparisons ──────────────────────────────────────

    private record Dated(DateTime UpdatedAt, string Name = "");
    private static readonly FilterParser DateParser = new([]);

    [Fact]
    public void DateTime_gt_within_window_matches()
    {
        // -5 min ago is after the -30 min cutoff
        var obj = new Dated(UpdatedAt: DateTime.UtcNow.AddMinutes(-5));
        FilterEvaluator.Evaluate(DateParser.Parse("UpdatedAt:>-30m"), obj).Should().BeTrue();
    }

    [Fact]
    public void DateTime_gt_outside_window_does_not_match()
    {
        // -5 min ago is before the -1 min cutoff
        var obj = new Dated(UpdatedAt: DateTime.UtcNow.AddMinutes(-5));
        FilterEvaluator.Evaluate(DateParser.Parse("UpdatedAt:>-1m"), obj).Should().BeFalse();
    }

    [Fact]
    public void DateTime_Between_matches_date_in_range()
    {
        var obj      = new Dated(UpdatedAt: new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        var inRange  = DateParser.Parse("UpdatedAt:2024-01-01..2024-12-31");
        var outRange = DateParser.Parse("UpdatedAt:2025-01-01..2025-12-31");
        FilterEvaluator.Evaluate(inRange,  obj).Should().BeTrue();
        FilterEvaluator.Evaluate(outRange, obj).Should().BeFalse();
    }

    // ── TimeSpan property via ToDouble (total seconds) ─────────────────────

    private record Timed(TimeSpan Duration);
    private static readonly FilterParser TimeParser = new([]);

    [Fact]
    public void TimeSpan_property_compared_as_total_seconds()
    {
        var obj = new Timed(Duration: TimeSpan.FromMinutes(5)); // 300 seconds
        FilterEvaluator.Evaluate(TimeParser.Parse("Duration:>200"),    obj).Should().BeTrue();
        FilterEvaluator.Evaluate(TimeParser.Parse("Duration:<200"),    obj).Should().BeFalse();
        FilterEvaluator.Evaluate(TimeParser.Parse("Duration:100..400"), obj).Should().BeTrue();
        FilterEvaluator.Evaluate(TimeParser.Parse("Duration:400..600"), obj).Should().BeFalse();
    }

    // ── Numeric Between with float boundaries ──────────────────────────────

    [Fact]
    public void Between_with_decimal_boundaries()
    {
        var ast = NumParser.Parse("Count:0.5..9.5");
        FilterEvaluator.Evaluate(ast, new NumObj(1)).Should().BeTrue();
        FilterEvaluator.Evaluate(ast, new NumObj(9)).Should().BeTrue();
        FilterEvaluator.Evaluate(ast, new NumObj(0)).Should().BeFalse();
        FilterEvaluator.Evaluate(ast, new NumObj(10)).Should().BeFalse();
    }

    // ── Boolean values ──────────────────────────────────────────────────────

    private record Flagged(bool IsOnline);
    private static readonly FilterParser BoolParser = new(["IsOnline"]);

    [Fact]
    public void Bool_true_matches_true_property()
    {
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:true"), new Flagged(true)).Should().BeTrue();
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:true"), new Flagged(false)).Should().BeFalse();
    }

    [Fact]
    public void Bool_false_matches_false_property()
    {
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:false"), new Flagged(false)).Should().BeTrue();
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:false"), new Flagged(true)).Should().BeFalse();
    }

    [Fact]
    public void Bool_is_case_insensitive_keyword()
    {
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:TRUE"), new Flagged(true)).Should().BeTrue();
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:False"), new Flagged(false)).Should().BeTrue();
    }

    [Fact]
    public void NOT_negates_bool_match()
    {
        FilterEvaluator.Evaluate(BoolParser.Parse("!IsOnline:true"), new Flagged(false)).Should().BeTrue();
        FilterEvaluator.Evaluate(BoolParser.Parse("!IsOnline:true"), new Flagged(true)).Should().BeFalse();
    }

    [Fact]
    public void Explicit_exact_operator_works_the_same_for_bool()
    {
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:=true"), new Flagged(true)).Should().BeTrue();
        FilterEvaluator.Evaluate(BoolParser.Parse("IsOnline:=true"), new Flagged(false)).Should().BeFalse();
    }

    [Theory]
    [InlineData(">true")]
    [InlineData(">=false")]
    [InlineData("<true")]
    [InlineData("<=false")]
    public void Comparison_operators_are_rejected_for_bool(string expr)
    {
        var act = () => BoolParser.Parse($"IsOnline:{expr}");
        act.Should().Throw<FilterParseException>();
    }

    [Fact]
    public void Range_is_rejected_for_bool()
    {
        var act = () => BoolParser.Parse("IsOnline:true..false");
        act.Should().Throw<FilterParseException>();
    }

    // ── Comparison/range only valid on orderable values ─────────────────────

    [Fact]
    public void Comparison_operator_on_string_field_throws_at_parse_time()
    {
        var act = () => Parser.Parse("ChunkId:>abc");
        act.Should().Throw<FilterParseException>();
    }

    [Fact]
    public void Range_between_strings_throws_at_parse_time()
    {
        var act = () => Parser.Parse("ChunkId:abc..xyz");
        act.Should().Throw<FilterParseException>();
    }

    [Fact]
    public void Mixed_type_range_throws_at_parse_time()
    {
        var act = () => BoolParser.Parse("IsOnline:5..true");
        act.Should().Throw<FilterParseException>();
    }
}
