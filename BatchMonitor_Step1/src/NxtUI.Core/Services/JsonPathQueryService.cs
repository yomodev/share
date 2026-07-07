using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;

namespace NxtUI.Core.Services;

/// <summary>
/// Result of evaluating a JSONPath (RFC 9535) expression against a JSON document via
/// JsonPath.Net. On success, ResultJson is a JSON array of every matched value (even for
/// a single match) so callers always get one shape to render — matches an empty array,
/// not an error, when the path is syntactically valid but selects nothing.
/// </summary>
public sealed record JsonPathQueryResult(bool Success, string? ResultJson, string? Error, int MatchCount)
{
    public static JsonPathQueryResult Ok(string resultJson, int matchCount) => new(true, resultJson, null, matchCount);
    public static JsonPathQueryResult Fail(string error) => new(false, null, error, 0);
}

/// <summary>
/// Runs a JSONPath query against a raw JSON string — shared by the Kafka message
/// inspector and the Mongo document inspector's detail-panel query bar.
/// </summary>
public static class JsonPathQueryService
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = false };

    public static JsonPathQueryResult Evaluate(string? json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return JsonPathQueryResult.Fail("Enter a JSONPath expression, e.g. $.store.book[*].title");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json ?? "null");
        }
        catch (JsonException ex)
        {
            return JsonPathQueryResult.Fail($"Invalid JSON: {ex.Message}");
        }

        if (root is null)
            return JsonPathQueryResult.Fail("Document is empty.");

        JsonPath parsed;
        try
        {
            parsed = JsonPath.Parse(path);
        }
        catch (PathParseException ex)
        {
            return JsonPathQueryResult.Fail($"Invalid JSONPath: {ex.Message}");
        }

        PathResult result;
        try
        {
            result = parsed.Evaluate(root);
        }
        catch (Exception ex)
        {
            // JsonPath.Net can throw at evaluation time too (e.g. a function called
            // with the wrong argument types) — not just at parse time.
            return JsonPathQueryResult.Fail($"Query failed: {ex.Message}");
        }

        var matches = result.Matches;
        if (matches is null || matches.Count == 0)
            return JsonPathQueryResult.Ok("[]", 0);

        // Matched nodes are still attached to `root` — JsonNode instances can only have
        // one parent, so they must be cloned before going into a new JsonArray.
        var array = new JsonArray();
        foreach (var match in matches)
            array.Add(match.Value?.DeepClone());

        return JsonPathQueryResult.Ok(array.ToJsonString(SerializeOptions), matches.Count);
    }
}
