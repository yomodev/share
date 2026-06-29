using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NxtUI.Configuration;
using NxtUI.Core.Models;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Mongo;

namespace NxtUI.Web.Services;

/// <summary>
/// Background singleton that polls Kafka and Mongo health every <see cref="PollIntervalSeconds"/>
/// seconds and caches the results. Consumers read synchronously via <see cref="GetKafka"/> /
/// <see cref="GetMongo"/> — no await, no per-render network call.
/// </summary>
public sealed class InfraHealthCache : BackgroundService
{
    private const int PollIntervalSeconds = 30;

    private readonly IKafkaMonitor    _kafka;
    private readonly MongoConnection  _mongoConnection;
    private readonly ILogger<InfraHealthCache> _log;

    private readonly Dictionary<string, KafkaHealth> _kafkaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MongoHealth>  _mongoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event Action<string>? OnHealthUpdated;

    public InfraHealthCache(IKafkaMonitor kafka, MongoConnection mongo, ILogger<InfraHealthCache> log)
    {
        _kafka           = kafka;
        _mongoConnection = mongo;
        _log             = log;
    }

    public KafkaHealth GetKafka(string env)
    {
        lock (_lock)
            return _kafkaCache.TryGetValue(env, out var h) ? h : new KafkaHealth { Status = HealthStatus.Unknown };
    }

    public MongoHealth GetMongo(string env)
    {
        lock (_lock)
            return _mongoCache.TryGetValue(env, out var h) ? h : new MongoHealth { Status = HealthStatus.Unknown };
    }

    /// <summary>Force an immediate poll for the given environment (e.g. on tab switch).</summary>
    public void RequestRefresh(string env) => _ = PollEnvAsync(env, CancellationToken.None);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                string[] envs;
                lock (_lock) envs = [.._kafkaCache.Keys.Union(_mongoCache.Keys)];
                foreach (var env in envs)
                    await PollEnvAsync(env, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollEnvAsync(string env, CancellationToken ct)
    {
        await Task.WhenAll(PollKafkaAsync(env, ct), PollMongoAsync(env, ct));
        OnHealthUpdated?.Invoke(env);
    }

    private async Task PollKafkaAsync(string env, CancellationToken ct)
    {
        KafkaHealth result;
        try
        {
            var info = await _kafka.GetClusterInfoAsync(env, ct);
            var onlineBrokers  = info.Brokers.Count(b => b.IsOnline);
            var totalBrokers   = info.Brokers.Count;
            result = new KafkaHealth
            {
                Status     = onlineBrokers == totalBrokers ? HealthStatus.Healthy
                           : onlineBrokers == 0            ? HealthStatus.Down
                                                           : HealthStatus.Degraded,
                Brokers    = info.Brokers.Select(b => new BrokerHealth
                             {
                                 Id       = b.Id,
                                 Host     = b.Host,
                                 Port     = b.Port,
                                 IsOnline = b.IsOnline,
                             }).ToList(),
                CheckedAt  = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InfraHealthCache: Kafka poll failed for {Env}", env);
            result = new KafkaHealth { Status = HealthStatus.Down, CheckedAt = DateTime.UtcNow, Error = ex.Message };
        }
        lock (_lock) _kafkaCache[env] = result;
    }

    private async Task PollMongoAsync(string env, CancellationToken ct)
    {
        MongoHealth result;
        try
        {
            // Use a cheap ping instead of GetDatabasesAsync (which runs dbStats for every
            // database sequentially and is far too expensive for a connectivity check).
            var db  = _mongoConnection.GetDatabase(env);
            var cmd = new MongoDB.Bson.BsonDocument("ping", 1);
            await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(cmd, cancellationToken: ct);
            result = new MongoHealth
            {
                Status    = HealthStatus.Healthy,
                CheckedAt = DateTime.UtcNow,
                Nodes     = [new MongoNodeHealth { Host = $"{env}-mongo", Role = "PRIMARY", IsOnline = true }],
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InfraHealthCache: Mongo poll failed for {Env}", env);
            result = new MongoHealth { Status = HealthStatus.Down, CheckedAt = DateTime.UtcNow, Error = ex.Message };
        }
        lock (_lock) _mongoCache[env] = result;
    }
}
