namespace NxtUI.Core.Models;

/// <summary>
/// Lightweight batch record returned by the batch list endpoint.
/// </summary>
public class RunSummary
{
    public string RunId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RunStatus Status { get; set; } = RunStatus.Unknown;
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }

    public TimeSpan Duration => (End ?? DateTime.UtcNow) - Start;
}
