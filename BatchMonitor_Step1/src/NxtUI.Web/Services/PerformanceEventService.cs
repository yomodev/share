using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NxtUI.Core.Events;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Per-tab event accumulator. Combines two delivery paths (Step 9):
///
///   1. HTTP polling (fallback, always active): loads historical events on
///      startup; polls incrementally for completed batches where live push
///      is not available. Goes through a <see cref="PerformanceEventSource"/>
///      (an <see cref="IEventSource"/>) rather than calling
///      <see cref="IRunService.GetRunEventsAsync"/> directly — the resume
///      point is an <see cref="EventCursor"/>, not a raw timestamp, and each
///      returned <see cref="RunEvent"/> is bridged back into a
///      <see cref="PerformanceEvent"/> via <see cref="PerformanceEventBridge"/>
///      before landing in the store below. This is the first live consumer of
///      the generalized event model — see docs/10 and docs/11.
///
///   2. Live push (primary for running batches): low-latency event delivery
///      via <see cref="RunEventBroker"/>, an in-process pub/sub. When push
///      is active, the polling interval is relaxed to a slow fallback rate.
///      (Push stays on the legacy PerformanceEvent shape — it's a distinct
///      low-latency channel, not itself an IEventSource.)
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
    private readonly ILogger<PerformanceEventService> _log;
    private readonly PerformanceEventStore _eventStore;
    private IEventSource? _eventSource;
    private EventCursor? _cursor;
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

    public PerformanceEventService(IRunService runService, ILogger<PerformanceEventService>? logger = null)
    {
        _runService = runService ?? throw new ArgumentNullException(nameof(runService));
        _log = logger ?? NullLogger<PerformanceEventService>.Instance;
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

        _eventSource = new PerformanceEventSource(_runService, env, startTime);
        _cursor = null;
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
                _log.LogDebug("Live push active for {RunId}", runId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Push subscription failed for {RunId}, using polling only", runId);
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
                    _log.LogError(task.Exception.GetBaseException(), "Poll task error");
            }, TaskScheduler.Default);
        }
    }

    public void ClearEvents() => _eventStore.Clear();

    // ── Private ───────────────────────────────────────────────────────────

    private async Task LoadHistoryAsync(string env, string runId, DateTime from, CancellationToken ct)
    {
        try
        {
            var count = await PollSourceAsync(runId, ct);
            if (count > 0)
                _log.LogDebug("Loaded {Count} historical events for {RunId}", count, runId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "History load error for {RunId}", runId); }
    }

    /// <summary>Polls <see cref="_eventSource"/> from <see cref="_cursor"/>, bridges the
    /// returned RunEvents into PerformanceEvents, upserts them, and advances the cursor.
    /// Returns how many events were bridged (0 = "nothing new").</summary>
    private async Task<int> PollSourceAsync(string runId, CancellationToken ct)
    {
        if (_eventSource is null) return 0;

        var batch = await _eventSource.PollAsync(runId, _cursor, ct);
        _cursor = batch.Cursor;
        if (batch.Events.Count == 0) return 0;

        var mapped = new List<PerformanceEvent>(batch.Events.Count);
        foreach (var evt in batch.Events)
        {
            var pe = PerformanceEventBridge.ToPerformanceEvent(evt);
            if (pe is not null) mapped.Add(pe);
        }
        if (mapped.Count == 0) return 0;

        _eventStore.UpsertEvents(mapped);
        _onEventsUpdated?.Invoke();
        return mapped.Count;
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

                var count = await PollSourceAsync(runId, ct);
                if (count > 0)
                    _log.LogDebug("Poll: {Count} events, total={Total}", count, _eventStore.Count);

                if (_isRunning && DateTime.UtcNow >= _nextStatusCheck)
                    await CheckRunStatusAsync(env, runId, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Poll error for {RunId}", runId); }
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
                _log.LogInformation("Run {RunId} finished (status={Status}) — stopped live push, falling back to slow poll", runId, details.Status);
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
            _log.LogWarning(ex, "Status check failed for {RunId}", runId);
        }
    }

    public void Dispose() => StopPolling();
}
