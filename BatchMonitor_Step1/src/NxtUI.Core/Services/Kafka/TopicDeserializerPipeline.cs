using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;

namespace NxtUI.Core.Services.Kafka;

/// <summary>
/// Deserialises raw Kafka message bytes according to topic pattern rules.
///
/// Resolution order:
///   1. If this topic previously resolved to a working type, try that first.
///   2. First matching glob pattern from config → try its candidate types in
///      order until one parses successfully; remember the winner for this topic.
///   3. No match or all candidates failed → try the configured default type.
///   4. That fails too → unknown stub JSON with byte size.
/// </summary>
public sealed class TopicDeserializerPipeline
{
    private readonly record struct Rule(Regex Pattern, List<string> TypeNames);

    private readonly List<Rule>       _rules;
    private readonly IMessageRegistry _registry;
    private readonly string?          _defaultType;

    // Per-topic cache of the last type that successfully parsed a message —
    // avoids re-trying every candidate on every message once one is known good.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resolvedType = new();

    public TopicDeserializerPipeline(IOptions<KafkaSettings> settings, IMessageRegistry registry)
    {
        _registry    = registry;
        _defaultType = settings.Value.DefaultProtoType;
        _rules       = settings.Value.TopicDeserializers
            .Select(r => new Rule(GlobToRegex(r.Pattern), r.Types))
            .ToList();
    }

    public (string JsonPayload, string PayloadType) Deserialize(string topicName, byte[] bytes)
    {
        // Phase 0 — a type already known to work for this topic
        if (_resolvedType.TryGetValue(topicName, out var known) &&
            _registry.TryParseToJson(known, bytes, out var knownJson))
            return (knownJson!, known);

        // Phase 1 — first matching glob pattern, try its candidates in order
        foreach (var rule in _rules)
        {
            if (!rule.Pattern.IsMatch(topicName)) continue;
            foreach (var typeName in rule.TypeNames)
            {
                if (!_registry.TryParseToJson(typeName, bytes, out var matched)) continue;
                _resolvedType[topicName] = typeName;
                return (matched!, typeName);
            }
            break; // matched a pattern but every candidate failed — fall through to default
        }

        // Phase 2 — configured default/last-chance type
        if (!string.IsNullOrEmpty(_defaultType) &&
            _registry.TryParseToJson(_defaultType, bytes, out var proto))
            return (proto!, _defaultType);

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
