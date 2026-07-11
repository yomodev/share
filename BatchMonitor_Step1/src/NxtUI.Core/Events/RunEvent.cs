namespace NxtUI.Core.Events;

/// <summary>
/// The generalized <c>Actor → Message → Target</c> event that every source (Mongo
/// PerformanceTracker, a SQL log table, SSISDB, log files, a live C# service) normalizes
/// into. <c>NxtUI.Core.Models.PerformanceEvent</c> is one specific shape of this — see
/// <c>PerformanceEventBridge</c> for the mapping back into the existing store/topology
/// pipeline, which lets the whole new model reuse the current Run Detail UI unchanged.
/// (The bridge lives in a separate adapter, not here, so this general model carries no
/// dependency on the legacy specific one.)
///
/// A record is either a <b>point event</b> (<see cref="FinishUtc"/> null — e.g. a produce,
/// a connect, an error) or a <b>span</b> (<see cref="FinishUtc"/> set — a unit of work that
/// ran from <see cref="TimestampUtc"/> to <see cref="FinishUtc"/>). Spans are first-class:
/// span-shaped sources (PerformanceTracker) don't get decomposed into two events.
/// </summary>
public sealed class RunEvent
{
    /// <summary>Which run this belongs to.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    /// Unique per step-attempt. Used as the upsert/dedup key so a later "finish" record for
    /// the same work replaces its earlier "start" (mirrors PerformanceEvent's CompositeKey).
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// The business correlation id — the batch/chunk/message that flows ACROSS actors. This
    /// is what ties a produce on one actor to a consume on another (the "Name" in
    /// PerformanceEvent). Null for lifecycle events (connect/disconnect) that carry no message.
    /// </summary>
    public string? CorrelationId { get; init; }

    // ── Actor ─────────────────────────────────────────────────────────────────
    public string Service { get; init; } = string.Empty;
    public string Server { get; init; } = string.Empty;
    public int ProcessId { get; init; }

    /// <summary>Pipeline within the service. Empty ⇒ the service's single default pipeline.</summary>
    public string Pipeline { get; init; } = string.Empty;

    // ── What happened ──────────────────────────────────────────────────────────
    public EventKind Kind { get; init; }

    /// <summary>The task/component/handler that acted (e.g. "DFT Extract", "OrderParser").</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Where the actor acted — the queue/topic/table/file/SP a message was produced to or
    /// consumed from. Two actors referencing the same Target is what makes them connected
    /// (producer→Target→consumer), and produced-minus-consumed at a Target is the lag.
    /// Null for events with no location (e.g. a pure Process span, connect/disconnect).
    /// </summary>
    public string? Target { get; init; }

    /// <summary>When it happened, or when a span started. UTC.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Set ⇒ this is a completed span (work ran until here). Null ⇒ point event or in-progress.</summary>
    public DateTime? FinishUtc { get; init; }

    // ── Payload / metrics ──────────────────────────────────────────────────────
    public EventStatus Status { get; init; } = EventStatus.None;

    /// <summary>
    /// Rows moved on a <see cref="EventKind.Produce"/> / <see cref="EventKind.Consume"/> (or
    /// processed by a span). The basis for precise lag at a Target: produced − consumed.
    /// </summary>
    public long? RecordCount { get; init; }

    public string? Error { get; init; }
    public string? Info { get; init; }

    public bool IsError => Kind == EventKind.Error || Status == EventStatus.Failed || !string.IsNullOrEmpty(Error);
}

/// <summary>
/// What an event notifies. Produce/Consume move a message to/from a <see cref="RunEvent.Target"/>;
/// Connect/Disconnect mark an actor becoming ready / finishing; Process is a unit of work that
/// ran (a span — the PerformanceEvent shape); Error and Info are self-explanatory.
/// </summary>
public enum EventKind
{
    /// <summary>Actor is up and ready (block appears in the UI). No message.</summary>
    Connect,

    /// <summary>Actor finished / shut down / went idle. No message.</summary>
    Disconnect,

    /// <summary>A message/batch was produced (sent/written) to <see cref="RunEvent.Target"/>.</summary>
    Produce,

    /// <summary>A message/batch was consumed (received/read) from <see cref="RunEvent.Target"/>.</summary>
    Consume,

    /// <summary>A unit of work ran on this actor/pipeline — a span. The PerformanceEvent shape.</summary>
    Process,

    /// <summary>A failure. Carries <see cref="RunEvent.Error"/>.</summary>
    Error,

    /// <summary>A free-form annotation (e.g. a precedence/branch decision). Carries <see cref="RunEvent.Info"/>.</summary>
    Info,
}

/// <summary>Outcome of a completed span / finished work. <see cref="None"/> = not applicable (point events, in-progress).</summary>
public enum EventStatus
{
    None,
    Success,
    Failed,
    Skipped,
}
