using System.Security.Cryptography;
using System.Text;

namespace NxtUI.Configuration;

public class KafkaSettings
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    // ── TLS / mTLS ───────────────────────────────────────────────────────────

    public bool    TlsEnabled         { get; set; }
    public string? TlsCertificatePath { get; set; }
    public string? TlsKeyPath         { get; set; }
    public string? TlsCaPath          { get; set; }

    // ── SASL ─────────────────────────────────────────────────────────────────

    public string? SaslMechanism { get; set; }
    public string? SaslUsername  { get; set; }
    public string? SaslPassword  { get; set; }

    // ── Deserialization pipeline ──────────────────────────────────────────────

    /// <summary>
    /// Topic pattern → proto type name mappings evaluated in order.
    /// Patterns are globs (* = any chars, ? = one char).
    /// </summary>
    public List<TopicDeserializerRule> TopicDeserializers { get; set; } = [];

    /// <summary>Maximum messages fetched in one streaming session (default 2000).</summary>
    public int MaxFetchMessages { get; set; } = 2_000;

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

    /// <summary>Proto type name registered in <c>MessageRegistry</c>.</summary>
    public string Type { get; set; } = string.Empty;
}
