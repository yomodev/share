using BatchMonitor.Core.Models;
using BatchMonitor.Core.Services;

namespace BatchMonitor.Services;

public class MockKafkaService : IKafkaService
{
    private static readonly KafkaClusterInfo _cluster = new()
    {
        ClusterId    = "mock-cluster-a1b2c3",
        ControllerId = 1,
        Brokers =
        [
            new() { Id = 1, Host = "kafka-1", Port = 9092, IsOnline = true  },
            new() { Id = 2, Host = "kafka-2", Port = 9092, IsOnline = true  },
            new() { Id = 3, Host = "kafka-3", Port = 9092, IsOnline = false },
        ]
    };

    private static readonly IReadOnlyList<KafkaTopicSummary> _topics =
    [
        new() { Name = "bm.orders",           PartitionCount = 12, ReplicationFactor = 3, MessageCount = 1_842_301, CleanupPolicy = "delete",          RetentionMs =  7 * 86_400_000L },
        new() { Name = "bm.invoices",         PartitionCount =  6, ReplicationFactor = 3, MessageCount =   422_018, CleanupPolicy = "delete",          RetentionMs =  7 * 86_400_000L },
        new() { Name = "bm.shipments",        PartitionCount =  6, ReplicationFactor = 3, MessageCount =   310_452, CleanupPolicy = "delete",          RetentionMs = 14 * 86_400_000L },
        new() { Name = "bm.payments",         PartitionCount = 12, ReplicationFactor = 3, MessageCount =   956_712, CleanupPolicy = "delete",          RetentionMs = 30 * 86_400_000L },
        new() { Name = "bm.returns",          PartitionCount =  3, ReplicationFactor = 3, MessageCount =    45_230, CleanupPolicy = "delete",          RetentionMs =  7 * 86_400_000L },
        new() { Name = "bm.orders.dlq",       PartitionCount =  3, ReplicationFactor = 3, MessageCount =       127, CleanupPolicy = "delete",          RetentionMs =  3 * 86_400_000L },
        new() { Name = "bm.invoices.dlq",     PartitionCount =  3, ReplicationFactor = 3, MessageCount =         8, CleanupPolicy = "delete",          RetentionMs =  3 * 86_400_000L },
        new() { Name = "bm.payments.dlq",     PartitionCount =  3, ReplicationFactor = 3, MessageCount =        42, CleanupPolicy = "delete",          RetentionMs =  3 * 86_400_000L },
        new() { Name = "bm.audit",            PartitionCount =  6, ReplicationFactor = 3, MessageCount = 4_201_887, CleanupPolicy = "compact",         RetentionMs = -1               },
        new() { Name = "bm.notifications",    PartitionCount =  6, ReplicationFactor = 3, MessageCount =   892_340, CleanupPolicy = "delete",          RetentionMs =      86_400_000L },
        new() { Name = "bm.events.raw",       PartitionCount = 24, ReplicationFactor = 3, MessageCount = 8_741_002, CleanupPolicy = "delete",          RetentionMs =  2 * 86_400_000L },
        new() { Name = "bm.events.enriched",  PartitionCount = 12, ReplicationFactor = 3, MessageCount = 8_736_451, CleanupPolicy = "delete",          RetentionMs =  2 * 86_400_000L },
        new() { Name = "bm.reports",          PartitionCount =  3, ReplicationFactor = 3, MessageCount =    12_018, CleanupPolicy = "delete",          RetentionMs = 30 * 86_400_000L },
        new() { Name = "bm.analytics",        PartitionCount =  6, ReplicationFactor = 3, MessageCount =   234_109, CleanupPolicy = "compact",         RetentionMs = -1               },
        new() { Name = "bm.metrics",          PartitionCount = 12, ReplicationFactor = 3, MessageCount =15_302_441, CleanupPolicy = "delete",          RetentionMs =      86_400_000L },
        new() { Name = "bm.alerts",           PartitionCount =  3, ReplicationFactor = 3, MessageCount =       891, CleanupPolicy = "delete",          RetentionMs =  7 * 86_400_000L },
        new() { Name = "bm.config",           PartitionCount =  1, ReplicationFactor = 3, MessageCount =        45, CleanupPolicy = "compact",         RetentionMs = -1               },
        new() { Name = "bm.customer-profiles",PartitionCount =  6, ReplicationFactor = 3, MessageCount =    78_334, CleanupPolicy = "compact,delete",  RetentionMs = 90 * 86_400_000L },
    ];

    public Task<KafkaClusterInfo> GetClusterInfoAsync(string env, CancellationToken ct = default)
        => Task.FromResult(_cluster);

    public Task<IReadOnlyList<KafkaTopicSummary>> GetTopicsAsync(string env, CancellationToken ct = default)
        => Task.FromResult(_topics);

