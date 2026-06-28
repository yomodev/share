using NxtUI.Core.Services;
using NxtUI.Models;
using System.Threading.Tasks;

namespace NxtUI.Services;

/// <summary>
/// Per-tab event accumulator. Combines two delivery paths (Step 9):
///
///   1. HTTP polling (fallback, always active): loads historical events on
///      startup; polls incrementally for completed batches where SignalR
///      push is not available.
///
///   2. SignalR push (primary for running batches): low-latency event delivery
///      from the server via <see cref="SignalRConnectionService"/>. When push
///      is active, the polling interval is relaxed to a slow fallback rate.
///
/// Focus-aware: while the owning tab is unfocused, polling slows to 10 s and
/// SignalR events are still accumulated (the component just won't re-render
/// until it regains focus — see BatchDetail.ShouldRender).
/// </summary>
public class PerformanceEventService : IDisposable
{
    public const int FocusedPollIntervalMs   = 3_000;
    public const int UnfocusedPollIntervalMs = 15_000;
    // When SignalR push is active, poll much less frequently (safety net only).
    public const int SignalRFallbackPollMs   = 30_000;

    private readonly IRunService _runService;
    private readonly PerformanceEventStore _eventStore;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private Action? _onEventsUpdated;
    private IDisposable? _signalRSubscription;
    private bool _signalRActive;

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
        _eventStore   = new PerformanceEventStore();
    }

    public IReadOnlyDictionary<string, PerformanceEvent> Events => _eventStore.Snapshot;
    public int EventCount => _eventStore.Count;

    // ── Startup ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads full history from run start, then subscribes to SignalR push
    /// (live runs) and starts a fallback polling loop.
    /// </summary>
    public async Task StartAsync(
        string env,
        string runId,
        DateTime startTime,
        bool isRunning,
        bool isFocused = true,
        Action? onEventsUpdated = null,
        SignalRConnectionService? signalR = null,
        CancellationToken ct = default)
    {
        _onEventsUpdated = onEventsUpdated;
        IsFocused        = isFocused;

        StopPolling();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 1. Load full history.
        await LoadHistoryAsync(env, runId, startTime, _cts.Token);

        // 2. Subscribe to SignalR push for running batches.
        if (isRunning && signalR is not null)
        {
            try
            {
                _signalRSubscription = await signalR.SubscribeToRunAsync(
                    env, runId, OnSignalREvent, _cts.Token);
                _signalRActive = true;
                Console.WriteLine($"[EventService] SignalR push active for {runId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EventService] SignalR subscription failed, using polling only: {ex.Message}");
            }
        }

        // 3. Start polling loop (always — safety net / completed batch support).
        _pollingTask = PollLoopAsync(env, runId, _cts.Token);
    }

    // ── SignalR push handler ──────────────────────────────────────────────

    private async Task OnSignalREvent(PerformanceEvent evt)
    {
        _eventStore.UpsertEvent(evt);
        _onEventsUpdated?.Invoke();
    }

    // ── Polling loop ──────────────────────────────────────────────────────

    public void StopPolling()
    {
        _signalRSubscription?.Dispose();
        _signalRSubscription = null;
        _signalRActive       = false;

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
                if (_signalRActive)
                    interval = SignalRFallbackPollMs;
                else if (!IsFocused)
                    interval = UnfocusedPollIntervalMs;
                else
                    interval = FocusedPollIntervalMs;

                if (!IsFocused && !_signalRActive)
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

                var from   = _eventStore.LastEventTimestamp ?? DateTime.UtcNow.AddMinutes(-10);
                var events = await _runService.GetRunEventsAsync(env, runId, from, ct);
                if (events?.Count > 0)
                {
                    _eventStore.UpsertEvents(events);
                    _onEventsUpdated?.Invoke();
                    Console.WriteLine($"[EventService] Poll: {events.Count} events, total={_eventStore.Count}");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[EventService] Poll error: {ex.Message}"); }
        }
    }

    public void Dispose() => StopPolling();
}
