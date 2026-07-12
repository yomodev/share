using NxtUI.Core.Filtering;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Abstraction over the batch data source.
/// Swap the implementation to connect to real MongoDB / REST backend.
/// </summary>
public interface IRunService
{
    /// <summary>
    /// Returns batches that started before <paramref name="before"/>, newest first.
    /// </summary>
    Task<List<RunSummary>> GetRunsAsync(
        string env,
        DateTime before,
        int count,
        RunFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a cancellation request for a running batch.
    /// Returns true if the request was accepted.
    /// </summary>
    Task<bool> CancelRunAsync(string env, string runId, CancellationToken ct = default);

    /// <summary>
    /// Returns detailed metadata for a batch, including its immediate child runs (if any —
    /// see <see cref="RunDetails.Children"/>) as lightweight summaries, never full detail.
    /// </summary>
    /// <param name="childDepth">
    /// How many levels of nested children to populate in one call (1 = immediate children
    /// only, the default — keeps payloads small and bounded at every level of a deep tree).
    /// A caller doing "expand all" can request more; each level still only carries
    /// <see cref="RunNode"/> summaries, never full <see cref="RunDetails"/>. See
    /// docs/12_Custom_Layout_And_Nested_Runs.md §7.3.
    /// </param>
    Task<RunDetails> GetRunDetailsAsync(string env, string runId, CancellationToken ct = default, int childDepth = 1);

    /// <summary>
    /// Returns lean performance events for a batch from a given timestamp onwards.
    /// Used for incremental event loading with polling.
    /// </summary>
    Task<List<PerformanceEvent>> GetRunEventsAsync(
        string env,
        string runId,
        DateTime from,
        CancellationToken ct = default);

    /// <summary>
    /// Returns inferred topology (nodes and edges) from the batch's events.
    /// Typically called after events have been accumulated in the client event store,
    /// but can also be computed server-side.
    /// </summary>
    Task<Topology> GetRunTopologyAsync(string env, string runId, CancellationToken ct = default);
}

/// <summary>
/// Optional filter parameters for the batch list endpoint.
/// All fields are optional — null means "no filter on this field".
/// </summary>
public class RunFilter
{
    /// <summary>Free-text search across RunId, RequestId, Description.</summary>
    public string? SearchText { get; set; }

    /// <summary>Filter by one or more statuses.</summary>
    public List<RunStatus>? Statuses { get; set; }

    /// <summary>Filter by one or more batch types.</summary>
    public List<string>? Types { get; set; }

    /// <summary>
    /// Full parsed AST from the filter box. When set, <see cref="SqlRunService"/> uses
    /// <see cref="SqlFilterBuilder"/> to generate a WHERE clause; other backends may ignore it.
    /// </summary>
    public FilterNode? FilterAst { get; set; }

    /// <summary>
    /// Column to sort by (canonical field name, e.g. "StartTime", "RunId"). Null/unrecognized
    /// falls back to the backend's default (StartTime). Validated against an allow-list —
    /// never interpolated into SQL without that check.
    /// </summary>
    public string? SortField { get; set; }

    /// <summary>Sort direction. Default: descending.</summary>
    public bool SortDescending { get; set; } = true;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SearchText) &&
        (Statuses is null || Statuses.Count == 0) &&
        (Types is null || Types.Count == 0) &&
        FilterAst is null;
}
