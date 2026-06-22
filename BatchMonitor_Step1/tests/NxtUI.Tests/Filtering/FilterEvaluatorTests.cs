using NxtUI.Filtering;
using FluentAssertions;

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
}
