using System.Text.Json;
using System.Text.Json.Serialization;

namespace NxtUI.Models;

public class BatchCatalog
{
    [JsonPropertyName("batches")]
    public List<BatchDefinition> Batches { get; init; } = [];
}

public class BatchDefinition
{
    [JsonPropertyName("id")]          public string Id          { get; init; } = "";
    [JsonPropertyName("name")]        public string Name        { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("category")]    public string Category    { get; init; } = "";
    [JsonPropertyName("tags")]        public List<string> Tags  { get; init; } = [];
    [JsonPropertyName("endpoint")]    public BatchEndpoint Endpoint   { get; init; } = new();
    [JsonPropertyName("parameters")]  public List<BatchParameter> Parameters { get; init; } = [];
}

public class BatchEndpoint
{
    [JsonPropertyName("method")] public string Method { get; init; } = "POST";
    [JsonPropertyName("url")]    public string Url    { get; init; } = "";
}

public class BatchParameter
{
    [JsonPropertyName("key")]         public string Key         { get; init; } = "";
    [JsonPropertyName("label")]       public string Label       { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    /// <summary>string | number | boolean | date | select | multiselect</summary>
    [JsonPropertyName("type")]        public string Type        { get; init; } = "string";
    [JsonPropertyName("default")]     public JsonElement Default { get; init; }
    [JsonPropertyName("required")]    public bool Required      { get; init; }
    [JsonPropertyName("options")]     public List<string> Options { get; init; } = [];
    [JsonPropertyName("min")]         public double? Min        { get; init; }
    [JsonPropertyName("max")]         public double? Max        { get; init; }

    public string DefaultString() => Default.ValueKind switch
    {
        JsonValueKind.String => Default.GetString() ?? "",
        JsonValueKind.Number => Default.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        JsonValueKind.Array  => string.Join(",", Default.EnumerateArray()
                                    .Select(e => e.GetString() ?? "")),
        _                    => ""
    };

    public List<string> DefaultList() => Default.ValueKind == JsonValueKind.Array
        ? Default.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
        : Default.ValueKind == JsonValueKind.String && Default.GetString() is { Length: > 0 } s
            ? [s] : [];
}

public record BatchTriggerResult(
    bool   Success,
    string ResolvedUrl,
    string ResolvedBody,
    string? JobId   = null,
    string? Message = null,
    string? Error   = null);
