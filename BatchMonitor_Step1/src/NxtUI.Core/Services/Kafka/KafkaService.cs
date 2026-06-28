using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Kafka;

public class KafkaService : IKafkaService
{
    private readonly KafkaConnection       _connection;
    private readonly KafkaSettings         _settings;
    private readonly ILogger<KafkaService> _log;

    public KafkaService(KafkaConnection connection, IOptions<KafkaSettings> settings, ILogger<KafkaService> log)
    {
        _connection = connection;
        _settings   = settings.Value;
        _log        = log;
    }

    // ── IKafkaMonitor ───────────────────────────────────────────────────────

    public Task<KafkaClusterInfo> GetClusterInfoAsync(string env, CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: getting cluster info", env);
        using var admin = BuildAdmin();
        var meta = admin.GetMetadata(TimeSpan.FromSeconds(10));

        var info = new KafkaClusterInfo
        {
            ClusterId    = meta.OriginatingBrokerId.ToString(),
            ControllerId = meta.OriginatingBrokerId,
            Brokers = meta.Brokers.Select(b => new KafkaBroker
            {
                Id       = b.BrokerId,
                Host     = b.Host,
                Port     = b.Port,
                IsOnline = true,
            }).ToList(),
        };
        return Task.FromResult(info);
    }

    public Task<IReadOnlyList<KafkaTopicSummary>> GetTopicsAsync(string env, CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: listing topics", env);
        using var admin = BuildAdmin();
        var meta   = admin.GetMetadata(TimeSpan.FromSeconds(10));

        IReadOnlyList<KafkaTopicSummary> result = meta.Topics
            .Where(t => !t.Error.IsError && !t.Topic.StartsWith("__"))
            .Select(t => new KafkaTopicSummary
            {
                Name              = t.Topic,
                PartitionCount    = t.Partitions.Count,
                ReplicationFactor = t.Partitions.FirstOrDefault()?.Replicas.Length ?? 1,
                MessageCount      = 0,
                CleanupPolicy     = "delete",
                RetentionMs       = 604_800_000,
            })
            .OrderBy(t => t.Name)
            .ToList();

        return Task.FromResult(result);
    }

    public async Task<KafkaTopicConfig> GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: getting config for topic '{Topic}'", env, topicName);
        using var admin = BuildAdmin();

        var results = await admin.DescribeConfigsAsync(
            [new ConfigResource { Type = ResourceType.Topic, Name = topicName }]);

        var cfg  = results.First();
        long Lng(string key, long def) =>
            cfg.Entries.TryGetValue(key, out var e) && long.TryParse(e.Value, out var v) ? v : def;
        int Int(string key, int def) =>
            cfg.Entries.TryGetValue(key, out var e) && int.TryParse(e.Value, out var v) ? v : def;
        string Str(string key, string def) =>
            cfg.Entries.TryGetValue(key, out var e) ? e.Value : def;

        var meta      = admin.GetMetadata(topicName, TimeSpan.FromSeconds(10));
        var topicMeta = meta.Topics.FirstOrDefault(t => t.Topic == topicName);

