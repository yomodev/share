using NxtUI.Filtering;
using AwesomeAssertions;

namespace NxtUI.Tests.Filtering;

public class SqlFilterBuilderTests
{
    private static readonly FilterParser Parser = new(["RunId"]);

    private static (string Sql, List<Microsoft.Data.SqlClient.SqlParameter> Params) Build(string filterText)
    {
        var node = Parser.Parse(filterText);
        return SqlFilterBuilder.Build(node);
    }

    // ── Exact equality on Number/Date — the "empty clause" bug ──────────────

    [Fact]
    public void Bare_number_produces_real_equality_clause_not_empty()
    {
        var (sql, ps) = Build("RequestId:34568");
        sql.Should().NotBeEmpty();
        sql.Should().Contain("=");
        ps.Should().ContainSingle().Which.Value.Should().Be(34568.0);
    }

    [Fact]
    public void Bare_date_produces_real_equality_clause_not_empty()
    {
        var (sql, ps) = Build("StartTime:2024-06-15");
        sql.Should().NotBeEmpty();
        sql.Should().Contain("=");
        ps.Should().ContainSingle();
    }

    // ── Boolean equality ─────────────────────────────────────────────────────

    [Fact]
    public void Bare_bool_true_produces_equality_clause()
    {
        var parser = new FilterParser(["IsOnline"]);
        var node   = parser.Parse("IsOnline:true");
        var (sql, ps) = SqlFilterBuilder.Build(node);
        sql.Should().Contain("=");
        ps.Should().ContainSingle().Which.Value.Should().Be(true);
    }

    [Fact]
    public void Bare_bool_false_produces_equality_clause()
    {
        var parser = new FilterParser(["IsOnline"]);
        var node   = parser.Parse("IsOnline:false");
        var (sql, ps) = SqlFilterBuilder.Build(node);
        ps.Should().ContainSingle().Which.Value.Should().Be(false);
    }

    // ── Numeric comparisons still work ──────────────────────────────────────

    [Fact]
    public void Greater_than_produces_comparison_clause()
    {
        var (sql, ps) = Build("RequestId:>100");
        sql.Should().Contain(">");
        ps.Should().ContainSingle().Which.Value.Should().Be(100.0);
    }

    // ── NOT wraps any leaf, including bool ───────────────────────────────────

    [Fact]
    public void NOT_wraps_bool_equality()
    {
        var parser = new FilterParser(["IsOnline"]);
        var node   = parser.Parse("!IsOnline:true");
        var (sql, _) = SqlFilterBuilder.Build(node);
        sql.Should().StartWith("NOT");
    }
}
