namespace NxtUI.Core.Models;

/// <summary>
/// Lean performance event from PerformanceTracker fields (see design doc §7.5).
/// Upserted by the client keyed on <see cref="CompositeKey"/> = (chunkId, service, pipeline, processId).
/// </summary>
public class PerformanceEvent
{
    /// <summary>Unique message identifier — one per (chunk × service instance × processing attempt).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>Unique chunk identifier (TypePrefix + IncrementalNumber).</summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>Service type name (e.g., "DataProcessor", "Transformer").</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Pipeline name — unique per service, one input topic.</summary>
    public string Pipeline { get; set; } = string.Empty;

    /// <summary>Machine hostname.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>OS process ID of the worker that handled this chunk.</summary>
    public string ProcessId { get; set; } = string.Empty;

    /// <summary>When processing started. Always present.</summary>
    public DateTime Start { get; set; }

    /// <summary>When processing finished. Null if still in progress.</summary>
    public DateTime? Finish { get; set; }

    /// <summary>Set if this chunk failed.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Class name or pipe processor that handled this chunk (e.g. "OrderParser",
    /// "ValidationHandler"). Used as the default Colour By key in the Timeline tab.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Records processed in this chunk.</summary>
    public int RecordCount { get; set; }

    /// <summary>
    /// Composite key for deduplication/upsert: (chunkId, service, pipeline, processId).
    /// </summary>
    public string CompositeKey => $"{ChunkId}:{Service}:{Pipeline}:{ProcessId}";

    /// <summary>True if this chunk has completed (successfully or with error).</summary>
    public bool IsDone => Finish.HasValue;

    /// <summary>True if this chunk errored.</summary>
    public bool IsError => !string.IsNullOrEmpty(Error);

    /// <summary>
    /// Convenience ordering timestamp — events are ordered by <see cref="Start"/>
    /// for topology/edge inference per §8.2.
    /// </summary>
    public DateTime Timestamp => Start;
}
