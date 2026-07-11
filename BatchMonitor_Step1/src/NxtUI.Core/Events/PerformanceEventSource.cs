using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Core.Events;

/// <summary>
/// The existing Mongo PerformanceTracker feed, expressed as an <see cref="IEventSource"/>.
/// This is the concrete proof that <see cref="PerformanceEvent"/> is just one shape of the
/// generalized model: each PerformanceEvent is a <see cref="EventKind.Process"/> span, and
/// the round-trip <see cref="RunEvent.ToPerformanceEvent"/> feeds the current store/topology
/// with no changes. New sources (SQL log, SSISDB) plug in alongside this one.
///
/// Constructed per (env, run) since <see cref="IRunService.GetRunEventsAsync"/> is env-scoped
/// and time-resumable; the cursor carries the last-seen timestamp.
/// </summary>
public sealed class PerformanceEventSource(IRunService runService, string env, DateTime runStartUtc) : IEventSource
{
    public string Name => "mongo-perf";

    public async Task<EventBatch> PollAsync(string runId, EventCursor? cursor, CancellationToken ct = default)
    {
        var from = cursor?.LastTimestampUtc ?? runStartUtc;
        var events = await runService.GetRunEventsAsync(env, runId, from, ct);
        if (events is null || events.Count == 0)
            return EventBatch.Empty(cursor ?? EventCursor.Empty);

        var mapped = new List<RunEvent>(events.Count);
        var maxTs = from;
        foreach (var pe in events)
        {
            mapped.Add(Map(runId, pe));
            var ts = pe.LastUpdate == default ? (pe.Finish ?? pe.Start) : pe.LastUpdate;
            if (ts > maxTs) maxTs = ts;
        }

        return new EventBatch(mapped, new EventCursor(LastTimestampUtc: maxTs));
    }

    private static RunEvent Map(string runId, PerformanceEvent pe) => new()
    {
        RunId = runId,
        EventId = pe.Id,
        CorrelationId = pe.Name,
        Service = pe.Service,
        Server = pe.Server,
        ProcessId = pe.ProcessId,
        Pipeline = pe.Pipeline,
        Kind = EventKind.Process,
        Source = pe.Source,
        TimestampUtc = pe.Start,
        FinishUtc = pe.Finish,
        Status = pe.IsError ? EventStatus.Failed : pe.Finish.HasValue ? EventStatus.Success : EventStatus.None,
        RecordCount = pe.RecordCount,
        Error = pe.Error,
        Info = string.IsNullOrEmpty(pe.Info) ? null : pe.Info,
    };
}
