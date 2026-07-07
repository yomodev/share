using AwesomeAssertions;
using NxtUI.Core.Services;

namespace NxtUI.Tests;

/// <summary>
/// Covers JsonPathQueryService — the shared JsonPath.Net query bar behind the Kafka
/// message and Mongo document inspectors' detail-panel "run a JSONPath" feature.
/// </summary>
public class JsonPathQueryServiceTests
{
    private const string StoreJson = @"{
        ""store"": {
            ""book"": [
                { ""title"": ""A"", ""price"": 8.95 },
                { ""title"": ""B"", ""price"": 12.99 }
            ]
        }
    }";

    [Fact]
    public void Evaluate_filter_expression_returns_only_matching_values()
    {
        var result = JsonPathQueryService.Evaluate(StoreJson, "$.store.book[?(@.price < 10)].title");

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(1);
        result.ResultJson.Should().Be("[\"A\"]");
    }

    [Fact]
    public void Evaluate_wildcard_returns_every_match_as_a_json_array()
    {
        var result = JsonPathQueryService.Evaluate(StoreJson, "$.store.book[*].title");

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(2);
        result.ResultJson.Should().Be("[\"A\",\"B\"]");
    }

    [Fact]
    public void Evaluate_valid_path_with_no_matches_is_success_with_empty_array()
    {
        // A syntactically valid path that selects nothing is not an error — it's
        // meaningfully different from a bad expression, and the UI should render an
        // empty result rather than an error message.
        var result = JsonPathQueryService.Evaluate(StoreJson, "$.store.book[?(@.price > 1000)]");

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.MatchCount.Should().Be(0);
        result.ResultJson.Should().Be("[]");
    }

    [Fact]
    public void Evaluate_invalid_jsonpath_syntax_fails_with_an_error()
    {
        var result = JsonPathQueryService.Evaluate(StoreJson, "$.store.book[");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.ResultJson.Should().BeNull();
    }

    [Fact]
    public void Evaluate_invalid_json_document_fails_with_an_error()
    {
        var result = JsonPathQueryService.Evaluate("{ not valid json", "$.foo");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Evaluate_blank_path_fails_without_touching_the_document()
    {
        var result = JsonPathQueryService.Evaluate(StoreJson, "   ");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
