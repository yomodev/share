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

    private readonly IKafkaMonitor          _kafka;
    private readonly MongoConnectionFactory _mongoFactory;
    private readonly ILogger<InfraHealthCache> _log;
    private readonly OperationTracker _ops;

    private readonly Dictionary<string, KafkaHealth> _kafkaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MongoHealth>  _mongoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationToken _ct = CancellationToken.None;

    public event Action<string>? OnHealthUpdated;

    public InfraHealthCache(IKafkaMonitor kafka, MongoConnectionFactory mongoFactory, ILogger<InfraHealthCache> log, OperationTracker ops)
    {
        _kafka        = kafka;
        _mongoFactory = mongoFactory;
        _log          = log;
        _ops          = ops;
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
    public void RequestRefresh(string env) =>
        _ = PollAllAsync([env], _ct).ContinueWith(
            t => _log.LogWarning(t.Exception, "InfraHealthCache: RequestRefresh faulted for {Env}", env),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                string[] envs;
                lock (_lock) envs = [.. _kafkaCache.Keys.Union(_mongoCache.Keys)];
                await PollAllAsync(envs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollAllAsync(string[] envs, CancellationToken ct)
    {
        // Kafka: each env may be a different cluster — poll in parallel.
        // Mongo: all envs share one server — ping once, fan result out to all envs.
        await Task.WhenAll(
            Task.WhenAll(envs.Select(env => PollKafkaAsync(env, ct))),
            PollMongoAllAsync(envs, ct));

        foreach (var env in envs)
        {
            try { OnHealthUpdated?.Invoke(env); }
            catch (Exception ex) { _log.LogWarning(ex, "InfraHealthCache: OnHealthUpdated subscriber threw for {Env}", env); }
        }
    }

    private async Task PollKafkaAsync(string env, CancellationToken ct)
    {
        KafkaHealth result;
        using var op = _ops.Track($"InfraHealthCache.Kafka({env})");
        try
        {
            var info = await _kafka.GetClusterInfoAsync(env, ct);
            var onlineBrokers = info.Brokers.Count(b => b.IsOnline);
            var totalBrokers  = info.Brokers.Count;
            result = new KafkaHealth
            {
                Status    = onlineBrokers == totalBrokers ? HealthStatus.Healthy
                          : onlineBrokers == 0            ? HealthStatus.Down
                                                         : HealthStatus.Degraded,
                Brokers   = info.Brokers.Select(b => new BrokerHealth
                            {
                                Id       = b.Id,
                                Host     = b.Host,
                                Port     = b.Port,
                                IsOnline = b.IsOnline,
                            }).ToList(),
                CheckedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InfraHealthCache: Kafka poll failed for {Env}", env);
            result = new KafkaHealth { Status = HealthStatus.Down, CheckedAt = DateTime.UtcNow, Error = ex.Message };
        }
        lock (_lock) _kafkaCache[env] = result;
    }

    // Each environment may point at a different Mongo server, so ping per env —
    // MongoConnectionFactory caches clients by settings fingerprint, so envs that
    // genuinely share one server still reuse a single underlying connection.
    private async Task PollMongoAllAsync(string[] envs, CancellationToken ct) =>
        await Task.WhenAll(envs.Select(env => PollMongoAsync(env, ct)));

    private async Task PollMongoAsync(string env, CancellationToken ct)
    {
        if (!_mongoFactory.IsConfigured(env))
            return;

        using var op = _ops.Track($"InfraHealthCache.Mongo({env})");

        MongoHealth result;
        try
        {
            var db = _mongoFactory.GetClient(env).GetDatabase("admin");
            await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            result = new MongoHealth { Status = HealthStatus.Healthy, CheckedAt = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InfraHealthCache: Mongo poll failed for {Env}", env);
            result = new MongoHealth { Status = HealthStatus.Down, CheckedAt = DateTime.UtcNow, Error = ex.Message };
        }

        lock (_lock) _mongoCache[env] = result;
    }
}
