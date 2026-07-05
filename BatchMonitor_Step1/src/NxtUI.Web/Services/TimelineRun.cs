using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Per-run data container for the Timeline tab.
/// Owns the event store, SignalR subscription, and 3h timeout for live runs.
/// </summary>
public class TimelineRun(
    string env,
    string runId,
    IRunService runService,
    SignalRConnectionService signalR,
    Func<Task> onUpdated) : IAsyncDisposable
{
    public string RunId { get; } = runId;
    public string Env { get; } = env;
    public string Description { get; internal set; } = string.Empty;
    public bool IsLive { get; internal set; }
    public DateTime RunStart { get; internal set; }

    private readonly Dictionary<string, PerformanceEvent> _events = new();
    private readonly object _lock = new();

    private IDisposable? _signalRSub;
    private CancellationTokenSource? _timeoutCts;

    private static readonly TimeSpan LiveTimeout = TimeSpan.FromHours(3);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(30);

    public IReadOnlyList<PerformanceEvent> Events
    {
        get { lock (_lock) { return _events.Values.OrderBy(e => e.Start).ToList(); } }
    }

    // ── Load ──────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        var details = await runService.GetRunDetailsAsync(Env, RunId);
        Description = details.Description;
        RunStart = details.Start;
        IsLive = details.Status == RunStatus.Running;

        var events = await runService.GetRunEventsAsync(Env, RunId, details.Start);
        UpsertEvents(events);

        if (IsLive)
            await StartLiveSubscriptionAsync();
    }

    public async Task RefreshAsync(IRunService svc)
    {
        var events = await svc.GetRunEventsAsync(Env, RunId, RunStart);
        UpsertEvents(events);
    }

    // ── Live subscription ─────────────────────────────────────────────────

    private async Task StartLiveSubscriptionAsync()
    {
        try
        {
            _signalRSub = await signalR.SubscribeToRunAsync(Env, RunId, OnRunEvent);

            _timeoutCts = new CancellationTokenSource();
            _ = StatusWatchLoopAsync(_timeoutCts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelineRun] SignalR subscription failed for {RunId}: {ex.Message}");
        }
    }

    /// <summary>
    /// While live, periodically re-checks the run's actual status (no backend pushes a
    /// completion event today — see IRunService docs) and stops the subscription as soon
    /// as it's no longer Running, instead of only on the 3h hard timeout.
    /// </summary>
    private async Task StatusWatchLoopAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LiveTimeout;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(StatusPollInterval, ct);
                if (ct.IsCancellationRequested) break;

                if (DateTime.UtcNow >= deadline)
                {
                    await StopLiveAsync("timeout");
                    return;
                }

                try
                {
                    var details = await runService.GetRunDetailsAsync(Env, RunId, ct);
                    if (details.Status != RunStatus.Running)
                    {
                        await StopLiveAsync($"status={details.Status}");
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Transient error — don't stop the run on one failed check, just retry next tick.
                    Console.WriteLine($"[TimelineRun] Status check failed for {RunId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task StopLiveAsync(string reason)
    {
        IsLive = false;
        _signalRSub?.Dispose();
        _signalRSub = null;
        Console.WriteLine($"[TimelineRun] Live subscription stopped for {RunId} ({reason})");
        await onUpdated();
    }

    private async Task OnRunEvent(PerformanceEvent evt)
    {
        UpsertEvent(evt);
        await onUpdated();
    }

    /// <summary>Public upsert used by CSV import.</summary>
    public void UpsertEventPublic(PerformanceEvent e) => UpsertEvent(e);

    /// <summary>Creates a CSV-imported run with no live subscription or polling.</summary>
    public static TimelineRun CreateFromCsv(string runId, string env, IEnumerable<PerformanceEvent> events)
    {
        var eventList = events.ToList();
        var run = new TimelineRun(env, runId, null!, null!, () => Task.CompletedTask)
        {
            Description = runId,
            RunStart = eventList.Count > 0 ? eventList.Min(e => e.Start) : DateTime.UtcNow,
            IsLive = false,
        };
        foreach (var e in eventList) run.UpsertEvent(e);
        return run;
    }

    // ── Event store ───────────────────────────────────────────────────────

    private void UpsertEvents(IEnumerable<PerformanceEvent>? events)
    {
        if (events is null) return;
        lock (_lock) { foreach (var e in events) UpsertLocked(e); }
    }

    private void UpsertEvent(PerformanceEvent e)
    {
        lock (_lock) { UpsertLocked(e); }
    }

    private void UpsertLocked(PerformanceEvent e)
    {
        if (!_events.TryGetValue(e.Id, out var existing) || e.Timestamp > existing.Timestamp)
            _events[e.Id] = e;
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _signalRSub?.Dispose();

        if (IsLive)
        {
            try { await signalR.UnsubscribeFromRunAsync(Env, RunId); }
            catch (Exception) { }
        }
    }
}
