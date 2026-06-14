using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Mock implementation of <see cref="IBatchService"/>.
/// Generates deterministic fake data so the UI works without a real backend.
/// Replace with <see cref="MongoBatchService"/> (Step 2 real impl) when ready.
/// </summary>
public class MockBatchService : IBatchService
{
    private static readonly string[] Types = { "FullLoad", "DeltaSync", "Reconcile", "Archive" };
    private static readonly string[] Entities = { "Customers", "Orders", "Products", "Inventory", "Pricing", "Contracts", "Shipments", "Invoices" };

    private readonly List<BatchSummary> _store;

    public MockBatchService()
    {
        var rng = new Random(42);
        _store = Enumerable.Range(1, 200).Select(i =>
        {
            var type   = Types[i % Types.Length];
            var entity = Entities[i % Entities.Length];
            var start  = DateTime.UtcNow.AddMinutes(-(i * 7 + rng.Next(0, 5)));
            var status = i switch { 1 => BatchStatus.Running, 2 => BatchStatus.Running, 3 => BatchStatus.Failed, 7 => BatchStatus.Failed, _ => BatchStatus.Completed };
            return new BatchSummary
            {
                RunId     = $"RUN-{DateTime.UtcNow:yyyyMMdd}-{i:D3}",
                BatchName = $"{type}_{entity}",
                Type      = type,
                Status    = status,
                Start     = start,
                End       = status != BatchStatus.Running ? start.AddSeconds(rng.Next(60, 1800)) : null
            };
        })
        .OrderByDescending(b => b.Start)
        .ToList();
    }

    public Task<List<BatchSummary>> GetBatchesAsync(
        string env, DateTime before, int count,
        BatchFilter? filter = null, CancellationToken ct = default)
    {
        var query = _store.Where(b => b.Start < before);

        if (filter is not null && !filter.IsEmpty)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var text = filter.SearchText.Trim().ToLowerInvariant();
                query = query.Where(b =>
                    b.RunId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    b.BatchName.Contains(text, StringComparison.OrdinalIgnoreCase));
            }
            if (filter.Statuses?.Count > 0)
                query = query.Where(b => filter.Statuses.Contains(b.Status));
            if (filter.Types?.Count > 0)
                query = query.Where(b => filter.Types.Contains(b.Type, StringComparer.OrdinalIgnoreCase));
        }

        return Task.FromResult(query.Take(count).ToList());
    }

    public Task<bool> CancelBatchAsync(string env, string runId, CancellationToken ct = default)
    {
        var batch = _store.FirstOrDefault(b => b.RunId == runId);
        if (batch is not null && batch.Status == BatchStatus.Running)
        {
            batch.Status = BatchStatus.Failed;
            batch.End    = DateTime.UtcNow;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
