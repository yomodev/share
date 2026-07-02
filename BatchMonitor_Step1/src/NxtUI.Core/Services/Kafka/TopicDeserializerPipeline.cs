using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;

namespace NxtUI.Core.Services.Kafka;

/// <summary>
/// Deserialises raw Kafka message bytes according to topic pattern rules.
///
/// Resolution order:
///   1. First matching glob pattern from config → try configured proto type
///   2. No match or parse failure → try <c>ProtoMsg</c> envelope
///   3. Both fail → unknown stub JSON with byte size
/// </summary>
public sealed class TopicDeserializerPipeline
{
    private readonly record struct Rule(Regex Pattern, string TypeName);

    private readonly List<Rule>      _rules;
    private readonly IMessageRegistry _registry;

    public TopicDeserializerPipeline(IOptions<KafkaSettings> settings, IMessageRegistry registry)
    {
        _registry = registry;
        _rules    = settings.Value.TopicDeserializers
            .Select(r => new Rule(GlobToRegex(r.Pattern), r.Type))
            .ToList();
    }

    public (string JsonPayload, string PayloadType) Deserialize(string topicName, byte[] bytes)
    {
        // Phase 1 — matched type
        foreach (var rule in _rules)
        {
            if (!rule.Pattern.IsMatch(topicName)) continue;
            if (_registry.TryParseToJson(rule.TypeName, bytes, out var matched))
                return (matched!, rule.TypeName);
            break;
        }

        // Phase 2 — ProtoMsg fallback
        if (_registry.TryParseToJson("ProtoMsg", bytes, out var proto))
            return (proto!, "ProtoMsg");

        // Phase 3 — unknown binary
        return ($"{{\"type\":\"unknown\",\"sizeBytes\":{bytes.Length}}}", "unknown");
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in glob)
            sb.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
