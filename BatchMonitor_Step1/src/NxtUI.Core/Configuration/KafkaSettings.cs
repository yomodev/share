using System.Security.Cryptography;
using System.Text;

namespace NxtUI.Core.Configuration;

public class KafkaSettings
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    // ── TLS / mTLS ───────────────────────────────────────────────────────────

    public bool TlsEnabled { get; set; }
    public string? TlsCertificatePath { get; set; }
    public string? TlsKeyPath { get; set; }
    public string? TlsCaPath { get; set; }

    // ── SASL ─────────────────────────────────────────────────────────────────

    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }

    // ── Deserialization pipeline ──────────────────────────────────────────────

    /// <summary>
    /// Topic pattern → proto type name mappings evaluated in order.
    /// Patterns are globs (* = any chars, ? = one char).
    /// </summary>
    public List<TopicDeserializerRule> TopicDeserializers { get; set; } = [];

    /// <summary>
    /// Last-chance proto type tried when no pattern matches a topic, or every
    /// candidate for a matched pattern fails to parse. Empty/null disables it
    /// (falls straight through to the raw-bytes stub).
    /// </summary>
    public string? DefaultProtoType { get; set; } = "ProtoMsg";

    /// <summary>
    /// Folder containing .proto schema files, parsed dynamically at runtime
    /// (no generated C# classes). Null/empty uses the folder bundled with
    /// NxtUI.Protos.
    /// </summary>
    public string? ProtoSchemaFolder { get; set; }

    /// <summary>Maximum messages fetched in one streaming session (default 2000).</summary>
    public int MaxFetchMessages { get; set; } = 2_000;

    /// <summary>
    /// Per-environment topic that services publish periodic memory-metrics samples to
    /// (protobuf-encoded MetricsTracker messages), single partition, low retention.
    /// ServiceMetricsMonitor tails this live and only falls back to disk-based log
    /// parsing for history older than the topic's retention window. Default: "metrics".
    /// </summary>
    public string MetricsTopicName { get; set; } = "metrics";

    /// <summary>
    /// Fallback retention (ms) a purge restores a topic to if its current retention.ms
    /// couldn't be read back from the broker before the purge started (should be rare —
    /// DescribeConfigs normally returns the effective value even when it's inherited from
    /// the broker default). Default: 7 days.
    /// </summary>
    public long DefaultTopicRetentionMs { get; set; } = 7 * 24 * 60 * 60 * 1000L;

    // ── Connection fingerprint ────────────────────────────────────────────────

    /// <summary>
    /// Short hash used as cache key by the connection factory.
    /// Two environments with identical connection settings share one client.
    /// </summary>
    public string GetFingerprint()
    {
        var key = $"{BootstrapServers}|{TlsEnabled}|{TlsCertificatePath}|{TlsKeyPath}|{TlsCaPath}" +
                  $"|{SaslMechanism}|{SaslUsername}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..12];
    }
}

public class TopicDeserializerRule
{
    /// <summary>Glob pattern matched against the topic name (case-insensitive).</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Candidate proto type names, tried in order until one parses successfully.
    /// Once a candidate succeeds for a given topic, the pipeline remembers it
    /// and tries that one first on subsequent messages for the same topic.
    /// </summary>
    public List<string> Types { get; set; } = [];
}
