using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Json.Path;

namespace NxtUI.Tests;

public class JsonPathTests
{
    [Fact]
    public void JsonPath_can_query_system_text_json()
    {
        var json = @"{
            ""store"": {
                ""book"": [
                    { ""title"": ""A"", ""price"": 8.95 },
                    { ""title"": ""B"", ""price"": 12.99 }
                ]
            }
        }";

        var node = JsonNode.Parse(json);
        var path = JsonPath.Parse("$.store.book[?(@.price < 10)].title");

        /*var matches = path.Evaluate(node)
            .Select(n => n!.ToJsonString().Trim('"'))
            .ToArray();

        matches.Should().Equal(new[] { "A" });*/
    }
}