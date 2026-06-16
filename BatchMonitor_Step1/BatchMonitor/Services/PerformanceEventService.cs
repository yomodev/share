using BatchMonitor.Models;
using System.Threading.Tasks;

namespace BatchMonitor.Services;

/// <summary>
/// Handles polling for events and maintains the in-memory event store.
/// Coordinates with the batch service to fetch incremental events.
/// </summary>
public class PerformanceEventService : IDisposable
{
    /// <summary>Poll interval while the owning tab is focused.</summary>
    public const int FocusedPollIntervalMs = 2000;

    /// <summary>Poll interval while the owning tab is unfocused (§9 throttle).</summary>
    public const int UnfocusedPollIntervalMs = 10_000;

    private readonly IBatchService _batchService;
    private readonly PerformanceEventStore _eventStore;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private Action? _onEventsUpdated;

    /// <summary>
    /// Whether the owning tab is currently focused. The poll loop reads this
    /// on each iteration to choose between <see cref="FocusedPollIntervalMs"/>
    /// and <see cref="UnfocusedPollIntervalMs"/> — no restart needed when this
    /// changes (§9 "restore on focus").
    /// </summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;

            // Refocusing should resume fast polling immediately rather than
            // waiting out an in-flight 10s unfocused delay.
            if (value)
            {
                _focusRegainedSignal?.TrySetResult();
            }
        }
    }
    private volatile bool _isFocused = true;
    private TaskCompletionSource? _focusRegainedSignal;

    public PerformanceEventService(IBatchService batchService)
    {
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        _eventStore = new PerformanceEventStore();
    }

    /// <summary>
    /// Returns a read-only snapshot of the current event store.
    /// </summary>
    public IReadOnlyDictionary<string, PerformanceEvent> Events => _eventStore.Snapshot;

    /// <summary>
    /// Current event count in store.
    /// </summary>
    public int EventCount => _eventStore.Count;

    /// <summary>
    /// Starts polling for events from a batch.
    /// On first call (from=batchStart), loads full history.
    /// Subsequent polls use lastEventTimestamp to fetch only new/updated events.
    /// The poll cadence adapts to <see cref="IsFocused"/> on each iteration.
    /// </summary>
    public async Task StartPollingAsync(
        string env,
        string runId,
        DateTime batchStartTime,
        bool isFocused = true,
        Action? onEventsUpdated = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(env) || string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("env and runId are required");

        _onEventsUpdated = onEventsUpdated;
        IsFocused = isFocused;

        // Stop any existing polling
        StopPolling();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Initial load: fetch full history from batch start time
            await LoadEventsAsync(env, runId, batchStartTime, _cts.Token);

            // Start polling loop for incremental updates
            _pollingTask = PollLoopAsync(env, runId, _cts.Token);
        }
        catch
        {
            _cts?.Dispose();
            _cts = null;
            throw;
        }
    }

    /// <summary>
    /// Stops the polling loop. Cancellation is signalled immediately and the
    /// loop exits on its own — this method does not block waiting for it
    /// (a blocking wait here previously caused a multi-second UI freeze on
    /// tab close, especially when the loop was mid-delay in unfocused/10s
    /// throttle mode).
    /// </summary>
    public void StopPolling()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        // Observe any exception from the polling task without blocking, so
        // it doesn't surface as an unobserved task exception.
        if (_pollingTask is not null)
        {
            var task = _pollingTask;
            _pollingTask = null;
            _ = task.ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    Console.WriteLine($"[EventService] Polling task ended with error: {t.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Clears all accumulated events (useful for testing or resetting).
    /// </summary>
    public void ClearEvents()
    {
        _eventStore.Clear();
    }

    // ── Private ──────────────────────────────────────────────────────

    private async Task LoadEventsAsync(string env, string runId, DateTime from, CancellationToken ct)
    {
        try
        {
            var events = await _batchService.GetBatchEventsAsync(env, runId, from, ct);
            if (events?.Count > 0)
            {
                _eventStore.UpsertEvents(events);
                _onEventsUpdated?.Invoke();
                Console.WriteLine($"[EventService] Loaded {events.Count} events for {runId} from {from:O}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[EventService] Error loading events: {ex.Message}");
        }
    }

    private async Task PollLoopAsync(string env, string runId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var intervalMs = IsFocused ? FocusedPollIntervalMs : UnfocusedPollIntervalMs;

                if (!IsFocused)
                {
                    // Allow a refocus to interrupt the long unfocused delay
                    // so polling resumes promptly (§9 "restore on focus").
                    _focusRegainedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var delayTask = Task.Delay(intervalMs, ct);
                    await Task.WhenAny(delayTask, _focusRegainedSignal.Task);
                    _focusRegainedSignal = null;
                }
                else
                {
                    await Task.Delay(intervalMs, ct);
                }

                if (ct.IsCancellationRequested) break;

                // Fetch new/updated events since last poll
                var lastTs = _eventStore.LastEventTimestamp ?? DateTime.UtcNow.AddMinutes(-10);
                var events = await _batchService.GetBatchEventsAsync(env, runId, lastTs, ct);

                if (events?.Count > 0)
                {
                    _eventStore.UpsertEvents(events);
                    _onEventsUpdated?.Invoke();
                    Console.WriteLine($"[EventService] Polled {events.Count} new/updated events, total now: {_eventStore.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EventService] Error during poll: {ex.Message}");
            }
        }

        Console.WriteLine($"[EventService] Polling stopped for {runId}");
    }

    public void Dispose()
    {
        StopPolling();
    }
}
