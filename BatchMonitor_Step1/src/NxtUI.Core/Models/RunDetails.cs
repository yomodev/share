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

    /// <summary>
    /// This run's own parent, if any — set only on the run actually fetched (never
    /// re-stated on nested <see cref="RunNode"/> children, whose parent is implicit from
    /// tree position). Used to walk UP and build breadcrumbs when deep-linking directly to
    /// a child run. Null at the root. See docs/12_Custom_Layout_And_Nested_Runs.md §7.2.
    /// </summary>
    public string? ParentRunId { get; set; }

    /// <summary>
    /// Child runs triggered by this one, "so far" — this list can GROW while the run is
    /// still in progress (an orchestrator may spawn children over time), so empty/absent
    /// means "none yet," never "leaf forever." Every run is potentially a parent; callers
    /// re-read this on each poll and diff it rather than relying on a static expandability
    /// flag. See docs/12_Custom_Layout_And_Nested_Runs.md §7.2/§7.3.
    /// </summary>
    public List<RunNode> Children { get; set; } = new();
}