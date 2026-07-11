using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Per-run data container for the Timeline tab.
/// Owns the event store, live-push subscription, and 3h timeout for live runs.
/// </summary>
public class TimelineRun(
    string env,
    string runId,
    IRunService runService,
    RunEventBroker eventBroker,
    Func<Task> onUpdated,
    ILogger<TimelineRun>? logger = null) : IAsyncDisposable
{
    private readonly ILogger<TimelineRun> _log = logger ?? NullLogger<TimelineRun>.Instance;

    public string RunId { get; } = runId;
    public string Env { get; } = env;
    public string Description { get; internal set; } = string.Empty;
    public bool IsLive { get; internal set; }
    public DateTime RunStart { get; internal set; }

    private readonly Dictionary<string, PerformanceEvent> _events = new();
    private readonly object _lock = new();

    // Cached sorted snapshot — Events was previously an OrderBy().ToList() on every
    // access (a full O(n log n) sort + copy), and TimelineTab's control bar reads
    // .Events.Count on every render just to show the total, making that sort/copy run
    // per render for the whole app, not just per data update. Invalidated only when
    // the store actually changes (UpsertLocked), so repeated reads between updates —
    // including the render-time .Count access — are O(1).
    private List<PerformanceEvent>? _sortedCache;

    private IDisposable? _pushSub;
    private CancellationTokenSource? _timeoutCts;

    private static readonly TimeSpan LiveTimeout = TimeSpan.FromHours(3);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(30);

    public IReadOnlyList<PerformanceEvent> Events
    {
        get
        {
            lock (_lock)
            {
                _sortedCache ??= _events.Values.OrderBy(e => e.Start).ToList();
                return _sortedCache;
            }
        }
    }

    /// <summary>Cheap count for display (e.g. the control-bar total) — avoids the
    /// sort/copy that reading .Events.Count would otherwise trigger on every render.</summary>
    public int EventCount { get { lock (_lock) return _events.Count; } }

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
            _pushSub = await eventBroker.SubscribeToRunAsync(Env, RunId, OnRunEvent);

            _timeoutCts = new CancellationTokenSource();
            _ = StatusWatchLoopAsync(_timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Live push subscription failed for {RunId}", RunId);
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
                    _log.LogWarning(ex, "Status check failed for {RunId}", RunId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task StopLiveAsync(string reason)
    {
        IsLive = false;
        _pushSub?.Dispose();
        _pushSub = null;
        _log.LogInformation("Live subscription stopped for {RunId} ({Reason})", RunId, reason);
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
        {
            _events[e.Id] = e;
            _sortedCache = null;
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _pushSub?.Dispose();
        return ValueTask.CompletedTask;
    }
}
