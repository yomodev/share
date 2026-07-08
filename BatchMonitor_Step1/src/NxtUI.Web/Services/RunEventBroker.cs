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
/// </summary>
public class RunEventBroker(ILogger<RunEventBroker> logger)
{
    private readonly Dictionary<string, List<Func<PerformanceEvent, Task>>> _handlers = new();
    private readonly object _lock = new();

    // ── Public API ────────────────────────────────────────────────────────

    public Task<IDisposable> SubscribeToRunAsync(
        string env, string runId,
        Func<PerformanceEvent, Task> handler,
        CancellationToken ct = default)
    {
        var key = Key(env, runId);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(key, out var list))
            {
                list = new List<Func<PerformanceEvent, Task>>();
                _handlers[key] = list;
            }
            list.Add(handler);
        }

        return Task.FromResult<IDisposable>(new Unsubscriber(() => Unsubscribe(key, handler)));
    }

    public void UnsubscribeFromRun(string env, string runId)
    {
        var key = Key(env, runId);
        lock (_lock) { _handlers.Remove(key); }
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

    private void Unsubscribe(string key, Func<PerformanceEvent, Task> handler)
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(key, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0) _handlers.Remove(key);
            }
        }
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
