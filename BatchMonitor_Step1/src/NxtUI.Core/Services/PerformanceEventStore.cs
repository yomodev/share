using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// In-memory event store that accumulates PerformanceEvents
/// keyed by (name, service, processId).
/// Thread-safe for concurrent polling/updates.
/// </summary>
public class PerformanceEventStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PerformanceEvent> _events = new();
    private DateTime? _lastEventTimestamp;

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
    /// Timestamp of the most recent event, or null if empty. Maintained incrementally
    /// on upsert rather than scanning every event on each access — safe because a key's
    /// timestamp only ever moves forward (see UpsertEvent) and keys are never removed
    /// except by Clear(), so the running max stays correct without a rescan.
    /// </summary>
    public DateTime? LastEventTimestamp
    {
        get { lock (_lock) return _lastEventTimestamp; }
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
            if (!_events.TryGetValue(evt.Id, out var existing) || evt.Timestamp > existing.Timestamp)
            {
                _events[evt.Id] = evt;
                if (_lastEventTimestamp is null || evt.Timestamp > _lastEventTimestamp)
                    _lastEventTimestamp = evt.Timestamp;
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
            _lastEventTimestamp = null;
        }
    }
}
