using NxtUI.Core.Models;

namespace NxtUI.Core.Events;

/// <summary>
/// Maps the generalized <see cref="RunEvent"/> back into the legacy
/// <see cref="PerformanceEvent"/> shape so the new event pipeline can feed the existing
/// PerformanceEventStore + TopologyComputationService (and the d3 graph/timeline) with no
/// changes. Kept as a standalone adapter — not a method on <see cref="RunEvent"/> — so the
/// general model carries no dependency on the legacy specific one (the specific knows the
/// general, not the reverse).
/// </summary>
public static class PerformanceEventBridge
{
    /// <summary>
    /// Converts a "work" event (<see cref="EventKind.Process"/>/<see cref="EventKind.Consume"/>/
    /// <see cref="EventKind.Produce"/>) carrying a correlation id into a PerformanceEvent.
    /// Lifecycle/annotation events (connect/disconnect/info) and correlation-less events return
    /// null — they're handled by the newer consumers (node liveness, the errors dialog) rather
    /// than the chunk-flow topology.
    /// </summary>
    public static PerformanceEvent? ToPerformanceEvent(RunEvent e)
    {
        if (e.Kind is not (EventKind.Process or EventKind.Consume or EventKind.Produce)) return null;
        if (string.IsNullOrEmpty(e.CorrelationId)) return null;

        return new PerformanceEvent
        {
            Id = e.EventId,
            Name = e.CorrelationId,
            Service = e.Service,
            Pipeline = e.Pipeline,
            Server = e.Server,
            ProcessId = e.ProcessId,
            Start = e.TimestampUtc,
            Finish = e.FinishUtc,
            Error = e.Error,
            Source = e.Source,
            RecordCount = (int)Math.Clamp(e.RecordCount ?? 0, 0, int.MaxValue),
            Info = e.Info ?? string.Empty,
            LastUpdate = e.FinishUtc ?? e.TimestampUtc,
        };
    }
}
