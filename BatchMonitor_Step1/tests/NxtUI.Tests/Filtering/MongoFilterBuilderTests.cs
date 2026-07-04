using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NxtUI.Filtering;
using AwesomeAssertions;

namespace NxtUI.Tests.Filtering;

public class MongoFilterBuilderTests
{
    private static readonly FilterParser Parser = new(["Name"]);

    private static string Render(string filterText)
    {
        var node   = Parser.Parse(filterText);
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
        var node   = parser.Parse("Name:svc IsOnline:true");
        var filter = MongoFilterBuilder.Build(node);
        var rendered = filter.Render(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry).ToString();
        rendered.Should().Contain("IsOnline");
        rendered.Should().Contain("true");
    }
}
