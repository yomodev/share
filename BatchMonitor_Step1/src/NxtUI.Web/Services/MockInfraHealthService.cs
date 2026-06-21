using BatchMonitor.Core.Models;
using BatchMonitor.Core.Services;

namespace BatchMonitor.Services;

public class MockInfraHealthService : IInfraHealthService
{
    private static readonly Random _rng = new();

    public async Task<KafkaHealth> CheckKafkaAsync(string env, CancellationToken ct = default)
    {
        // Simulate broker metadata fetch + leader election check (~80-250 ms)
        await Task.Delay(_rng.Next(80, 250), ct);

        var brokers = new BrokerHealth[]
        {
            new() { Id = 1, Host = "kafka-1", Port = 9092, IsOnline = true,  LatencyMs = _rng.Next(2, 9)  },
            new() { Id = 2, Host = "kafka-2", Port = 9092, IsOnline = true,  LatencyMs = _rng.Next(3, 14) },
            new() { Id = 3, Host = "kafka-3", Port = 9092, IsOnline = false, LatencyMs = null             },
        };

        var online = brokers.Count(b => b.IsOnline);
        var status = online == 0            ? HealthStatus.Down
                   : online < brokers.Length ? HealthStatus.Degraded
                   :                           HealthStatus.Healthy;

        return new KafkaHealth { Status = status, Brokers = brokers, CheckedAt = DateTime.UtcNow };
    }

    public async Task<MongoHealth> CheckMongoAsync(string env, CancellationToken ct = default)
    {
        // Simulate replica-set hello + lightweight ping command (~30-150 ms)
        await Task.Delay(_rng.Next(30, 150), ct);

        var nodes = new MongoNodeHealth[]
        {
            new() { Host = "mongo-1:27017", Role = "PRIMARY",   IsOnline = true, LatencyMs = _rng.Next(1, 5)  },
            new() { Host = "mongo-2:27017", Role = "SECONDARY", IsOnline = true, LatencyMs = _rng.Next(2, 9)  },
            new() { Host = "mongo-3:27017", Role = "SECONDARY", IsOnline = true, LatencyMs = _rng.Next(2, 11) },
        };

        return new MongoHealth
        {
            Status        = HealthStatus.Healthy,
            DatabaseCount = 3,
            Nodes         = nodes,
            CheckedAt     = DateTime.UtcNow,
        };
    }
}
