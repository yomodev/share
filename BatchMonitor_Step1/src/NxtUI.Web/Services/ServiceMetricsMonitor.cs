using NxtUI.Core.Services;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Logging;
using NxtUI.Core.Models;

namespace NxtUI.Web.Services;

/// <summary>
/// Background metrics monitor. For each subscribed environment it periodically reads
/// the (already discovered) per-service metrics file incrementally, parses the memory
/// lines and caches them.
///
/// Polling is paused when an environment has no subscribers, but cached data (file offsets
/// and sample history) is kept for <c>IdleReleaseMinutes</c> so that re-subscribing resumes
/// from where it left off — no redundant file re-reads.
///
/// Service lists come from <see cref="IHeartbeatMonitor"/> (already cached) so this service
/// never hits MongoDB directly.
/// </summary>
public sealed class ServiceMetricsMonitor : BackgroundService, IServiceMetricsMonitor
{
    private const int MaxHistory = 1000;

    private readonly IHeartbeatMonitor               _heartbeatMonitor;
    private readonly ILogPathDiscoveryService        _discovery;
    private readonly LogPathSettings                 _paths;
    private readonly ILogger<ServiceMetricsMonitor>  _log;

    private readonly object                             _subLock  = new();
    private readonly Dictionary<string, int>            _envRefs  = new();   // env -> subscriber count
    private readonly Dictionary<string, DateTime>       _envIdle  = new();   // env -> idle-since (no subscribers)
    private readonly Dictionary<string, IDisposable>    _hbSubs   = new();   // env -> HeartbeatMonitor subscription
    private readonly ConcurrentDictionary<string, Entry>          _entries  = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim>  _envGates = new();
    private CancellationToken _ct = CancellationToken.None;

    public event Action<string>? OnMetricsUpdated;

    public ServiceMetricsMonitor(
        IHeartbeatMonitor               heartbeatMonitor,
        ILogPathDiscoveryService        discovery,
        IOptions<LogPathSettings>       paths,
        ILogger<ServiceMetricsMonitor>  log)
    {
        _heartbeatMonitor = heartbeatMonitor;
        _discovery        = discovery;
        _paths            = paths.Value;
        _log              = log;

        // A freshly resolved path means there may be a file to read right away —
        // poll that env immediately instead of waiting for the next interval.
        _discovery.OnPathResolved += OnPathResolved;
    }

    private void OnPathResolved(string cacheKey)
    {
        var env = cacheKey.Split('|', 2)[0]; // CacheKey is "env|host|service|pid"
        if (HasSubscribers(env))
            _ = PollEnvAsync(env, _ct);
    }

    public override void Dispose()
    {
        _discovery.OnPathResolved -= OnPathResolved;
        base.Dispose();
    }

    private sealed class Entry
    {
        public readonly object              Sync    = new();
        public long                         Offset;
        public MetricsSample?               Latest;
        public readonly List<MetricsSample> History = new();
    }

    // ── Subscription ───────────────────────────────────────────────────────────

    public IDisposable Subscribe(string env)
    {
        lock (_subLock)
        {
            _envRefs[env] = _envRefs.TryGetValue(env, out var n) ? n + 1 : 1;
            _envIdle.Remove(env); // no longer idle
            // Keep a HeartbeatMonitor subscription alive for this env so it keeps polling.
            if (!_hbSubs.ContainsKey(env))
                _hbSubs[env] = _heartbeatMonitor.Subscribe(env);
        }

        // Poll immediately so a freshly opened tab doesn't wait a full interval.
        _ = PollEnvAsync(env, _ct);
        return new Subscription(this, env);
    }

    private void Release(string env)
    {
        lock (_subLock)
        {
            if (!_envRefs.TryGetValue(env, out var n)) return;
            if (n <= 1)
            {
                _envRefs.Remove(env);
                _envIdle[env] = DateTime.UtcNow;
                // Release the HeartbeatMonitor subscription; its own idle window keeps it cached.
                if (_hbSubs.TryGetValue(env, out var hbSub))
                {
                    hbSub.Dispose();
                    _hbSubs.Remove(env);
                }
            }
            else
                _envRefs[env] = n - 1;
        }
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
        _ct = ct;
        var interval = TimeSpan.FromSeconds(Math.Max(5, _paths.MetricsIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                CleanupIdleEnvs();
                foreach (var env in SubscribedEnvs())
                    await PollEnvAsync(env, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private void CleanupIdleEnvs()
    {
        var idleTimeout = TimeSpan.FromMinutes(_paths.IdleReleaseMinutes);
        var now = DateTime.UtcNow;

        List<string> toRelease;
        lock (_subLock)
            toRelease = _envIdle.Where(kv => now - kv.Value > idleTimeout)
                                .Select(kv => kv.Key).ToList();

        foreach (var env in toRelease)
        {
            lock (_subLock) _envIdle.Remove(env);
            foreach (var key in _entries.Keys
                         .Where(k => k.StartsWith(env + "|", StringComparison.Ordinal)).ToList())
                _entries.TryRemove(key, out _);
            _log.LogInformation("metrics [{Env}]: idle cache released", env);
        }
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
            var services = _heartbeatMonitor.GetServices(env);
            if (services is null)
            {
                _log.LogDebug("metrics [{Env}]: heartbeat cache empty, skipping", env);
                return;
            }

            _log.LogDebug("metrics [{Env}]: polling {Count} services", env, services.Count);

            var changed = false;
            var parsed  = 0;
            foreach (var svc in services)
            {
                var folder = _discovery.GetCachedPath(svc, env);
                if (folder is null)
                {
                    _log.LogDebug("metrics [{Env}]: {Svc}@{Host} — folder not yet discovered", env, svc.ServiceName, svc.HostName);
                    _discovery.EnsureDiscovering(svc, env);
                    continue;
                }

                var file  = Path.Combine(folder, _paths.MetricsFileName);
                var key   = LogPathDiscoveryService.CacheKey(svc, env);
                var entry = _entries.GetOrAdd(key, _ => new Entry());

                _log.LogDebug("metrics [{Env}]: reading {File} (offset={Offset})", env, file, entry.Offset);

                IncrementalFileReader.Result result;
                try { result = await Task.Run(() => IncrementalFileReader.ReadNew(file, entry.Offset), ct); }
                catch (Exception ex) { _log.LogWarning(ex, "metrics: read failed {File}", file); continue; }

                if (result.Lines.Count == 0)
                {
                    _log.LogDebug("metrics [{Env}]: {Svc}@{Host} — no new lines", env, svc.ServiceName, svc.HostName);
                    continue;
                }

                _log.LogDebug("metrics [{Env}]: {Svc}@{Host} — {Lines} new lines", env, svc.ServiceName, svc.HostName, result.Lines.Count);

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
                        parsed++;
                    }
                }
            }

            if (parsed > 0)
                _log.LogDebug("metrics [{Env}]: {Parsed} new samples parsed", env, parsed);

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
