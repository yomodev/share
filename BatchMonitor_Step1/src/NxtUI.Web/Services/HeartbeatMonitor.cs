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
        public volatile IReadOnlyList<ServiceStatus> Services = [];
    }

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
        if (!_states.ContainsKey(env)) return;

        // Prevent concurrent polls for the same env (timer vs. immediate-subscribe race).
        var gate = _gates.GetOrAdd(env, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        using var op = _ops.Track($"HeartbeatMonitor.Poll({env})");
        try
        {
            var services = await _heartbeat.GetServiceStatusesAsync(env, ct: ct);
            if (_states.TryGetValue(env, out var state))
            {
                state.Services = services;
                OnServicesUpdated?.Invoke(env);
                _log.LogDebug("heartbeat-monitor [{Env}]: {Count} services cached", env, services.Count);
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
