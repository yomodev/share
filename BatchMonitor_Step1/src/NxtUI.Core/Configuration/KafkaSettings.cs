namespace NxtUI.Configuration;

/// <summary>
/// Kafka connection settings — bound from appsettings.json "Kafka" section.
/// </summary>
public class KafkaSettings
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    // ── TLS / mTLS ───────────────────────────────────────────────────────────

    public bool    TlsEnabled           { get; set; }
    /// <summary>Path to the client certificate PEM file.</summary>
    public string? TlsCertificatePath   { get; set; }
    /// <summary>Path to the client private key PEM file.</summary>
    public string? TlsKeyPath           { get; set; }
    /// <summary>Path to the CA certificate PEM file (for broker verification).</summary>
    public string? TlsCaPath            { get; set; }

    // ── SASL ─────────────────────────────────────────────────────────────────
    // Supported values for SaslMechanism: Plain, ScramSha256, ScramSha512, GssApi

    public string? SaslMechanism { get; set; }
    public string? SaslUsername  { get; set; }
    public string? SaslPassword  { get; set; }
}
