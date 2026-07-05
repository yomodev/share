namespace NxtUI.Core.Models;

/// <summary>
/// Detailed metadata for a single batch (returned by /api/{env}/batches/{runId}/details).
/// </summary>
public class RunDetails
{
    public string RunId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public RunStatus Status { get; set; } = RunStatus.Unknown;
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public TimeSpan Duration => (End ?? DateTime.UtcNow) - Start;
}