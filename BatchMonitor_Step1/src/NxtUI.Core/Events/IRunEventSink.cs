namespace NxtUI.Core.Events;

/// <summary>
/// Producer-side destination for events emitted BY a run (a C# service, a DTSX Script Task,
/// etc.). Where <see cref="IEventSource"/> is how the monitor READS events, this is how a
/// participant WRITES them. Implementations persist to wherever the monitor later polls —
/// e.g. a SQL log table (<see cref="Sql.SqlRunEventSink"/>), Kafka, or a Mongo collection.
/// </summary>
public interface IRunEventSink
{
    /// <summary>Persists a batch of events. Should be safe to call concurrently.</summary>
    Task WriteAsync(IReadOnlyList<RunEvent> events, CancellationToken ct = default);
}
