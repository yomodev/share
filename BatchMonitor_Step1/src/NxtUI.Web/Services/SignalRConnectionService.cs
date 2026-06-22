using NxtUI.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace NxtUI.Services;

/// <summary>
/// Scoped service (one per Blazor circuit) that owns a single SignalR
/// connection to <c>/hubs/run-events</c>.
///
/// Multiple <see cref="RunDetail"/> tabs share this connection — each tab
/// calls <see cref="SubscribeToRunAsync"/> to join its group and registers
/// a callback; the service fans incoming events out to all registered
/// callbacks keyed by (env, runId).
/// </summary>
public class SignalRConnectionService(
    ILogger<SignalRConnectionService> logger,
    NavigationManager nav) : IAsyncDisposable
{
    private HubConnection? _connection;
    private IDisposable?   _eventSubscription;

    private readonly Dictionary<string, List<Func<PerformanceEvent, Task>>> _handlers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Public API ────────────────────────────────────────────────────────

    public async Task<IDisposable> SubscribeToRunAsync(
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

        if (_connection?.State == HubConnectionState.Connected)
        {
            try { await _connection.InvokeAsync("SubscribeToRun", env, runId, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to join SignalR group {Key}", key); }
        }

        return new Unsubscriber(() => _ = UnsubscribeAsync(env, runId, handler));
    }

    public async Task UnsubscribeFromRunAsync(string env, string runId)
    {
        var key = Key(env, runId);
        await _lock.WaitAsync();
        try { _handlers.Remove(key); }
        finally { _lock.Release(); }

        if (_connection?.State == HubConnectionState.Connected)
        {
            try { await _connection.InvokeAsync("UnsubscribeFromRun", env, runId); }
            catch { }
        }
    }

    // ── Connection management ─────────────────────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is not null) return;

        var url = nav.ToAbsoluteUri("/hubs/run-events").ToString();

        _connection = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .Build();

        _eventSubscription = _connection.On<string, string, PerformanceEvent>(
            "RunEvent", async (env, runId, evt) =>
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
                    catch (Exception ex) { logger.LogWarning(ex, "RunEvent handler error"); }
                }
            });

        _connection.Reconnected += async connectionId =>
        {
            logger.LogInformation("SignalR reconnected ({Id})", connectionId);
            await _lock.WaitAsync();
            var keys = _handlers.Keys.ToList();
            _lock.Release();
            foreach (var k in keys)
            {
                var parts = k.Split(':', 2);
                if (parts.Length == 2)
                {
                    try { await _connection.InvokeAsync("SubscribeToRun", parts[0], parts[1]); }
                    catch { }
                }
            }
        };

        try
        {
            await _connection.StartAsync(ct);
            logger.LogInformation("SignalR connected to {Url}", url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SignalR connection failed — events will arrive via polling only");
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
            catch { }
        }
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