    public Task<KafkaTopicConfig> GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default)
    {
        var t = _topics.FirstOrDefault(x => x.Name == topicName);
        var cfg = new KafkaTopicConfig
        {
            RetentionMs       = t?.RetentionMs       ?? 604_800_000,
            CleanupPolicy     = t?.CleanupPolicy     ?? "delete",
            MaxMessageBytes   = topicName.Contains("events") ? 5_242_880 : 1_048_576,
            MinInSyncReplicas = 2,
            CompressionType   = topicName.Contains("metrics") ? "lz4" : "producer",
            PartitionCount    = t?.PartitionCount    ?? 6,
            ReplicationFactor = t?.ReplicationFactor ?? 3,
        };
        return Task.FromResult(cfg);
    }

    private static readonly Dictionary<string, IReadOnlyList<KafkaTopicConsumerGroup>> _topicGroups = new()
    {
        ["bm.orders"]          = [ new() { GroupId = "svc-orders-consumer",  State = "Stable",      TotalLag = 0  },
                                   new() { GroupId = "svc-orders-retry",     State = "Stable",      TotalLag = 12 },
                                   new() { GroupId = "svc-orders-archive",   State = "Dead",        TotalLag = 0  } ],
        ["bm.invoices"]        = [ new() { GroupId = "svc-invoices-consumer",State = "Stable",      TotalLag = 0  },
                                   new() { GroupId = "svc-invoices-retry",   State = "Rebalancing", TotalLag = 5  } ],
        ["bm.payments"]        = [ new() { GroupId = "svc-payments-consumer",State = "Stable",      TotalLag = 0  } ],
        ["bm.events.raw"]      = [ new() { GroupId = "svc-enricher",         State = "Stable",      TotalLag = 241 },
                                   new() { GroupId = "svc-analytics",        State = "Stable",      TotalLag = 0  } ],
        ["bm.events.enriched"] = [ new() { GroupId = "svc-loader",           State = "Stable",      TotalLag = 0  } ],
        ["bm.metrics"]         = [ new() { GroupId = "svc-metrics-consumer", State = "Stable",      TotalLag = 0  } ],
        ["bm.audit"]           = [ new() { GroupId = "svc-audit-reader",     State = "Empty",       TotalLag = 0  } ],
        ["bm.notifications"]   = [ new() { GroupId = "svc-notifier",         State = "Stable",      TotalLag = 3  } ],
    };

    public Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(string env, string topicName, CancellationToken ct = default)
    {
        var result = _topicGroups.TryGetValue(topicName, out var groups)
            ? groups
            : (IReadOnlyList<KafkaTopicConsumerGroup>)[];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(string env, CancellationToken ct = default)
    {
        // invert _topicGroups: group → list of topics with their lag
        var byGroup = new Dictionary<string, (string State, long Lag, List<string> Topics)>();
        foreach (var (topic, groups) in _topicGroups)
        {
            foreach (var g in groups)
            {
                if (!byGroup.TryGetValue(g.GroupId, out var entry))
                    entry = (g.State, 0, new List<string>());
                byGroup[g.GroupId] = (g.State, entry.Lag + g.TotalLag, entry.Topics.Append(topic).ToList());
            }
        }
        IReadOnlyList<KafkaConsumerGroupOverview> result = byGroup
            .Select(kv => new KafkaConsumerGroupOverview
            {
                GroupId    = kv.Key,
                State      = kv.Value.State,
                TopicCount = kv.Value.Topics.Count,
                TotalLag   = kv.Value.Lag,
                Topics     = kv.Value.Topics,
            })
            .OrderBy(g => g.GroupId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<KafkaGroupTopicLag>> GetGroupTopicLagsAsync(string env, string groupId, CancellationToken ct = default)
    {
        IReadOnlyList<KafkaGroupTopicLag> result = _topicGroups
            .Where(kv => kv.Value.Any(g => g.GroupId == groupId))
            .Select(kv =>
            {
                var g       = kv.Value.First(g => g.GroupId == groupId);
                var topic   = _topics.FirstOrDefault(t => t.Name == kv.Key);
                return new KafkaGroupTopicLag
                {
                    TopicName  = kv.Key,
                    Partitions = topic?.PartitionCount ?? 3,
                    Lag        = g.TotalLag,
                };
            })
            .OrderBy(x => x.TopicName)
            .ToList();
        return Task.FromResult(result);
    }

    private static readonly Random _rng = new(42);
    private static readonly string[] _keyPrefixes = ["order", "invoice", "payment", "user", "event", "cmd", "reply", "req"];
    private static readonly string[] _valueTemplates =
    [
        """{"id":"{key}-{n}","status":"CREATED","amount":{amount},"ts":"{ts}"}""",
        """{"eventType":"UPDATED","entityId":"{key}-{n}","payload":{"field":"value_{n}"}}""",
        """{"type":"COMMAND","correlationId":"{key}-{n}","data":{"step":{n},"retry":false}}""",
        """{"result":"OK","ref":"{key}-{n}","items":[{n},{amount}]}""",
    ];

    public async IAsyncEnumerable<KafkaMessage> TailTopicAsync(
        string env, string topicName, int maxMessages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var topic   = _topics.FirstOrDefault(t => t.Name == topicName);
        var parts   = topic?.PartitionCount ?? 3;
        var baseOff = _rng.NextInt64(1_000, 5_000_000);
        var baseTs  = DateTime.UtcNow.AddMinutes(-maxMessages * 0.4);

        for (int i = 0; i < maxMessages; i++)
        {
            ct.ThrowIfCancellationRequested();

            var key      = $"{_keyPrefixes[_rng.Next(_keyPrefixes.Length)]}-{_rng.Next(1000, 9999)}";
            var tmpl     = _valueTemplates[_rng.Next(_valueTemplates.Length)];
            var value    = tmpl
                .Replace("{key}",    key)
                .Replace("{n}",      (baseOff + i).ToString())
                .Replace("{amount}", _rng.Next(10, 9999).ToString())
                .Replace("{ts}",     baseTs.AddSeconds(i * 2).ToString("o"));

            yield return new KafkaMessage
            {
                Offset    = baseOff + i,
                Partition = _rng.Next(0, parts),
                Key       = key,
                Value     = value,
                Timestamp = baseTs.AddSeconds(i * 2),
                Headers   = i % 4 == 0
                    ? new() { ["correlation-id"] = Guid.NewGuid().ToString("N")[..8], ["source"] = "mock" }
                    : new(),
            };

            if (i % 10 == 9)
                await Task.Delay(20, ct);
        }
    }
}
