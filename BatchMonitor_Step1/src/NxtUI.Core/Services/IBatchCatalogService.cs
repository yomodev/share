using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

public interface IBatchCatalogService
{
    /// <summary>Returns all batch definitions from the catalog.</summary>
    Task<IReadOnlyList<BatchDefinition>> GetBatchesAsync(CancellationToken ct = default);

    /// <summary>
    /// Triggers a batch by resolving parameter placeholders, building the request
    /// body, and calling the batch endpoint.
    /// </summary>
    Task<BatchTriggerResult> TriggerAsync(
        BatchDefinition              batch,
        Dictionary<string, string>   values,
        string                       env,
        CancellationToken            ct = default);
}
