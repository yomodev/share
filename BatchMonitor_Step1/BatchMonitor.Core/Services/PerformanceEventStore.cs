using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// In-memory event store that accumulates PerformanceEvents
/// keyed by (chunkId, service, processId).
/// Thread-safe for concurrent polling/updates.
/// </summary>
public class PerformanceEventStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PerformanceEvent> _events = new();

    /// <summary>
    /// Returns a read-only snapshot of all events.
    /// </summary>
    public IReadOnlyDictionary<string, PerformanceEvent> Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, PerformanceEvent>(_events);
            }
        }
    }

    /// <summary>
    /// Total number of unique events in the store.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>
    /// Timestamp of the most recent event, or null if empty.
    /// </summary>
    public DateTime? LastEventTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _events.Values.MaxBy(e => e.Timestamp)?.Timestamp;
            }
        }
    }

    /// <summary>
    /// Upsert or merge an event by its composite key.
    /// If the key exists, the newer event (by timestamp) replaces it.
    /// </summary>
    public void UpsertEvent(PerformanceEvent evt)
    {
        if (evt is null) return;

        lock (_lock)
        {
            var key = evt.CompositeKey;
            if (!_events.TryGetValue(key, out var existing) || evt.Timestamp > existing.Timestamp)
            {
                _events[key] = evt;
            }
        }
    }

    /// <summary>
    /// Merge multiple events at once (batch upsert).
    /// </summary>
    public void UpsertEvents(IEnumerable<PerformanceEvent> events)
    {
        if (events is null) return;

        foreach (var evt in events)
        {
            UpsertEvent(evt);
        }
    }

    /// <summary>
    /// Clear all events (for testing or reset).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}