        return new KafkaTopicConfig
        {
            RetentionMs       = Lng("retention.ms",        604_800_000),
            CleanupPolicy     = Str("cleanup.policy",      "delete"),
            MaxMessageBytes   = Lng("max.message.bytes",   1_048_576),
            MinInSyncReplicas = Int("min.insync.replicas", 1),
            CompressionType   = Str("compression.type",    "producer"),
            PartitionCount    = topicMeta?.Partitions.Count ?? 0,
            ReplicationFactor = topicMeta?.Partitions.FirstOrDefault()?.Replicas.Length ?? 1,
        };
    }

    public async Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(
        string env, string topicName, CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: getting consumer groups for topic '{Topic}'", env, topicName);
        using var admin = BuildAdmin();

        var listing = await admin.ListConsumerGroupsAsync();
        var groupIds = listing.Valid.Select(g => g.GroupId).ToList();
        if (groupIds.Count == 0) return [];

        var described = await admin.DescribeConsumerGroupsAsync(groupIds);
        var result    = new List<KafkaTopicConsumerGroup>();

        foreach (var desc in described.ConsumerGroupDescriptions)
        {
            var assignedToTopic = desc.Members
                .Any(m => m.Assignment.TopicPartitions.Any(tp => tp.Topic == topicName));

            if (!assignedToTopic) continue;

            var lag = await ComputeGroupLagForTopicAsync(desc.GroupId, topicName, ct);
            result.Add(new KafkaTopicConsumerGroup
            {
                GroupId  = desc.GroupId,
                State    = desc.State.ToString(),
                TotalLag = lag,
            });
        }

        return result;
    }

    public async IAsyncEnumerable<KafkaMessage> TailTopicAsync(
        string env, string topicName, int maxMessages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: tailing topic '{Topic}' maxMessages={Max}", env, topicName, maxMessages);

        bool live   = maxMessages <= 0;
        var groupId = $"nxtui-tail-{Guid.NewGuid():N}";
        var config  = _connection.BuildConsumerConfig(groupId);
        config.EnableAutoCommit   = false;
        config.AutoOffsetReset    = live ? AutoOffsetReset.Latest : AutoOffsetReset.Earliest;
        config.EnablePartitionEof = true;

        using var admin = BuildAdmin();
        var meta      = admin.GetMetadata(topicName, TimeSpan.FromSeconds(10));
        var partCount = meta.Topics.FirstOrDefault(t => t.Topic == topicName)?.Partitions.Count ?? 1;

        using var consumer = new ConsumerBuilder<string?, string>(config).Build();
        consumer.Subscribe(topicName);

        int consumed = 0;
        int eofCount = 0;

        while (!ct.IsCancellationRequested && (live || consumed < maxMessages))
        {
            ConsumeResult<string?, string>? cr;
            try { cr = consumer.Consume(TimeSpan.FromMilliseconds(500)); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "kafka: consume error"); break; }

            if (cr is null) continue;

            if (cr.IsPartitionEOF)
            {
                if (!live && ++eofCount >= partCount) break;
                continue;
            }

            var headers = new Dictionary<string, string>();
            foreach (var h in cr.Message.Headers)
            {
                try { headers[h.Key] = System.Text.Encoding.UTF8.GetString(h.GetValueBytes() ?? []); }
                catch { /* skip non-UTF8 */ }
            }

            yield return new KafkaMessage
            {
                Offset    = cr.Offset.Value,
                Partition = cr.Partition.Value,
                Key       = cr.Message.Key,
                Value     = cr.Message.Value ?? string.Empty,
                Timestamp = cr.Message.Timestamp.UtcDateTime,
                Headers   = headers,
            };

            consumed++;
        }

        consumer.Close();
    }

    public async Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(
        string env, CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: listing all consumer groups", env);
        using var admin = BuildAdmin();

        var listing  = await admin.ListConsumerGroupsAsync();
        var groupIds = listing.Valid.Select(g => g.GroupId).ToList();
        if (groupIds.Count == 0) return [];

        var described = await admin.DescribeConsumerGroupsAsync(groupIds);
        var result    = new List<KafkaConsumerGroupOverview>();

        foreach (var desc in described.ConsumerGroupDescriptions)
        {
            var topics = desc.Members
                .SelectMany(m => m.Assignment.TopicPartitions.Select(tp => tp.Topic))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            var lag = await ComputeGroupTotalLagAsync(desc.GroupId, ct);
            result.Add(new KafkaConsumerGroupOverview
            {
                GroupId    = desc.GroupId,
                State      = desc.State.ToString(),
                TopicCount = topics.Count,
                TotalLag   = lag,
                Topics     = topics,
            });
        }

        return result.OrderBy(g => g.GroupId).ToList();
    }

    public async Task<IReadOnlyList<KafkaGroupTopicLag>> GetGroupTopicLagsAsync(
        string env, string groupId, CancellationToken ct = default)
    {
        _log.LogDebug("kafka [{Env}]: getting lag for group '{Group}'", env, groupId);
        using var admin = BuildAdmin();

        var described = await admin.DescribeConsumerGroupsAsync([groupId]);
        var desc      = described.ConsumerGroupDescriptions.First();

        var topicPartitions = desc.Members
            .SelectMany(m => m.Assignment.TopicPartitions)
            .GroupBy(tp => tp.Topic)
            .Select(g => (Topic: g.Key, PartitionCount: g.Count()))
            .ToList();

        var result = new List<KafkaGroupTopicLag>();
        foreach (var (topic, partCount) in topicPartitions)
        {
            var lag = await ComputeGroupLagForTopicAsync(groupId, topic, ct);
            result.Add(new KafkaGroupTopicLag
            {
                TopicName  = topic,
                Partitions = partCount,
                Lag        = lag,
            });
        }

        return result.OrderBy(x => x.TopicName).ToList();
    }

    // ── IKafkaAdmin ─────────────────────────────────────────────────────────

    public async Task DeleteTopicAsync(string env, string topicName, CancellationToken ct = default)
    {
        _log.LogInformation("kafka [{Env}]: deleting topic '{Topic}'", env, topicName);
        using var admin = BuildAdmin();
        await admin.DeleteTopicsAsync([topicName]);
    }

    public async Task SetTopicRetentionAsync(string env, string topicName, long retentionMs, CancellationToken ct = default)
    {
        _log.LogInformation("kafka [{Env}]: setting retention on '{Topic}' to {Ms}ms", env, topicName, retentionMs);
        using var admin = BuildAdmin();
        await admin.AlterConfigsAsync(new Dictionary<ConfigResource, List<ConfigEntry>>
        {
            [new ConfigResource { Type = ResourceType.Topic, Name = topicName }] =
            [
                new ConfigEntry { Name = "retention.ms", Value = retentionMs.ToString() }
            ]
        });
    }

    public async Task DeleteConsumerGroupAsync(string env, string groupId, CancellationToken ct = default)
    {
        _log.LogInformation("kafka [{Env}]: deleting consumer group '{Group}'", env, groupId);
        using var admin = BuildAdmin();
        await admin.DeleteGroupsAsync([groupId]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private IAdminClient BuildAdmin() =>
        new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _settings.BootstrapServers }).Build();

    private Task<long> ComputeGroupLagForTopicAsync(string groupId, string topicName, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                var consumerCfg  = _connection.BuildConsumerConfig($"{groupId}__lag_{Guid.NewGuid():N}");
                consumerCfg.EnableAutoCommit = false;
                using var consumer = new ConsumerBuilder<Ignore, Ignore>(consumerCfg).Build();

                using var adminMeta = BuildAdmin();
                var meta       = adminMeta.GetMetadata(topicName, TimeSpan.FromSeconds(10));
                var topicMeta  = meta.Topics.FirstOrDefault(t => t.Topic == topicName);
                if (topicMeta is null) return 0L;

                var tps       = topicMeta.Partitions.Select(p => new TopicPartition(topicName, p.PartitionId)).ToList();
                var committed = consumer.Committed(tps, TimeSpan.FromSeconds(10));

                long lag = 0;
                foreach (var tpo in committed)
                {
                    var hi             = consumer.QueryWatermarkOffsets(tpo.TopicPartition, TimeSpan.FromSeconds(5));
                    var committedOff   = tpo.Offset.IsSpecial ? 0 : tpo.Offset.Value;
                    lag               += Math.Max(0, hi.High.Value - committedOff);
                }
                return lag;
            }
            catch { return 0L; }
        }, ct);

    private async Task<long> ComputeGroupTotalLagAsync(string groupId, CancellationToken ct)
    {
        using var admin = BuildAdmin();
        var meta        = admin.GetMetadata(TimeSpan.FromSeconds(10));
        var topics      = meta.Topics.Where(t => !t.Topic.StartsWith("__")).Select(t => t.Topic).ToList();
        long total      = 0;
        foreach (var topic in topics)
            total += await ComputeGroupLagForTopicAsync(groupId, topic, ct);
        return total;
    }
}
