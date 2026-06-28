using NxtUI.Core.Services;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Logging;
using NxtUI.Models;

namespace NxtUI.Services;

/// <summary>
/// Background metrics monitor. For each subscribed environment it periodically reads
/// the (already discovered) per-service metrics file incrementally, parses the memory
/// lines and caches them. Work happens only while an environment has subscribers;
/// when the last subscriber leaves, that environment's cache is cleared.
///
/// Keyed per (env, service) because the metrics file path depends on {env}, so the
/// same service can be monitored independently under several open tabs.
/// </summary>
public sealed class ServiceMetricsMonitor : BackgroundService, IServiceMetricsMonitor
{
    private const int MaxHistory = 1000;

    private readonly IHeartbeatService              _heartbeat;
    private readonly ILogPathDiscoveryService       _discovery;
    private readonly LogPathSettings                _paths;
    private readonly ILogger<ServiceMetricsMonitor> _log;

    private readonly object                  _subLock  = new();
    private readonly Dictionary<string, int> _envRefs  = new();           // env -> subscriber count
    private readonly ConcurrentDictionary<string, Entry>        _entries  = new(); // (env|host|svc|pid) -> data
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _envGates = new(); // serialise per-env polls

    public event Action<string>? OnMetricsUpdated;

    public ServiceMetricsMonitor(
        IHeartbeatService              heartbeat,
        ILogPathDiscoveryService       discovery,
        IOptions<LogPathSettings>      paths,
        ILogger<ServiceMetricsMonitor> log)
    {
        _heartbeat = heartbeat;
        _discovery = discovery;
        _paths     = paths.Value;
        _log       = log;

        // A freshly resolved path means there may be a file to read right away —
        // poll that env immediately instead of waiting for the next interval.
        _discovery.OnPathResolved += OnPathResolved;
    }

    private void OnPathResolved(string cacheKey)
    {
        var env = cacheKey.Split('|', 2)[0]; // CacheKey is "env|host|service|pid"
        if (HasSubscribers(env))
            _ = PollEnvAsync(env, CancellationToken.None);
    }

    public override void Dispose()
    {
        _discovery.OnPathResolved -= OnPathResolved;
        base.Dispose();
    }

    private sealed class Entry
    {
        public readonly object             Sync    = new();
        public long                        Offset;
        public MetricsSample?              Latest;
        public readonly List<MetricsSample> History = new();
    }

    // ── Subscription ───────────────────────────────────────────────────────────

    public IDisposable Subscribe(string env)
    {
        lock (_subLock)
            _envRefs[env] = _envRefs.TryGetValue(env, out var n) ? n + 1 : 1;

        // Poll immediately so a freshly opened tab doesn't wait a full interval.
        _ = PollEnvAsync(env, CancellationToken.None);
        return new Subscription(this, env);
    }

    private void Release(string env)
    {
        var clear = false;
        lock (_subLock)
        {
            if (_envRefs.TryGetValue(env, out var n))
            {
                if (n <= 1) { _envRefs.Remove(env); clear = true; }
                else        { _envRefs[env] = n - 1; }
            }
        }

        if (!clear) return;

        // Last subscriber gone: drop cached data for this env.
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(env + "|", StringComparison.Ordinal)).ToList())
            _entries.TryRemove(key, out _);
    }

    private bool HasSubscribers(string env)
    {
        lock (_subLock) return _envRefs.ContainsKey(env);
    }

    private string[] SubscribedEnvs()
    {
        lock (_subLock) return _envRefs.Keys.ToArray();
    }

    // ── Reads ──────────────────────────────────────────────────────────────────

    public MetricsSample? GetLatest(string env, ServiceStatus svc)
    {
        if (_entries.TryGetValue(LogPathDiscoveryService.CacheKey(svc, env), out var e))
            lock (e.Sync) return e.Latest;
        return null;
    }

    public IReadOnlyList<MetricsSample> GetHistory(string env, ServiceStatus svc)
    {
        if (_entries.TryGetValue(LogPathDiscoveryService.CacheKey(svc, env), out var e))
            lock (e.Sync) return e.History.ToList();
        return Array.Empty<MetricsSample>();
    }

    // ── Polling loop ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _paths.MetricsIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                foreach (var env in SubscribedEnvs())
                    await PollEnvAsync(env, ct);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task PollEnvAsync(string env, CancellationToken ct)
    {
        if (!HasSubscribers(env)) return;
        if (string.IsNullOrWhiteSpace(_paths.MetricsFileName)) return;

        // Skip if a poll for this env is already in flight (timer vs immediate-subscribe).
        var gate = _envGates.GetOrAdd(env, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            List<ServiceStatus> services;
            try { services = await _heartbeat.GetServiceStatusesAsync(env, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "metrics: heartbeat failed for {Env}", env); return; }

            var changed = false;
            foreach (var svc in services)
            {
                var folder = _discovery.GetCachedPath(svc, env);
                if (folder is null)
                {
                    _discovery.EnsureDiscovering(svc, env); // resolve for next tick
                    continue;
                }

                var file  = Path.Combine(folder, _paths.MetricsFileName);
                var key   = LogPathDiscoveryService.CacheKey(svc, env);
                var entry = _entries.GetOrAdd(key, _ => new Entry());

                IncrementalFileReader.Result result;
                try { result = await Task.Run(() => IncrementalFileReader.ReadNew(file, entry.Offset), ct); }
                catch (Exception ex) { _log.LogWarning(ex, "metrics: read failed {File}", file); continue; }

                if (result.Lines.Count == 0) continue;

                lock (entry.Sync)
                {
                    entry.Offset = result.NewOffset;
                    foreach (var line in result.Lines)
                    {
                        if (!MetricsLogParser.TryParse(line, out var sample) || sample is null) continue;
                        entry.Latest = sample;
                        entry.History.Add(sample);
                        if (entry.History.Count > MaxHistory)
                            entry.History.RemoveRange(0, entry.History.Count - MaxHistory);
                        changed = true;
                    }
                }
            }

            if (changed) OnMetricsUpdated?.Invoke(env);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private ServiceMetricsMonitor? _owner;
        private readonly string        _env;

        public Subscription(ServiceMetricsMonitor owner, string env) { _owner = owner; _env = env; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_env);
    }
}
