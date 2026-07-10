using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NxtUI.Core.Configuration;
using NxtUI.Core.Models;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Mongo;

namespace NxtUI.Web.Services;

/// <summary>
/// Background singleton that polls Kafka and Mongo health (independently, each on its own
/// configurable interval — see <see cref="InfraHealthSettings"/>) and caches the results.
/// Consumers read synchronously via <see cref="GetKafka"/> / <see cref="GetMongo"/> — no
/// await, no per-render network call.
///
/// A single failed poll never immediately reports "Down" — it takes
/// <see cref="InfraHealthSettings.ConsecutiveFailuresBeforeDown"/> failures IN A ROW for the
/// same service before escalating from Degraded (yellow — "unreachable just now, might be a
/// blip") to Down (red — "consistently unreachable"). Any success resets the streak to zero.
/// </summary>
public sealed class InfraHealthCache(
    IKafkaMonitor kafka,
    MongoConnectionFactory mongoFactory,
    IOptions<InfraHealthSettings> options,
    ILogger<InfraHealthCache> log,
    OperationTracker ops)
    : BackgroundService
{
    private InfraHealthSettings S => options.Value;
    private readonly Dictionary<string, KafkaHealth> _kafkaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MongoHealth> _mongoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _kafkaFailureStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _mongoFailureStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationToken _ct = CancellationToken.None;

    public event Action<string>? OnHealthUpdated;

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

    /// <summary>Force an immediate poll (both services) for the given environment (e.g. on tab switch).</summary>
    public void RequestRefresh(string env) =>
        _ = Task.WhenAll(PollKafkaAsync(env, _ct), PollMongoAsync(env, _ct))
            .ContinueWith(
                t => log.LogWarning(t.Exception, "InfraHealthCache: RequestRefresh faulted for {Env}", env),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default)
            .ContinueWith(_ => RaiseUpdated(env), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    // Kafka and Mongo poll on independent cadences (InfraHealthSettings) — a slow/expensive
    // Mongo ping interval shouldn't force Kafka's, or vice versa.
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        try
        {
            await Task.WhenAll(RunKafkaLoopAsync(ct), RunMongoLoopAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunKafkaLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, S.KafkaPollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            string[] envs;
            lock (_lock) envs = [.. _kafkaCache.Keys];
            await Task.WhenAll(envs.Select(env => PollKafkaAsync(env, ct)));
            foreach (var env in envs) RaiseUpdated(env);
        }
    }

    private async Task RunMongoLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, S.MongoPollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            string[] envs;
            lock (_lock) envs = [.. _mongoCache.Keys];
            // All envs typically share one Mongo server; MongoConnectionFactory already
            // caches clients by settings fingerprint, so this doesn't multiply connections.
            await Task.WhenAll(envs.Select(env => PollMongoAsync(env, ct)));
            foreach (var env in envs) RaiseUpdated(env);
        }
    }

    private void RaiseUpdated(string env)
    {
        try { OnHealthUpdated?.Invoke(env); }
        catch (Exception ex) { log.LogWarning(ex, "InfraHealthCache: OnHealthUpdated subscriber threw for {Env}", env); }
    }

    private async Task PollKafkaAsync(string env, CancellationToken ct)
    {
        KafkaHealth result;
        using var op = ops.Track($"InfraHealthCache.Kafka({env})");
        try
        {
            var info = await kafka.GetClusterInfoAsync(env, ct);
            var onlineBrokers = info.Brokers.Count(b => b.IsOnline);
            var totalBrokers = info.Brokers.Count;

            if (onlineBrokers == totalBrokers) ResetStreak(_kafkaFailureStreak, env);

            result = new KafkaHealth
            {
                Status = onlineBrokers == totalBrokers ? HealthStatus.Healthy
                          : onlineBrokers == 0 ? StatusForFailure(_kafkaFailureStreak, env)
                                                : HealthStatus.Degraded,
                Brokers = info.Brokers.Select(b => new BrokerHealth
                {
                    Id = b.Id,
                    Host = b.Host,
                    Port = b.Port,
                    IsOnline = b.IsOnline,
                }).ToList(),
                CheckedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "InfraHealthCache: Kafka poll failed for {Env}", env);
            result = new KafkaHealth { Status = StatusForFailure(_kafkaFailureStreak, env), CheckedAt = DateTime.UtcNow, Error = ex.Message };
        }
        lock (_lock) _kafkaCache[env] = result;
    }

    private async Task PollMongoAsync(string env, CancellationToken ct)
    {
        if (!mongoFactory.IsConfigured(env))
            return;

        using var op = ops.Track($"InfraHealthCache.Mongo({env})");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, S.MongoPingTimeoutSeconds));
        timeoutCts.CancelAfter(timeout);

        MongoHealth result;
        try
        {
            // GetClientAsync (not GetClient) — the first time a client is built for this
            // env's settings fingerprint, building it can itself take several seconds
            // (candidate username probing in MongoConnectionFactory). Using the sync
            // GetClient here would block past timeoutCts entirely, since only the ping
            // call below was ever bounded by it — a slow-but-working first connection
            // could take the whole poll well past its configured timeout and get reported
            // as Down instead of the (correct) Degraded-just-this-once.
            var client = await mongoFactory.GetClientAsync(env, timeoutCts.Token);
            var db = client.GetDatabase("admin");
            await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeoutCts.Token);
            ResetStreak(_mongoFailureStreak, env);
            result = new MongoHealth { Status = HealthStatus.Healthy, CheckedAt = DateTime.UtcNow };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogError("InfraHealthCache: Mongo poll timed out after {Timeout}s for {Env}", timeout.TotalSeconds, env);
            result = new MongoHealth { Status = StatusForFailure(_mongoFailureStreak, env), CheckedAt = DateTime.UtcNow, Error = "Timed out" };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "InfraHealthCache: Mongo poll failed for {Env}: {Error}", env, ex.Message);
            result = new MongoHealth { Status = StatusForFailure(_mongoFailureStreak, env), CheckedAt = DateTime.UtcNow, Error = ex.Message };
        }

        lock (_lock) _mongoCache[env] = result;
    }

    // ── Failure-streak → Degraded/Down escalation ───────────────────────────────
    // A single bad poll after a run of successes reads as Degraded (yellow — "might be a
    // blip"); only ConsecutiveFailuresBeforeDown failures in a row for the SAME service
    // escalate it to Down (red — "consistently unreachable"). Any success resets to zero.

    private void ResetStreak(Dictionary<string, int> streaks, string env)
    {
        lock (_lock) streaks[env] = 0;
    }

    private HealthStatus StatusForFailure(Dictionary<string, int> streaks, string env)
    {
        int count;
        lock (_lock) count = streaks[env] = streaks.GetValueOrDefault(env) + 1;
        return count >= Math.Max(1, S.ConsecutiveFailuresBeforeDown) ? HealthStatus.Down : HealthStatus.Degraded;
    }
}
