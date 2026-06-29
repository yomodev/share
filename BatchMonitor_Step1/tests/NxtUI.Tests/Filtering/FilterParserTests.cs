using NxtUI.Filtering;
using FluentAssertions;
using MatchType = NxtUI.Filtering.MatchType;

namespace NxtUI.Tests.Filtering;

public class FilterParserTests
{
    private static readonly string[] Fields = ["ChunkId", "Service", "Pipeline"];

    private static readonly FilterParser Parser = new(Fields);
    private static FilterNode? Parse(string input) => Parser.Parse(input);

    // ── Null / empty ───────────────────────────────────────────────────────

    [Fact]
    public void Empty_input_returns_null()
    {
        Parse("").Should().BeNull();
        Parse("   ").Should().BeNull();
    }

    // ── Bare term expansion ────────────────────────────────────────────────

    [Fact]
    public void Bare_term_expands_to_OR_across_all_searchable_fields()
    {
        var node = Parse("hello");
        node.Should().BeOfType<OrNode>();
    }

    [Fact]
    public void Bare_term_produces_Contains_match_for_plain_string()
    {
        var node = Parse("hello");
        // Flatten OR tree and collect all leaves
        var leaves = CollectLeaves(node!);
        leaves.Should().AllSatisfy(l =>
        {
            l.MatchType.Should().Be(MatchType.Contains);
            l.Value.Should().BeOfType<StringValue>().Which.Value.Should().Be("hello");
        });
        leaves.Select(l => l.Field).Should().BeEquivalentTo(Fields);
    }

    [Fact]
    public void Leading_zero_string_is_treated_as_string_not_number()
    {
        var node = Parse("0114");
        var leaves = CollectLeaves(node!);
        leaves.Should().AllSatisfy(l =>
        {
            l.Value.Should().BeOfType<StringValue>().Which.Value.Should().Be("0114");
        });
    }

    [Fact]
    public void Plain_integer_is_treated_as_NumberValue()
    {
        var node = Parse("42");
        var leaves = CollectLeaves(node!);
        leaves.Should().AllSatisfy(l =>
            l.Value.Should().BeOfType<NumberValue>().Which.Value.Should().Be(42));
    }

    // ── Field-scoped terms ─────────────────────────────────────────────────

    [Fact]
    public void Field_colon_term_produces_single_FieldTermNode()
    {
        var node = Parse("ChunkId:abc");
        node.Should().BeOfType<FieldTermNode>()
            .Which.Field.Should().Be("ChunkId");
    }

    [Fact]
    public void Field_colon_term_is_case_insensitive_on_field_name()
    {
        var node = Parse("chunkid:abc");
        node.Should().BeOfType<FieldTermNode>()
            .Which.Field.Should().Be("ChunkId");
    }

    [Fact]
    public void Alias_chunk_resolves_to_ChunkId()
    {
        var node = Parse("chunk:0114");
        node.Should().BeOfType<FieldTermNode>()
            .Which.Field.Should().Be("ChunkId");
    }

    [Fact]
    public void Exact_match_operator_produces_Exact_matchType()
    {
        var node = Parse("ChunkId:=abc") as FieldTermNode;
        node.Should().NotBeNull();
        node!.MatchType.Should().Be(MatchType.Exact);
    }

    [Fact]
    public void Wildcard_produces_Glob_matchType()
    {
        var node = Parse("ChunkId:chk-*") as FieldTermNode;
        node!.MatchType.Should().Be(MatchType.Glob);
    }

    // ── Boolean operators ──────────────────────────────────────────────────

    [Fact]
    public void AND_produces_AndNode()
    {
        // Space between terms is implicit AND
        var node = Parse("Service:svcA Pipeline:pipe1");
        node.Should().BeOfType<AndNode>();
    }

    [Fact]
    public void OR_produces_OrNode()
    {
        // Comma separates OR alternatives
        var node = Parse("Service:svcA, Service:svcB");
        node.Should().BeOfType<OrNode>();
    }

    [Fact]
    public void NOT_produces_NotNode()
    {
        var node = Parse("!Service:svcA");
        node.Should().BeOfType<NotNode>();
    }

    // ── Comparison operators ───────────────────────────────────────────────

    [Theory]
    [InlineData(">5",  MatchType.GreaterThan)]
    [InlineData(">=5", MatchType.GreaterThanOrEqual)]
    [InlineData("<5",  MatchType.LessThan)]
    [InlineData("<=5", MatchType.LessThanOrEqual)]
    public void Comparison_operators_parsed_correctly(string expr, MatchType expected)
    {
        var node = Parse($"ChunkId:{expr}") as FieldTermNode;
        node!.MatchType.Should().Be(expected);
    }

