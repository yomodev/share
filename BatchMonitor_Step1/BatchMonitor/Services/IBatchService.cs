using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Abstraction over the batch data source.
/// Swap the implementation to connect to real MongoDB / REST backend.
/// </summary>
public interface IBatchService
{
    /// <summary>
    /// Returns batches that started before <paramref name="before"/>, newest first.
    /// </summary>
    Task<List<BatchSummary>> GetBatchesAsync(
        string env,
        DateTime before,
        int count,
        BatchFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a cancellation request for a running batch.
    /// Returns true if the request was accepted.
    /// </summary>
    Task<bool> CancelBatchAsync(string env, string runId, CancellationToken ct = default);

    /// <summary>
    /// Returns detailed metadata for a batch.
    /// </summary>
    Task<BatchDetails> GetBatchDetailsAsync(string env, string runId, CancellationToken ct = default);

    /// <summary>
    /// Returns lean performance events for a batch from a given timestamp onwards.
    /// Used for incremental event loading with polling.
    /// </summary>
    Task<List<PerformanceEvent>> GetBatchEventsAsync(
        string env,
        string runId,
        DateTime from,
        CancellationToken ct = default);
}

/// <summary>
/// Optional filter parameters for the batch list endpoint.
/// All fields are optional — null means "no filter on this field".
/// </summary>
public class BatchFilter
{
    /// <summary>Free-text search across RunId, RequestId, Name.</summary>
    public string? SearchText { get; set; }

    /// <summary>Filter by one or more statuses.</summary>
    public List<BatchStatus>? Statuses { get; set; }

    /// <summary>Filter by one or more batch types.</summary>
    public List<string>? Types { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SearchText) &&
        (Statuses is null || Statuses.Count == 0) &&
        (Types    is null || Types.Count    == 0);
}
