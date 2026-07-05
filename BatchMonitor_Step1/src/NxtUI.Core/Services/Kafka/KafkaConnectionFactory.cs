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

    // GetClientConfig returns a cached, shared ClientConfig instance per environment
    // fingerprint. Confluent.Kafka's Config(Config) copy constructor shares the underlying
    // properties dictionary rather than deep-copying it — building a ConsumerConfig/
    // AdminClientConfig straight from the shared instance and then setting properties on
    // it (GroupId, EnableAutoCommit, ...) mutates that same shared dictionary, permanently
    // leaking consumer-only properties into every other client built from it afterward
    // (surfaces as librdkafka warnings like "Configuration property group.id is a consumer
    // property and will be ignored by this producer instance"). Wrapping in a fresh
    // Dictionary first forces an independent copy regardless of Config's sharing behavior.
    public AdminClientConfig GetAdminConfig(string envId) =>
        new(new Dictionary<string, string>(GetClientConfig(envId)));

    public ConsumerConfig BuildConsumerConfig(string envId, string groupId) =>
        new(new Dictionary<string, string>(GetClientConfig(envId)))
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
