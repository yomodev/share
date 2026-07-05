namespace NxtUI.Core.Logging;

/// <summary>
/// A single parsed memory-metrics line from a service log file.
/// All usage values are in bytes.
/// </summary>
public record MetricsSample
{
    public DateTime Timestamp { get; init; }
    public long CurrentUsageBytes { get; init; }
    public long PeakUsageBytes { get; init; }
    public long ChildUsageBytes { get; init; }
    public long ChildPeakUsageBytes { get; init; }

    /// <summary>Current usage of the process and its children.</summary>
    public long TotalCurrentBytes => CurrentUsageBytes + ChildUsageBytes;

    /// <summary>Peak usage of the process and its children.</summary>
    public long TotalPeakBytes => PeakUsageBytes + ChildPeakUsageBytes;
}
