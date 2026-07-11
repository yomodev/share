using System.Collections.Concurrent;
using NxtUI.Core.Events;

namespace NxtUI.Tests.Events;

/// <summary>
/// Reusable test double for <see cref="IEventSource"/>. Events are appended via
/// <see cref="Enqueue"/> (simulating a source that gains new data over time) and each is
/// assigned a monotonic sequence number; <see cref="PollAsync"/> returns everything after
/// the cursor's <see cref="EventCursor.LastId"/>, mirroring the resumable-cursor contract
/// any real source (SQL log table, SSISDB) implements, without needing real infrastructure.
///
/// Not tied to any specific event shape — seed it with whatever RunEvents a test scenario
/// needs (Connect/Disconnect/Produce/Consume/Process/Error/Info), independent of
/// PerformanceEventSource's Mongo-specific timestamp-cursor semantics.
/// </summary>
public sealed class FakeEventSource(string name = "fake") : IEventSource
{
    private readonly ConcurrentQueue<RunEvent> _all = new();

    public string Name => name;

    /// <summary>Total events ever enqueued, regardless of what's been polled.</summary>
    public int TotalEnqueued => _all.Count;

    /// <summary>How many times <see cref="PollAsync"/> has been called — lets a test assert polling actually happened.</summary>
    public int PollCount { get; private set; }

    /// <summary>Set to make the next PollAsync call throw, to test a consumer's error handling.</summary>
    public Exception? ThrowOnNextPoll { get; set; }

    /// <summary>Appends events as if they'd just become available from the underlying source.</summary>
    public void Enqueue(params RunEvent[] events)
    {
        foreach (var e in events)
            _all.Enqueue(e);
    }

    public Task<EventBatch> PollAsync(string runId, EventCursor? cursor, CancellationToken ct = default)
    {
        PollCount++;

        if (ThrowOnNextPoll is { } ex)
        {
            ThrowOnNextPoll = null;
            throw ex;
        }

        var lastId = cursor?.LastId ?? 0;
        var snapshot = _all.ToArray(); // index in the queue IS the sequence id (1-based)
        var newEvents = new List<RunEvent>();
        var maxId = lastId;

        for (var i = 0; i < snapshot.Length; i++)
        {
            var id = i + 1;
            if (id <= lastId) continue;
            newEvents.Add(snapshot[i]);
            maxId = id;
        }

        return Task.FromResult(new EventBatch(newEvents, new EventCursor(LastId: maxId)));
    }
}
