using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Per-tab event accumulator. Combines two delivery paths (Step 9):
///
///   1. HTTP polling (fallback, always active): loads historical events on
///      startup; polls incrementally for completed batches where live push
///      is not available.
///
///   2. Live push (primary for running batches): low-latency event delivery
///      via <see cref="RunEventBroker"/>, an in-process pub/sub. When push
///      is active, the polling interval is relaxed to a slow fallback rate.
///
/// Focus-aware: while the owning tab is unfocused, polling slows to 10 s and
/// pushed events are still accumulated (the component just won't re-render
/// until it regains focus — see BatchDetail.ShouldRender).
/// </summary>
public class PerformanceEventService : IDisposable
{
    public const int FocusedPollIntervalMs = 3_000;
    public const int UnfocusedPollIntervalMs = 15_000;
    // When live push is active, poll much less frequently (safety net only).
    public const int PushFallbackPollMs = 30_000;
    // No backend pushes a completion event today — periodically re-check the run's own
    // status while live so push gets torn down (and the UI's status chip updates)
    // as soon as it finishes, instead of only relying on events stopping.
    private static readonly TimeSpan StatusCheckInterval = TimeSpan.FromSeconds(30);

    private readonly IRunService _runService;
    private readonly PerformanceEventStore _eventStore;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private Action? _onEventsUpdated;
    private IDisposable? _pushSubscription;
    private bool _pushActive;
    private bool _isRunning;
    private DateTime _nextStatusCheck;

    /// <summary>
    /// Set once a status re-check discovers the run is no longer Running. Null until then.
    /// Callers (e.g. RunDetail.razor) should read this from their onEventsUpdated callback
    /// and update their own displayed status, since _details is loaded once and never
    /// otherwise refreshed.
    /// </summary>
    public RunStatus? DetectedStatus { get; private set; }

    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            if (value) _focusRegainedSignal?.TrySetResult();
        }
    }
    private volatile bool _isFocused = true;
    private TaskCompletionSource? _focusRegainedSignal;

    public PerformanceEventService(IRunService runService)
    {
        _runService = runService ?? throw new ArgumentNullException(nameof(runService));
        _eventStore = new PerformanceEventStore();
    }

    public IReadOnlyDictionary<string, PerformanceEvent> Events => _eventStore.Snapshot;
    public int EventCount => _eventStore.Count;

    // ── Startup ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads full history from run start, then subscribes to live push
    /// (live runs) and starts a fallback polling loop.
    /// </summary>
    public async Task StartAsync(
        string env,
        string runId,
        DateTime startTime,
        bool isRunning,
        bool isFocused = true,
        Action? onEventsUpdated = null,
        RunEventBroker? eventBroker = null,
        CancellationToken ct = default)
    {
        _onEventsUpdated = onEventsUpdated;
        IsFocused = isFocused;
        _isRunning = isRunning;
        DetectedStatus = null;
        _nextStatusCheck = DateTime.UtcNow + StatusCheckInterval;

        StopPolling();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 1. Load full history.
        await LoadHistoryAsync(env, runId, startTime, _cts.Token);

        // 2. Subscribe to live push for running batches.
        if (isRunning && eventBroker is not null)
        {
            try
            {
                _pushSubscription = await eventBroker.SubscribeToRunAsync(
                    env, runId, OnPushEvent, _cts.Token);
                _pushActive = true;
                Console.WriteLine($"[EventService] Live push active for {runId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EventService] Push subscription failed, using polling only: {ex.Message}");
            }
        }

        // 3. Start polling loop (always — safety net / completed batch support).
        _pollingTask = PollLoopAsync(env, runId, _cts.Token);
    }

    // ── Live push handler ───────────────────────────────────────────────────

    private Task OnPushEvent(PerformanceEvent evt)
    {
        _eventStore.UpsertEvent(evt);
        _onEventsUpdated?.Invoke();
        return Task.CompletedTask;
    }

    // ── Polling loop ──────────────────────────────────────────────────────

    public void StopPolling()
    {
        _pushSubscription?.Dispose();
        _pushSubscription = null;
        _pushActive = false;

        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_pollingTask is not null)
        {
            var t = _pollingTask;
            _pollingTask = null;
            _ = t.ContinueWith(task =>
            {
                if (task.Exception is not null)
                    Console.WriteLine($"[EventService] Poll task error: {task.Exception.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }
    }

    public void ClearEvents() => _eventStore.Clear();

    // ── Private ───────────────────────────────────────────────────────────

    private async Task LoadHistoryAsync(string env, string runId, DateTime from, CancellationToken ct)
    {
        try
        {
            var events = await _runService.GetRunEventsAsync(env, runId, from, ct);
            if (events?.Count > 0)
            {
                _eventStore.UpsertEvents(events);
                _onEventsUpdated?.Invoke();
                Console.WriteLine($"[EventService] Loaded {events.Count} historical events for {runId}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[EventService] History load error: {ex.Message}"); }
    }

    private async Task PollLoopAsync(string env, string runId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int interval;
                if (_pushActive)
                    interval = PushFallbackPollMs;
                else if (!IsFocused)
                    interval = UnfocusedPollIntervalMs;
                else
                    interval = FocusedPollIntervalMs;

                if (!IsFocused && !_pushActive)
                {
                    _focusRegainedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Task.WhenAny(Task.Delay(interval, ct), _focusRegainedSignal.Task);
                    _focusRegainedSignal = null;
                }
                else
                {
                    await Task.Delay(interval, ct);
                }

                if (ct.IsCancellationRequested) break;

                var from = _eventStore.LastEventTimestamp ?? DateTime.UtcNow.AddMinutes(-10);
                var events = await _runService.GetRunEventsAsync(env, runId, from, ct);
                if (events?.Count > 0)
                {
                    _eventStore.UpsertEvents(events);
                    _onEventsUpdated?.Invoke();
                    Console.WriteLine($"[EventService] Poll: {events.Count} events, total={_eventStore.Count}");
                }

                if (_isRunning && DateTime.UtcNow >= _nextStatusCheck)
                    await CheckRunStatusAsync(env, runId, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[EventService] Poll error: {ex.Message}"); }
        }
    }

    private async Task CheckRunStatusAsync(string env, string runId, CancellationToken ct)
    {
        try
        {
            var details = await _runService.GetRunDetailsAsync(env, runId, ct);
            if (details.Status != RunStatus.Running)
            {
                _isRunning = false;
                DetectedStatus = details.Status;
                _pushSubscription?.Dispose();
                _pushSubscription = null;
                _pushActive = false;
                _onEventsUpdated?.Invoke();
                Console.WriteLine($"[EventService] Run {runId} finished (status={details.Status}) — stopped live push, falling back to slow poll.");
            }
            else
            {
                _nextStatusCheck = DateTime.UtcNow + StatusCheckInterval;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Transient error — don't flip status on one failed check, just retry next tick.
            Console.WriteLine($"[EventService] Status check failed for {runId}: {ex.Message}");
        }
    }

    public void Dispose() => StopPolling();
}
