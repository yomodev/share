namespace BatchMonitor.Models;

/// <summary>
/// Lightweight batch record returned by the batch list endpoint.
/// </summary>
public class BatchSummary
{
    public string RunId { get; set; } = string.Empty;
    public string BatchName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public BatchStatus Status { get; set; } = BatchStatus.Unknown;
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }

    public TimeSpan Duration => (End ?? DateTime.UtcNow) - Start;
}

public enum BatchStatus
{
    Unknown,
    Running,
    Completed,
    Failed
}
