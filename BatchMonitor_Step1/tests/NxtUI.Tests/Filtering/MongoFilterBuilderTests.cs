using AwesomeAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NxtUI.Core.Filtering;

namespace NxtUI.Tests.Filtering;

public class MongoFilterBuilderTests
{
    private static readonly FilterParser Parser = new(["Name"]);

    private static string Render(string filterText)
    {
        var node = Parser.Parse(filterText);
        var filter = MongoFilterBuilder.Build(node);
        return filter.Render(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry).ToString();
    }

    // ── Exact equality on Number/Date/Bool — the "matches everything" bug ──

    [Fact]
    public void Bare_number_produces_real_equality_filter_not_empty()
    {
        Render("ProcessId:34568").Should().Be("{ \"ProcessId\" : 34568.0 }");
    }

    [Fact]
    public void Bare_date_produces_real_equality_filter_not_empty()
    {
        var rendered = Render("StartTime:2024-06-15");
        rendered.Should().NotBe("{ }");
        rendered.Should().Contain("StartTime");
    }

    [Fact]
    public void Bare_bool_true_produces_equality_filter()
    {
        Render("IsOnline:true").Should().Be("{ \"IsOnline\" : true }");
    }

    [Fact]
    public void Bare_bool_false_produces_equality_filter()
    {
        Render("IsOnline:false").Should().Be("{ \"IsOnline\" : false }");
    }

    [Fact]
    public void Explicit_exact_operator_on_bool_produces_equality_filter()
    {
        Render("IsOnline:=true").Should().Be("{ \"IsOnline\" : true }");
    }

    // ── Numeric comparisons still work ──────────────────────────────────────

    [Fact]
    public void Greater_than_produces_gt_filter()
    {
        Render("ProcessId:>100").Should().Be("{ \"ProcessId\" : { \"$gt\" : 100.0 } }");
    }

    // ── NOT works generically over any leaf, including bool ─────────────────

    [Fact]
    public void NOT_wraps_bool_equality_in_nor()
    {
        var rendered = Render("!IsOnline:true");
        rendered.Should().Contain("IsOnline");
        rendered.Should().Contain("true");
    }

    // ── AND / OR combine bool terms with others ─────────────────────────────

    [Fact]
    public void AND_combines_bool_and_string_terms()
    {
        // MongoDB.Driver's Builders.And flattens same-level conditions on different
        // fields into one document instead of nesting under $and.
        var parser = new FilterParser(["Name", "IsOnline"]);
        var node = parser.Parse("Name:svc IsOnline:true");
        var filter = MongoFilterBuilder.Build(node);
        var rendered = filter.Render(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry).ToString();
        rendered.Should().Contain("IsOnline");
        rendered.Should().Contain("true");
    }

    // ── BuildCollectionNameFilter (MongoDashboard collection-name search) ───────
    // Regression coverage for the raw-regex passthrough this replaced: "!ccr" and
    // "wcr*"/"*str" must use the shared grammar's NOT/glob semantics, not literal
    // regex syntax, and literal regex metacharacters in a name must be escaped.

    private static string RenderCollectionFilter(string? search)
    {
        var filter = MongoFilterBuilder.BuildCollectionNameFilter(search);
        return filter.Render(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry).ToString();
    }

    [Fact]
    public void Null_or_blank_search_produces_empty_filter()
    {
        RenderCollectionFilter(null).Should().Be("{ }");
        RenderCollectionFilter("  ").Should().Be("{ }");
    }

    [Fact]
    public void Plain_bare_term_is_a_case_insensitive_contains_on_name()
    {
        RenderCollectionFilter("ccr").Should()
            .Be("{ \"name\" : /ccr/i }");
    }

    [Fact]
    public void Negated_bare_term_wraps_contains_in_not_instead_of_matching_everything()
    {
        // Previously "!ccr" was passed straight through as a raw regex (where '!' has no
        // special meaning), so it matched names literally containing "!ccr" instead of
        // excluding names that contain "ccr".
        var rendered = RenderCollectionFilter("!ccr");
        rendered.Should().Contain("name");
        rendered.Should().Contain("ccr");
        rendered.Should().Contain("$not");
    }

    [Fact]
    public void Leading_glob_star_anchors_to_starts_with()
    {
        // Previously "wcr*" was raw regex — '*' quantifies the preceding char, not a
        // "starts with" wildcard — so this only coincidentally looked like it worked.
        RenderCollectionFilter("wcr*").Should()
            .Be("{ \"name\" : /^wcr.*$/i }");
    }

    [Fact]
    public void Trailing_glob_star_anchors_to_ends_with()
    {
        RenderCollectionFilter("*str").Should()
            .Be("{ \"name\" : /^.*str$/i }");
    }

    [Fact]
    public void Literal_regex_metacharacters_in_name_are_escaped()
    {
        // Previously the raw regex passthrough let '.' and '+' act as regex metacharacters
        // (any-char / quantifier), so e.g. searching "log.events" would also match
        // "logXevents". The shared grammar escapes literal characters via Regex.Escape.
        RenderCollectionFilter("log.events").Should()
            .Be("{ \"name\" : /log\\.events/i }");
    }

    [Theory]
    [InlineData("name:ccr")]
    [InlineData("collection:ccr")]
    public void Field_prefixed_aliases_resolve_to_the_name_field(string search)
    {
        RenderCollectionFilter(search).Should().Be("{ \"name\" : /ccr/i }");
    }

    [Fact]
    public void Unparseable_input_falls_back_to_a_literal_escaped_contains_match()
    {
        // An unbalanced paren is a parse error in the shared grammar — the search box
        // must never just throw or silently return nothing for odd input.
        RenderCollectionFilter("(ccr").Should()
            .Be("{ \"name\" : /\\(ccr/i }");
    }
}
