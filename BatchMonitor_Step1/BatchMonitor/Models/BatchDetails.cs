namespace BatchMonitor.Models;

/// <summary>
/// Detailed metadata for a single batch (returned by /api/{env}/batches/{runId}/details).
/// </summary>
public class BatchDetails
{
    public string RunId { get; set; } = string.Empty;
    public string BatchName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public BatchStatus Status { get; set; } = BatchStatus.Unknown;
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public TimeSpan Duration => (End ?? DateTime.UtcNow) - Start;
}