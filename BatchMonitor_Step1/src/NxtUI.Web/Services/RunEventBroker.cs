using NxtUI.Core.Models;

namespace NxtUI.Web.Services;

/// <summary>
/// Singleton in-process pub/sub for live run events.
///
/// Replaces a per-circuit SignalR client connection that looped back to this
/// same process's own <c>/hubs/run-events</c> hub — every event was serialized,
/// sent over a websocket, received by the same process, and deserialized, for
/// no reason: the only publisher (<see cref="MockRunService"/>) runs in-process.
/// <see cref="RunDetail"/>/<see cref="TimelineRun"/> tabs call
/// <see cref="SubscribeToRunAsync"/> to register a callback keyed by (env, runId);
/// a publisher calls <see cref="Publish"/> to fan an event out to every
/// subscriber of that run, synchronously and in-process.
///
/// If a genuinely external process ever needs to publish run events over the
/// network, reintroduce a hub that calls <see cref="Publish"/> on receipt —
/// subscribers here don't need to change.
///
/// <see cref="RunWatchStarted"/>/<see cref="RunWatchStopped"/> fire on the
/// 0→1 / 1→0 subscriber-count transition for a given run, regardless of who's
/// subscribing or publishing — <see cref="RunEventWatcher"/> uses these to
/// start/stop a shared server-side poller for backends that don't push on
/// their own (see <see cref="IPushesOwnRunEvents"/>).
/// </summary>
public class RunEventBroker(ILogger<RunEventBroker> logger)
{
    private readonly Dictionary<string, List<Func<PerformanceEvent, Task>>> _handlers = new();
    private readonly object _lock = new();

    public event Action<string, string>? RunWatchStarted;
    public event Action<string, string>? RunWatchStopped;

    // ── Public API ────────────────────────────────────────────────────────

    public Task<IDisposable> SubscribeToRunAsync(
        string env, string runId,
        Func<PerformanceEvent, Task> handler,
        CancellationToken ct = default)
    {
        var key = Key(env, runId);
        bool isFirst;
        lock (_lock)
        {
            isFirst = !_handlers.TryGetValue(key, out var list);
            if (isFirst)
            {
                list = new List<Func<PerformanceEvent, Task>>();
                _handlers[key] = list;
            }
            list!.Add(handler);
        }

        if (isFirst) RunWatchStarted?.Invoke(env, runId);

        return Task.FromResult<IDisposable>(new Unsubscriber(() => Unsubscribe(env, runId, key, handler)));
    }

    public void UnsubscribeFromRun(string env, string runId)
    {
        var key = Key(env, runId);
        bool hadAny;
        lock (_lock) { hadAny = _handlers.Remove(key); }
        if (hadAny) RunWatchStopped?.Invoke(env, runId);
    }

    /// <summary>Fans an event out to every current subscriber of (env, runId). Fire-and-forget per handler.</summary>
    public void Publish(string env, string runId, PerformanceEvent evt)
    {
        var key = Key(env, runId);
        List<Func<PerformanceEvent, Task>> snapshot;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(key, out var list)) return;
            snapshot = list.ToList();
        }

        foreach (var handler in snapshot)
            _ = InvokeSafeAsync(handler, evt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Key(string env, string runId) => $"{env}:{runId}";

    private async Task InvokeSafeAsync(Func<PerformanceEvent, Task> handler, PerformanceEvent evt)
    {
        try { await handler(evt); }
        catch (Exception ex) { logger.LogWarning(ex, "RunEvent handler error"); }
    }

    private void Unsubscribe(string env, string runId, string key, Func<PerformanceEvent, Task> handler)
    {
        bool becameEmpty;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(key, out var list))
            {
                becameEmpty = false;
            }
            else
            {
                list.Remove(handler);
                becameEmpty = list.Count == 0;
                if (becameEmpty) _handlers.Remove(key);
            }
        }

        if (becameEmpty) RunWatchStopped?.Invoke(env, runId);
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
