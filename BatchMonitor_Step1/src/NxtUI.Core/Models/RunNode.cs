namespace NxtUI.Core.Models;

/// <summary>
/// Summary-only view of a child run, attached to its parent's <see cref="RunDetails.Children"/>.
/// See docs/12_Custom_Layout_And_Nested_Runs.md §7.2 for the full design.
///
/// Deliberately does NOT carry:
///   - a parent-run id (implicit from tree position — only the fetched run's own
///     <see cref="RunDetails.ParentRunId"/> is needed, for walking up to build breadcrumbs
///     on a deep link);
///   - a "has children" flag (children can appear DURING a run — a growing orchestrator
///     may add them at any time — so a static flag would be a moving target as well as
///     redundant: every run is potentially a parent, and drilling into one shows whatever
///     exists at that instant).
/// </summary>
public sealed class RunNode
{
    public string RunId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RunStatus Status { get; set; } = RunStatus.Unknown;
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }

    /// <summary>Best-effort live progress. Null when not cheaply known. The collapsed
    /// child-run card (renderFakePipelineRow in d3-graph.js) reads this as one of three
    /// states: a real percentage when both counts are known, an animated indeterminate bar
    /// when the run is Running with no counts yet, or no bar at all when there's neither
    /// (a finished/not-yet-started run with nothing to show).</summary>
    public int? DoneCount { get; set; }
    public int? TotalCount { get; set; }
}
