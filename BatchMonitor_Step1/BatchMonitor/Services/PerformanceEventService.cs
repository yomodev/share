using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Handles polling for events and maintains the in-memory event store.
/// Coordinates with the batch service to fetch incremental events.
/// </summary>
public class PerformanceEventService : IDisposable
{
    private readonly IBatchService _batchService;
    private readonly PerformanceEventStore _eventStore;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private Action? _onEventsUpdated;

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
    /// </summary>
    public async Task StartPollingAsync(
        string env,
        string runId,
        DateTime batchStartTime,
        int pollIntervalMs = 2000,
        Action? onEventsUpdated = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(env) || string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("env and runId are required");

        _onEventsUpdated = onEventsUpdated;

        // Stop any existing polling
        StopPolling();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Initial load: fetch full history from batch start time
            await LoadEventsAsync(env, runId, batchStartTime, _cts.Token);

            // Start polling loop for incremental updates
            _pollingTask = PollLoopAsync(env, runId, pollIntervalMs, _cts.Token);
        }
        catch
        {
            _cts?.Dispose();
            _cts = null;
            throw;
        }
    }

    /// <summary>
    /// Stops the polling loop and wipes the token.
    /// </summary>
    public void StopPolling()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_pollingTask is not null)
        {
            try { _pollingTask.Wait(TimeSpan.FromSeconds(5)); }
            catch { }
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

    private async Task PollLoopAsync(string env, string runId, int intervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, ct);

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
