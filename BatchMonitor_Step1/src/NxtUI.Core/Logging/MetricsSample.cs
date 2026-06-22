namespace NxtUI.Logging;

/// <summary>
/// A single parsed memory-metrics line from a service log file.
/// All usage values are in bytes.
/// </summary>
public record MetricsSample
{
    public DateTime Timestamp           { get; init; }
    public long     CurrentUsageBytes   { get; init; }
    public long     PeakUsageBytes      { get; init; }
    public long     ChildUsageBytes     { get; init; }
    public long     ChildPeakUsageBytes { get; init; }
    public DateTime? ProcessStartTime   { get; init; }

    /// <summary>Metrics stream name (redundant — folder is already per-service).</summary>
    public string?  StreamName          { get; init; }

    /// <summary>Message id preceding ".Memory" (redundant for now).</summary>
    public string?  MessageId           { get; init; }
}
