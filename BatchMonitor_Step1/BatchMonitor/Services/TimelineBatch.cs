using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Per-batch data container for the Timeline tab.
/// Owns the event store, SignalR subscription, and 3h timeout for running batches.
/// </summary>
public class TimelineBatch : IAsyncDisposable
{
    public string RunId { get; }
    public string Env   { get; }
    public string BatchName  { get; internal set; } = string.Empty;
    public bool   IsLive     { get; internal set; }
    public DateTime BatchStart { get; internal set; }

    private readonly IBatchService _batchService;
    private readonly SignalRConnectionService _signalR;
    private readonly Func<Task> _onUpdated;
    private readonly Dictionary<string, PerformanceEvent> _events = new();
    private readonly object _lock = new();

    private IDisposable? _signalRSub;
    private CancellationTokenSource? _timeoutCts;

    // 3h timeout per spec §11
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromHours(3);

    public TimelineBatch(string env, string runId,
        IBatchService batchService,
        SignalRConnectionService signalR,
        Func<Task> onUpdated)
    {
        Env            = env;
        RunId          = runId;
        _batchService  = batchService;
        _signalR       = signalR;
        _onUpdated     = onUpdated;
    }

    public IReadOnlyList<PerformanceEvent> Events
    {
        get { lock (_lock) { return _events.Values.OrderBy(e => e.Start).ToList(); } }
    }

    // ── Load ──────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        var details = await _batchService.GetBatchDetailsAsync(Env, RunId);
        BatchName  = details.BatchName;
        BatchStart = details.Start;
        IsLive     = details.Status == BatchStatus.Running;

        // Load historical events.
        var events = await _batchService.GetBatchEventsAsync(Env, RunId, details.Start);
        UpsertEvents(events);

        // Subscribe to live push for running batches.
        if (IsLive)
        {
            await StartLiveSubscriptionAsync();
        }
    }

    public async Task RefreshAsync(IBatchService batchService)
    {
        var events = await batchService.GetBatchEventsAsync(Env, RunId, BatchStart);
        UpsertEvents(events);
    }

    // ── Live subscription ─────────────────────────────────────────────────

    private async Task StartLiveSubscriptionAsync()
    {
        try
        {
            _signalRSub = await _signalR.SubscribeToBatchAsync(
                Env, RunId, OnBatchEvent);

            // 3h timeout: stop live subscription, freeze display.
            _timeoutCts = new CancellationTokenSource();
            _ = Task.Delay(LiveTimeout, _timeoutCts.Token).ContinueWith(async t =>
            {
                if (!t.IsCanceled)
                {
                    IsLive = false;
                    _signalRSub?.Dispose();
                    _signalRSub = null;
                    Console.WriteLine($"[TimelineBatch] Live timeout for {RunId}");
                    await _onUpdated();
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelineBatch] SignalR subscription failed for {RunId}: {ex.Message}");
        }
    }

    private async Task OnBatchEvent(PerformanceEvent evt)
    {
        UpsertEvent(evt);
        await _onUpdated();
    }

    /// <summary>Public upsert used by CSV import.</summary>
    public void UpsertEventPublic(PerformanceEvent e) => UpsertEvent(e);

    /// <summary>Creates a CSV-imported batch with no live subscription or polling.</summary>
    public static TimelineBatch CreateFromCsv(string runId, string env, IEnumerable<PerformanceEvent> events)
    {
        // Use dummy services — CSV batches never poll or subscribe.
        var batch = new TimelineBatch(env, runId, null!, null!, () => Task.CompletedTask)
        {
            BatchName  = runId,
            BatchStart = DateTime.UtcNow,
            IsLive     = false,
        };
        foreach (var e in events) batch.UpsertEvent(e);
        return batch;
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
        // Key by ChunkId — last version (newest Timestamp) wins per spec.
        if (!_events.TryGetValue(e.ChunkId, out var existing) || e.Timestamp > existing.Timestamp)
            _events[e.ChunkId] = e;
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _signalRSub?.Dispose();

        if (IsLive)
        {
            try { await _signalR.UnsubscribeFromBatchAsync(Env, RunId); }
            catch { }
        }
    }
}
