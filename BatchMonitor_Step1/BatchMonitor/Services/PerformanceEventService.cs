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
        Console.WriteLine("[PerformanceEventService] Created new instance");
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

        Console.WriteLine($"[PerformanceEventService.StartPollingAsync] env={env}, runId={runId}, pollIntervalMs={pollIntervalMs}");

        _onEventsUpdated = onEventsUpdated;

        // Stop any existing polling
        StopPolling();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Initial load: fetch full history from batch start time
            Console.WriteLine($"[PerformanceEventService] Starting initial load...");
            await LoadEventsAsync(env, runId, batchStartTime, _cts.Token);
            Console.WriteLine($"[PerformanceEventService] Initial load complete, event count: {EventCount}");

            // Start polling loop for incremental updates
            _pollingTask = PollLoopAsync(env, runId, pollIntervalMs, _cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceEventService.StartPollingAsync] Error: {ex.Message}");
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
            Console.WriteLine($"[PerformanceEventService.LoadEventsAsync] Fetching events from {from:O}");
            var events = await _batchService.GetBatchEventsAsync(env, runId, from, ct);
            
            if (events?.Count > 0)
            {
                Console.WriteLine($"[PerformanceEventService.LoadEventsAsync] Received {events.Count} events");
                _eventStore.UpsertEvents(events);
                _onEventsUpdated?.Invoke();
                Console.WriteLine($"[PerformanceEventService] Loaded {events.Count} events, store now has {EventCount} total");
            }
            else
            {
                Console.WriteLine($"[PerformanceEventService.LoadEventsAsync] No events returned");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[PerformanceEventService.LoadEventsAsync] Operation cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceEventService.LoadEventsAsync] Error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task PollLoopAsync(string env, string runId, int intervalMs, CancellationToken ct)
    {
        Console.WriteLine($"[PerformanceEventService.PollLoopAsync] Starting polling loop (interval: {intervalMs}ms)");
        int pollCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, ct);

                // Fetch new/updated events since last poll
                var lastTs = _eventStore.LastEventTimestamp ?? DateTime.UtcNow.AddMinutes(-10);
                var events = await _batchService.GetBatchEventsAsync(env, runId, lastTs, ct);

                pollCount++;

                if (events?.Count > 0)
                {
                    _eventStore.UpsertEvents(events);
                    _onEventsUpdated?.Invoke();
                    Console.WriteLine($"[PerformanceEventService] Poll #{pollCount}: {events.Count} new events, total now: {EventCount}");
                }
                else
                {
                    if (pollCount % 5 == 0) // Log every 5th empty poll
                        Console.WriteLine($"[PerformanceEventService] Poll #{pollCount}: No new events");
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[PerformanceEventService.PollLoopAsync] Cancelled after {pollCount} polls");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerformanceEventService.PollLoopAsync] Error on poll #{pollCount}: {ex.Message}");
            }
        }

        Console.WriteLine($"[PerformanceEventService] Polling stopped for {runId} after {pollCount} polls");
    }

    public void Dispose()
    {
        Console.WriteLine("[PerformanceEventService] Disposing");
        StopPolling();
    }
}
