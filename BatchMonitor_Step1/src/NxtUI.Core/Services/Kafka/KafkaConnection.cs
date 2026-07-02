using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;

namespace NxtUI.Core.Services.Kafka;

/// <summary>
/// Centralised Kafka connection factory.
/// Register as singleton; inject wherever a producer or consumer config is needed.
/// </summary>
/// <remarks>
/// TLS / mTLS: set <see cref="KafkaSettings.TlsEnabled"/> to true and point
/// <see cref="KafkaSettings.TlsCertificatePath"/> (PEM) and optionally
/// <see cref="KafkaSettings.TlsKeyPath"/> at your client certificate and key.
/// For SASL/SCRAM or SASL/PLAIN, populate <see cref="KafkaSettings.SaslMechanism"/>,
/// <see cref="KafkaSettings.SaslUsername"/>, and <see cref="KafkaSettings.SaslPassword"/>.
/// </remarks>
public sealed class KafkaConnection(IOptions<KafkaSettings> options)
{
    private readonly KafkaSettings _settings = options.Value;

    /// <summary>Returns a producer config pre-wired with security settings.</summary>
    public ProducerConfig BuildProducerConfig() =>
        Apply(new ProducerConfig { BootstrapServers = _settings.BootstrapServers });

    /// <summary>Returns a consumer config pre-wired with security settings.</summary>
    public ConsumerConfig BuildConsumerConfig(string groupId) =>
        Apply(new ConsumerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            GroupId          = groupId,
            AutoOffsetReset  = AutoOffsetReset.Earliest,
        });

    // ── internals ────────────────────────────────────────────────────────────

    private T Apply<T>(T cfg) where T : ClientConfig
    {
        if (_settings.TlsEnabled)
        {
            cfg.SecurityProtocol = string.IsNullOrWhiteSpace(_settings.SaslMechanism)
                ? SecurityProtocol.Ssl
                : SecurityProtocol.SaslSsl;

            if (!string.IsNullOrWhiteSpace(_settings.TlsCertificatePath))
                cfg.SslCertificateLocation = _settings.TlsCertificatePath;

            if (!string.IsNullOrWhiteSpace(_settings.TlsKeyPath))
                cfg.SslKeyLocation = _settings.TlsKeyPath;

            if (!string.IsNullOrWhiteSpace(_settings.TlsCaPath))
                cfg.SslCaLocation = _settings.TlsCaPath;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SaslMechanism))
        {
            cfg.SaslMechanism = Enum.Parse<SaslMechanism>(_settings.SaslMechanism, ignoreCase: true);
            cfg.SaslUsername  = _settings.SaslUsername;
            cfg.SaslPassword  = _settings.SaslPassword;
        }

        return cfg;
    }
}
