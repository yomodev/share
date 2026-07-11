namespace NxtUI.Core.Events;

/// <summary>
/// A pollable source of <see cref="RunEvent"/>s for one run. Every backend (Mongo
/// PerformanceTracker, a SQL log table, SSISDB, log files) implements this so the monitor
/// can treat them uniformly and merge several at once for a single run (a run can span
/// services that log to different places).
///
/// The contract is <b>resumable pull</b>: the caller passes back the <see cref="EventCursor"/>
/// from the previous poll, and the source returns only what's new since then plus an updated
/// cursor. This is what lets polling "resume from the last reported event" — the requirement
/// that shaped the whole model. Sources are also fine to call offline (a finished run): they
/// just return everything up to the end and a terminal cursor.
/// </summary>
public interface IEventSource
{
    /// <summary>Human-readable id for logs/diagnostics (e.g. "mongo-perf", "sql-log", "ssisdb").</summary>
    string Name { get; }

    /// <summary>
    /// Returns events that appeared after <paramref name="cursor"/> (or from the start when
    /// it's null), newest-cursor-last, together with the cursor to pass on the next call.
    /// An empty batch with an unchanged cursor means "nothing new yet".
    /// </summary>
    Task<EventBatch> PollAsync(string runId, EventCursor? cursor, CancellationToken ct = default);
}

/// <summary>
/// Opaque resume token. Different sources advance a different field — a timestamp
/// (PerformanceTracker), an IDENTITY id (a SQL log table), an <c>event_message_id</c>
/// (SSISDB) — so both are carried and each source reads whichever it uses.
/// </summary>
public sealed record EventCursor(long? LastId = null, DateTime? LastTimestampUtc = null)
{
    public static readonly EventCursor Empty = new();
}

/// <summary>One poll's worth of events plus the cursor to resume from next time.</summary>
public sealed record EventBatch(IReadOnlyList<RunEvent> Events, EventCursor Cursor)
{
    public static EventBatch Empty(EventCursor cursor) => new([], cursor);
}
