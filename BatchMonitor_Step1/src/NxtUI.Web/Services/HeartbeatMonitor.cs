using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Background service that polls <see cref="IHeartbeatService"/> for each subscribed
/// environment, caches the results, and fires <see cref="OnServicesUpdated"/> after
/// every successful fetch. Polling runs only while an environment has at least one
/// subscriber. The cache is kept in memory for <c>IdleReleaseMinutes</c> after the
/// last subscriber leaves so that re-subscribing (e.g. switching tabs) is instant.
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService, IHeartbeatMonitor
{
    private readonly IHeartbeatService _heartbeat;
    private readonly HeartbeatSettings _settings;
    private readonly ILogger<HeartbeatMonitor> _log;
    private readonly OperationTracker _ops;

    private CancellationToken _ct = CancellationToken.None;

    private sealed class EnvState
    {
        public readonly object Lock = new();
        public int Subscribers;
        public DateTime IdleSince = DateTime.MinValue;
        // Null means "no successful poll yet" — the next poll does a full fetch. Once set,
        // subsequent polls only ask Mongo for documents changed since this timestamp.
        public DateTime? LastPollUtc;
        // Keyed by (host, service, pid) — accumulates across incremental polls; a service
        // that stops heartbeating just stops getting new entries, it isn't dropped by an
        // incremental fetch (which only returns what changed), so IsOnline is recomputed
        // from the current time on every poll instead of trusted from whenever it was fetched.
        public readonly Dictionary<string, ServiceStatus> ServiceMap = new(StringComparer.OrdinalIgnoreCase);
        public volatile IReadOnlyList<ServiceStatus> Services = [];
    }

    private static string Key(ServiceStatus s) => $"{s.HostName}|{s.ServiceName}|{s.ProcessId}";

    private readonly ConcurrentDictionary<string, EnvState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? OnServicesUpdated;

    public HeartbeatMonitor(
        IHeartbeatService heartbeat,
        IOptions<HeartbeatSettings> settings,
        ILogger<HeartbeatMonitor> log,
        OperationTracker ops)
    {
        _heartbeat = heartbeat;
        _settings = settings.Value;
        _log = log;
        _ops = ops;
    }

    // ── IHeartbeatMonitor ─────────────────────────────────────────────────────

    public IDisposable Subscribe(string env)
    {
        var state = _states.GetOrAdd(env, _ => new EnvState());
        bool wasIdle;
        lock (state.Lock)
        {
            wasIdle = state.Subscribers == 0;
            state.Subscribers++;
            state.IdleSince = DateTime.MinValue;
        }
        // Trigger an immediate poll so callers don't wait a full interval for data.
        if (wasIdle)
            _ = PollEnvAsync(env, _ct);
        return new Subscription(this, env);
    }

    public IReadOnlyList<ServiceStatus>? GetServices(string env) =>
        _states.TryGetValue(env, out var s) ? (s.Services.Count > 0 ? s.Services : null) : null;

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var idleTimeout = TimeSpan.FromMinutes(_settings.IdleReleaseMinutes);
                var now = DateTime.UtcNow;

                foreach (var (env, state) in _states)
                {
                    bool shouldRelease, shouldPoll;
                    lock (state.Lock)
                    {
                        shouldRelease = state.Subscribers == 0 &&
                                        state.IdleSince != DateTime.MinValue &&
                                        (now - state.IdleSince) > idleTimeout;
                        shouldPoll = !shouldRelease && state.Subscribers > 0;
                    }

                    if (shouldRelease)
                    {
                        if (_states.TryRemove(env, out _))
                        {
                            _gates.TryRemove(env, out _);
                            _log.LogInformation("heartbeat-monitor [{Env}]: idle cache released", env);
                        }
                        continue;
                    }

                    if (shouldPoll)
                        await PollEnvAsync(env, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── private ───────────────────────────────────────────────────────────────

    private async Task PollEnvAsync(string env, CancellationToken ct)
    {
        if (!_states.TryGetValue(env, out var state)) return;

        // Prevent concurrent polls for the same env (timer vs. immediate-subscribe race).
        var gate = _gates.GetOrAdd(env, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        using var op = _ops.Track($"HeartbeatMonitor.Poll({env})");
        try
        {
            DateTime? since;
            lock (state.Lock) since = state.LastPollUtc;

            // Captured before querying so nothing updated mid-query is missed by the next poll.
            var pollStartUtc = DateTime.UtcNow;

            // First poll for this env (since == null): full fetch, same as before — everything
            // within IHeartbeatService's default recent window. Every poll after that only asks
            // for documents changed since the previous successful poll.
            var changed = await _heartbeat.GetServiceStatusesAsync(env, since, ct);

            if (_states.TryGetValue(env, out _)) // still subscribed — env wasn't idle-released mid-query
            {
                var threshold = _settings.OfflineThreshold;
                var now = DateTime.UtcNow;

                lock (state.Lock)
                {
                    foreach (var svc in changed)
                        state.ServiceMap[Key(svc)] = svc;

                    // Recompute liveness for every cached entry from the current time — a
                    // service that stopped heartbeating simply stops appearing in future
                    // incremental fetches, so its cached IsOnline would otherwise never flip.
                    foreach (var svc in state.ServiceMap.Values)
                        svc.IsOnline = (now - svc.UpdatedDateTime) <= threshold;

                    state.Services    = state.ServiceMap.Values.ToList();
                    state.LastPollUtc = pollStartUtc;
                }

                OnServicesUpdated?.Invoke(env);
                _log.LogDebug(
                    "heartbeat-monitor [{Env}]: {Changed} changed, {Total} cached ({Mode})",
                    env, changed.Count, state.Services.Count, since is null ? "full" : "incremental");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "heartbeat-monitor [{Env}]: poll failed", env);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Release(string env)
    {
        if (!_states.TryGetValue(env, out var state)) return;
        lock (state.Lock)
        {
            state.Subscribers = Math.Max(0, state.Subscribers - 1);
            if (state.Subscribers == 0)
                state.IdleSince = DateTime.UtcNow;
        }
    }

    private sealed class Subscription(HeartbeatMonitor owner, string env) : IDisposable
    {
        private HeartbeatMonitor? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(env);
    }
}
