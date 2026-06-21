using BatchMonitor.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace BatchMonitor.Services;

/// <summary>
/// Scoped service (one per Blazor circuit) that owns a single SignalR
/// connection to <c>/hubs/batch-events</c>.
///
/// Multiple <see cref="BatchDetail"/> tabs share this connection — each tab
/// calls <see cref="SubscribeToBatchAsync"/> to join its group and registers
/// a callback via <see cref="OnBatchEvent"/>; the service fans incoming
/// events out to all registered callbacks keyed by (env, runId).
///
/// Step 9: full implementation.
/// </summary>
public class SignalRConnectionService : IAsyncDisposable
{
    private readonly ILogger<SignalRConnectionService> _logger;
    private readonly NavigationManager _nav;

    private HubConnection? _connection;
    private IDisposable?   _eventSubscription;

    // Per-(env:runId) callbacks — multiple tabs can subscribe.
    private readonly Dictionary<string, List<Func<PerformanceEvent, Task>>> _handlers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SignalRConnectionService(
        ILogger<SignalRConnectionService> logger,
        NavigationManager nav)
    {
        _logger = logger;
        _nav    = nav;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to live <see cref="PerformanceEvent"/> pushes for
    /// <paramref name="runId"/>. <paramref name="handler"/> is called
    /// on the SignalR dispatcher thread for every incoming event.
    /// Returns a disposable that unsubscribes the handler when disposed.
    /// </summary>
    public async Task<IDisposable> SubscribeToBatchAsync(
        string env, string runId,
        Func<PerformanceEvent, Task> handler,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var key = Key(env, runId);
        await _lock.WaitAsync(ct);
        try
        {
            if (!_handlers.TryGetValue(key, out var list))
            {
                list = new List<Func<PerformanceEvent, Task>>();
                _handlers[key] = list;
            }
            list.Add(handler);
        }
        finally { _lock.Release(); }

        // Tell the server to add us to the group.
        if (_connection?.State == HubConnectionState.Connected)
        {
            try { await _connection.InvokeAsync("SubscribeToBatch", env, runId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to join SignalR group {Key}", key); }
        }

        return new Unsubscriber(() => _ = UnsubscribeAsync(env, runId, handler));
    }

    public async Task UnsubscribeFromBatchAsync(string env, string runId)
    {
        var key = Key(env, runId);
        await _lock.WaitAsync();
        try { _handlers.Remove(key); }
        finally { _lock.Release(); }

        if (_connection?.State == HubConnectionState.Connected)
        {
            try { await _connection.InvokeAsync("UnsubscribeFromBatch", env, runId); }
            catch { /* best-effort */ }
        }
    }

    // ── Connection management ─────────────────────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is not null) return;

        var url = _nav.ToAbsoluteUri("/hubs/batch-events").ToString();

        _connection = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        // Fan incoming "BatchEvent" messages out to the correct per-batch handlers.
        _eventSubscription = _connection.On<string, string, PerformanceEvent>(
            "BatchEvent", async (env, runId, evt) =>
            {
                var key = Key(env, runId);
                List<Func<PerformanceEvent, Task>> snapshot;
                await _lock.WaitAsync();
                try
                {
                    snapshot = _handlers.TryGetValue(key, out var list)
                        ? list.ToList()
                        : new List<Func<PerformanceEvent, Task>>();
                }
                finally { _lock.Release(); }

                foreach (var h in snapshot)
                {
                    try { await h(evt); }
                    catch (Exception ex) { _logger.LogWarning(ex, "BatchEvent handler error"); }
                }
            });

        _connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("SignalR reconnected ({Id})", connectionId);
            // Re-join all groups after reconnection.
            await _lock.WaitAsync();
            var keys = _handlers.Keys.ToList();
            _lock.Release();
            foreach (var k in keys)
            {
                var parts = k.Split(':', 2);
                if (parts.Length == 2)
                {
                    try { await _connection.InvokeAsync("SubscribeToBatch", parts[0], parts[1]); }
                    catch { /* best-effort */ }
                }
            }
        };

        try
        {
            await _connection.StartAsync(ct);
            _logger.LogInformation("SignalR connected to {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR connection failed — events will arrive via polling only");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Key(string env, string runId) => $"{env}:{runId}";

    private async Task UnsubscribeAsync(string env, string runId, Func<PerformanceEvent, Task> handler)
    {
        var key = Key(env, runId);
        await _lock.WaitAsync();
        try
        {
            if (_handlers.TryGetValue(key, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0) _handlers.Remove(key);
            }
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch { /* best-effort */ }
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _action;
        public Unsubscriber(Action action) => _action = action;
        public void Dispose() => _action();
    }
}