    [Fact]
    public void Range_operator_produces_Between_matchType()
    {
        var node = Parse("ChunkId:1..10") as FieldTermNode;
        node!.MatchType.Should().Be(MatchType.Between);
        node.Value.Should().BeOfType<RangeValue>();
    }

    // ── Null keyword ───────────────────────────────────────────────────────

    [Fact]
    public void Null_keyword_produces_IsNull_matchType()
    {
        var node = Parse("ChunkId:null") as FieldTermNode;
        node!.MatchType.Should().Be(MatchType.IsNull);
    }

    // ── Quoted strings ─────────────────────────────────────────────────────

    [Fact]
    public void Single_quoted_string_is_case_insensitive()
    {
        var node = Parse("ChunkId:'ABC'") as FieldTermNode;
        node!.CaseSensitive.Should().BeFalse();
        node.Value.Should().BeOfType<StringValue>().Which.Value.Should().Be("ABC");
    }

    [Fact]
    public void Double_quoted_string_is_case_sensitive()
    {
        var node = Parse("ChunkId:\"ABC\"") as FieldTermNode;
        node!.CaseSensitive.Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<FieldTermNode> CollectLeaves(FilterNode node)
    {
        var result = new List<FieldTermNode>();
        Collect(node, result);
        return result;
    }

    // ── Date parsing ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-10m")]
    [InlineData("-2h")]
    [InlineData("-7d")]
    [InlineData("-1w")]
    public void Negative_relative_offset_parses_as_now_minus_N(string input)
    {
        var before = DateTime.UtcNow;
        var node   = Parse($"updated:>{input}") as FieldTermNode;
        var after  = DateTime.UtcNow;

        node.Should().NotBeNull();
        var dv = node!.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Should().BeOnOrAfter(before.AddDays(-8)).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Positive_minutes_offset_parses_as_today_plus_minutes()
    {
        var today = DateTime.UtcNow.Date;
        var node  = Parse("updated:<10m") as FieldTermNode;
        node.Should().NotBeNull();
        var dv = node!.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Should().Be(today.AddMinutes(10));
    }

    [Fact]
    public void Positive_hours_offset_parses_as_today_plus_hours()
    {
        var today = DateTime.UtcNow.Date;
        var node  = Parse("updated:<2h") as FieldTermNode;
        node.Should().NotBeNull();
        var dv = node!.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Should().Be(today.AddHours(2));
    }

    [Fact]
    public void Positive_days_offset_parses_as_today_plus_days()
    {
        var today = DateTime.UtcNow.Date;
        var node  = Parse("updated:<1d") as FieldTermNode;
        node.Should().NotBeNull();
        var dv = node!.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Should().Be(today.AddDays(1));
    }

    [Fact]
    public void Time_only_parses_as_today_plus_time()
    {
        var today = DateTime.UtcNow.Date;
        var node  = Parse("updated:<00:10") as FieldTermNode;

        node.Should().NotBeNull();
        var dv = node!.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Should().Be(today.AddMinutes(10));
    }

    [Fact]
    public void Positive_10m_and_time_00_10_resolve_to_same_value()
    {
        var n1 = (Parse("updated:<10m")   as FieldTermNode)!.Value as DateValue;
        var n2 = (Parse("updated:<00:10") as FieldTermNode)!.Value as DateValue;

        n1.Should().NotBeNull();
        n2.Should().NotBeNull();
        n1!.Value.Should().BeCloseTo(n2!.Value, TimeSpan.FromSeconds(1));
    }

    // ── Date ranges ────────────────────────────────────────────────────────

    [Fact]
    public void ISO_date_range_produces_Between_with_DateValues()
    {
        var node = Parse("updated:2024-01-01..2024-12-31") as FieldTermNode;
        node.Should().NotBeNull();
        node!.MatchType.Should().Be(MatchType.Between);
        var rv = node.Value.Should().BeOfType<RangeValue>().Subject;
        rv.Low.Should().BeOfType<DateValue>().Which.Value.Should().Be(new DateTime(2024, 1, 1));
        rv.High.Should().BeOfType<DateValue>().Which.Value.Should().Be(new DateTime(2024, 12, 31));
    }

    [Fact]
    public void Relative_date_range_produces_Between_with_DateValues()
    {
        var before = DateTime.UtcNow;
        var node   = Parse("updated:-7d..-1d") as FieldTermNode;
        var after  = DateTime.UtcNow;

        node.Should().NotBeNull();
        node!.MatchType.Should().Be(MatchType.Between);
        var rv = node.Value.Should().BeOfType<RangeValue>().Subject;
        rv.Low.Should().BeOfType<DateValue>().Which.Value
            .Should().BeCloseTo(before.AddDays(-7), TimeSpan.FromSeconds(2));
        rv.High.Should().BeOfType<DateValue>().Which.Value
            .Should().BeCloseTo(before.AddDays(-1), TimeSpan.FromSeconds(2));
    }

    // ── Numeric range with decimals ────────────────────────────────────────

    [Fact]
    public void Numeric_range_with_decimal_boundaries()
    {
        var node = Parse("ChunkId:0.5..9.5") as FieldTermNode;
        node.Should().NotBeNull();
        node!.MatchType.Should().Be(MatchType.Between);
        var rv = node.Value.Should().BeOfType<RangeValue>().Subject;
        rv.Low.Should().BeOfType<NumberValue>().Which.Value.Should().BeApproximately(0.5, 0.001);
        rv.High.Should().BeOfType<NumberValue>().Which.Value.Should().BeApproximately(9.5, 0.001);
    }

    // ── Double NOT ────────────────────────────────────────────────────────

    [Fact]
    public void Double_NOT_produces_nested_NotNodes()
    {
        var node  = Parse("!!Service:svcA");
        var outer = node.Should().BeOfType<NotNode>().Subject;
        var inner = outer.Operand.Should().BeOfType<NotNode>().Subject;
        inner.Operand.Should().BeOfType<FieldTermNode>();
    }

    // ── Three-way OR ──────────────────────────────────────────────────────

    [Fact]
    public void Three_way_OR_collects_three_leaf_nodes()
    {
        var node   = Parse("Service:a, Service:b, Service:c");
        var leaves = CollectLeaves(node!);
        leaves.Should().HaveCount(3)
              .And.AllSatisfy(l => l.Field.Should().Be("Service"));
    }

    // ── Bare wildcard → Glob ───────────────────────────────────────────────

    [Fact]
    public void Bare_wildcard_expands_to_Glob_match_type()
    {
        var leaves = CollectLeaves(Parse("chk-*")!);
        leaves.Should().AllSatisfy(l => l.MatchType.Should().Be(MatchType.Glob));
        leaves.Select(l => l.Field).Should().BeEquivalentTo(Fields);
    }

    // ── ISO date with time component ──────────────────────────────────────

    [Fact]
    public void ISO_date_with_time_component_parses_to_DateValue()
    {
        var node = Parse("updated:>2024-06-15T12:00z") as FieldTermNode;
        node.Should().NotBeNull();
        node!.MatchType.Should().Be(MatchType.GreaterThan);
        var dv = node.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Date.Should().Be(new DateTime(2024, 6, 15));
    }

    // ── Empty double-quoted string ─────────────────────────────────────────

    [Fact]
    public void Empty_double_quoted_string_is_case_sensitive_StringValue()
    {
        var node = Parse("ChunkId:\"\"") as FieldTermNode;
        node.Should().NotBeNull();
        node!.Value.Should().BeOfType<StringValue>().Which.Value.Should().Be(string.Empty);
        node.CaseSensitive.Should().BeTrue();
    }

    // ── Time-only with seconds ─────────────────────────────────────────────

    [Fact]
    public void Time_only_with_seconds_parses_to_DateValue()
    {
        var today = DateTime.UtcNow.Date;
        var node  = Parse("updated:<01:30:45") as FieldTermNode;
        node.Should().NotBeNull();
        var dv = node!.Value.Should().BeOfType<DateValue>().Subject;
        dv.Value.Should().Be(today.AddHours(1).AddMinutes(30).AddSeconds(45));
    }

    // ── AND > OR precedence ────────────────────────────────────────────────

    [Fact]
    public void AND_groups_left_side_of_OR()
    {
        // "A B, C" should parse as "(A AND B) OR C" — root is OrNode
        var node = Parse("Service:a Pipeline:b, Service:c");
        node.Should().BeOfType<OrNode>();
        // The left child of OR should be the AND of the first two terms
        var or = (OrNode)node!;
        or.Left.Should().BeOfType<AndNode>();
        or.Right.Should().BeOfType<FieldTermNode>();
    }

    // ── NOT applied to compound rhs ────────────────────────────────────────

    [Fact]
    public void NOT_on_field_term_is_not_double_negated_by_AND()
    {
        // "!Service:svcA Pipeline:b" = (NOT Service:svcA) AND Pipeline:b — root is AndNode
        var node = Parse("!Service:svcA Pipeline:b");
        node.Should().BeOfType<AndNode>();
        var and = (AndNode)node!;
        and.Left.Should().BeOfType<NotNode>();
        and.Right.Should().BeOfType<FieldTermNode>();
    }

    private static void Collect(FilterNode node, List<FieldTermNode> acc)
    {
        switch (node)
        {
            case FieldTermNode f: acc.Add(f); break;
            case OrNode or:  Collect(or.Left, acc);  Collect(or.Right, acc); break;
            case AndNode and: Collect(and.Left, acc); Collect(and.Right, acc); break;
            case NotNode not: Collect(not.Operand, acc); break;
        }
    }
}
