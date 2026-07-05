using System.Collections.Concurrent;
using Confluent.Kafka;
using NxtUI.Core.Configuration;

namespace NxtUI.Core.Services.Kafka;

/// <summary>
/// Builds and caches Kafka client configs per environment.
/// Two environments that share identical broker/auth settings reuse the same
/// <see cref="ClientConfig"/> instance (keyed by settings fingerprint).
/// </summary>
public sealed class KafkaConnectionFactory(EnvironmentConfigLoader loader)
{
    private readonly ConcurrentDictionary<string, ClientConfig> _cache = new();

    public ClientConfig GetClientConfig(string envId)
    {
        var settings = loader.GetKafka(envId);
        var fingerprint = settings.GetFingerprint();
        return _cache.GetOrAdd(fingerprint, _ => Build(settings));
    }

    public AdminClientConfig GetAdminConfig(string envId) =>
        new(GetClientConfig(envId));

    public ConsumerConfig BuildConsumerConfig(string envId, string groupId) =>
        new(GetClientConfig(envId))
        {
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = true,
        };

    private static ClientConfig Build(KafkaSettings s)
    {
        var cfg = new ClientConfig { BootstrapServers = s.BootstrapServers };

        if (s.TlsEnabled)
        {
            cfg.SecurityProtocol = string.IsNullOrWhiteSpace(s.SaslMechanism)
                ? SecurityProtocol.Ssl
                : SecurityProtocol.SaslSsl;

            if (!string.IsNullOrWhiteSpace(s.TlsCertificatePath))
                cfg.SslCertificateLocation = s.TlsCertificatePath;
            if (!string.IsNullOrWhiteSpace(s.TlsKeyPath))
                cfg.SslKeyLocation = s.TlsKeyPath;
            if (!string.IsNullOrWhiteSpace(s.TlsCaPath))
                cfg.SslCaLocation = s.TlsCaPath;
        }

        if (!string.IsNullOrWhiteSpace(s.SaslMechanism))
        {
            cfg.SaslMechanism = Enum.Parse<SaslMechanism>(s.SaslMechanism, ignoreCase: true);
            cfg.SaslUsername = s.SaslUsername;
            cfg.SaslPassword = s.SaslPassword;
        }

        return cfg;
    }
}
