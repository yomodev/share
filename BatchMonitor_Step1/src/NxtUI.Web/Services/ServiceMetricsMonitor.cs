using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Logging;
using NxtUI.Core.Models;
using NxtUI.Core.Services;
using System.Collections.Concurrent;

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
    private const int MaxParallel = 48;   // max concurrent file reads across all envs

    private readonly IHeartbeatMonitor _heartbeatMonitor;
    private readonly ILogPathDiscoveryService _discovery;
    private readonly LogPathSettings _paths;
    private readonly ILogger<ServiceMetricsMonitor> _log;

    // Limits simultaneous network file reads to avoid overwhelming the servers.
    private readonly SemaphoreSlim _ioGate = new(MaxParallel, MaxParallel);

    private readonly object _subLock = new();
    private readonly Dictionary<string, int> _envRefs = new();   // env -> subscriber count
    private readonly Dictionary<string, DateTime> _envIdle = new();   // env -> idle-since (no subscribers)
    private readonly Dictionary<string, IDisposable> _hbSubs = new();   // env -> HeartbeatMonitor subscription
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _envGates = new();
    private CancellationToken _ct = CancellationToken.None;

    public event Action<string>? OnMetricsUpdated;

    public ServiceMetricsMonitor(
        IHeartbeatMonitor heartbeatMonitor,
        ILogPathDiscoveryService discovery,
        IOptions<LogPathSettings> paths,
        ILogger<ServiceMetricsMonitor> log)
    {
        _heartbeatMonitor = heartbeatMonitor;
        _discovery = discovery;
        _paths = paths.Value;
        _log = log;

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
        public readonly object Sync = new();
        public string? FilePath;    // cached once discovered; never re-searched
        public long Offset;
        public DateTime LastWriteUtc = DateTime.MinValue;
        public MetricsSample? Latest;
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
                await Task.WhenAll(SubscribedEnvs().Select(env => PollEnvAsync(env, ct)));
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

    // Coalesced rerun flag: set when a trigger (timer / subscribe / path-resolved)
    // arrives while a poll for that env is already running. Instead of dropping
    // the trigger outright, the in-flight poll checks this after it finishes and
    // runs once more immediately — so a burst of triggers during a busy poll
    // results in exactly one extra pass, not zero (silently dropped) and not N
    // (queued/piled up).
    private readonly ConcurrentDictionary<string, bool> _envRerunPending = new();

    private async Task PollEnvAsync(string env, CancellationToken ct)
    {
        if (!HasSubscribers(env)) return;
        if (string.IsNullOrWhiteSpace(_paths.MetricsFileName)) return;

        var gate = _envGates.GetOrAdd(env, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            _envRerunPending[env] = true;
            return;
        }

        try
        {
            do
            {
                _envRerunPending[env] = false;
                await PollEnvOnceAsync(env, ct);
            }
            while (_envRerunPending.TryGetValue(env, out var pending) && pending && HasSubscribers(env));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PollEnvOnceAsync(string env, CancellationToken ct)
    {
        var services = _heartbeatMonitor.GetServices(env);
        if (services is null)
        {
            _log.LogDebug("metrics [{Env}]: heartbeat cache empty, skipping", env);
            return;
        }

        _log.LogDebug("metrics [{Env}]: polling {Count} services", env, services.Count);

        var parsed = 0;

        await Task.WhenAll(services.Select(async svc =>
        {
            var key = LogPathDiscoveryService.CacheKey(svc, env);
            var entry = _entries.GetOrAdd(key, _ => new Entry());

            // Resolve and cache the file path once; never re-search after that.
            if (entry.FilePath is null)
            {
                var folder = _discovery.GetCachedPath(svc, env);
                if (folder is null) { _discovery.EnsureDiscovering(svc, env); return; }
                entry.FilePath = Path.Combine(folder, _paths.MetricsFileName);
            }

            var file = entry.FilePath;

            // Cheap stat — skip the read entirely if the file hasn't changed.
            // Runs unthrottled because a stat is far cheaper than a read.
            DateTime mtime;
            try
            {
                mtime = await Task.Run(() => new FileInfo(file).LastWriteTimeUtc, ct);
            }
            catch { return; }

            lock (entry.Sync)
            {
                if (mtime <= entry.LastWriteUtc) return;
            }

            // File changed — throttle concurrent reads to avoid saturating servers.
            long offset;
            lock (entry.Sync) offset = entry.Offset;

            await _ioGate.WaitAsync(ct);
            IncrementalFileReader.Result result;
            try { result = await Task.Run(() => IncrementalFileReader.ReadNew(file, offset), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _log.LogWarning(ex, "metrics: read failed {File}", file); return; }
            finally { _ioGate.Release(); }

            if (result.Lines.Count == 0)
            {
                lock (entry.Sync) entry.LastWriteUtc = mtime; // mark seen even with no new metrics lines
                return;
            }

            var parsedAny = false;
            lock (entry.Sync)
            {
                entry.Offset = result.NewOffset;
                entry.LastWriteUtc = mtime;
                foreach (var line in result.Lines)
                {
                    if (!MetricsLogParser.TryParse(line, out var sample) || sample is null) continue;
                    entry.Latest = sample;
                    entry.History.Add(sample);
                    if (entry.History.Count > MaxHistory)
                        entry.History.RemoveRange(0, entry.History.Count - MaxHistory);
                    parsedAny = true;
                    Interlocked.Increment(ref parsed);
                }
            }

            // Report this service's update the moment it's parsed, instead of
            // waiting for every other service in the env to finish reading too.
            if (parsedAny) OnMetricsUpdated?.Invoke(env);
        }));

        if (parsed > 0)
            _log.LogDebug("metrics [{Env}]: {Parsed} new samples parsed", env, parsed);

        // Prune entries for services no longer reported by heartbeat.
        var activeKeys = services.Select(svc => LogPathDiscoveryService.CacheKey(svc, env)).ToHashSet();
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(env + "|", StringComparison.Ordinal) && !activeKeys.Contains(k)).ToList())
        {
            _entries.TryRemove(key, out _);
            _log.LogDebug("metrics [{Env}]: dropped stale entry {Key}", env, key);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private ServiceMetricsMonitor? _owner;
        private readonly string _env;

        public Subscription(ServiceMetricsMonitor owner, string env) { _owner = owner; _env = env; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_env);
    }
}
