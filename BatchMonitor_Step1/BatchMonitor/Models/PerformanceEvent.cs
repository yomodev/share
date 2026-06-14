namespace BatchMonitor.Models;

/// <summary>
/// Lean performance event from PerformanceTracker fields.
/// Keyed by (chunkId, service, processId) for in-memory store.
/// </summary>
public class PerformanceEvent
{
    /// <summary>Unique identifier within a batch.</summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>Service name (e.g., "DataProcessor", "Transformer").</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Worker process ID.</summary>
    public string ProcessId { get; set; } = string.Empty;

    /// <summary>When this event was recorded.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Duration of the operation in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Status of the operation (Success, Failed, Skipped).</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>Optional message (error, warning, etc.).</summary>
    public string? Message { get; set; }

    /// <summary>Records processed in this chunk.</summary>
    public int RecordCount { get; set; }

    /// <summary>Memory used in MB.</summary>
    public double MemoryMb { get; set; }

    /// <summary>CPU utilization percentage.</summary>
    public double CpuPercent { get; set; }

    /// <summary>
    /// Composite key for deduplication/upsert.
    /// </summary>
    public string CompositeKey => $"{ChunkId}:{Service}:{ProcessId}";
}
