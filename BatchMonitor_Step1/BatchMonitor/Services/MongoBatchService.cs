using BatchMonitor.Configuration;
using BatchMonitor.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BatchMonitor.Services;

/// <summary>
/// Real MongoDB implementation of <see cref="IBatchService"/>.
/// Reads from the PerformanceTracker collection.
/// Register this in Program.cs instead of MockBatchService when connecting to a real cluster.
/// </summary>
public class MongoBatchService : IBatchService
{
    private readonly MongoSettings _settings;
    private readonly MongoClient _client;

    public MongoBatchService(IOptions<MongoSettings> settings)
    {
        _settings = settings.Value;
        _client   = new MongoClient(_settings.ConnectionString);
    }

    public async Task<List<BatchSummary>> GetBatchesAsync(
        string env, DateTime before, int count,
        BatchFilter? filter = null, CancellationToken ct = default)
    {
        var db         = _client.GetDatabase(_settings.GetDatabaseName(env));
        var collection = db.GetCollection<BsonDocument>(_settings.PerformanceTrackerCollection);

        // Build filter — adapt field names to match your actual MongoDB schema
        var builder    = Builders<BsonDocument>.Filter;
        var baseFilter = builder.Lt("Start", before);

        if (filter is not null && !filter.IsEmpty)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var text = filter.SearchText.Trim();
                var textFilter = builder.Or(
                    builder.Regex("RunId",     new BsonRegularExpression(text, "i")),
                    builder.Regex("BatchName", new BsonRegularExpression(text, "i")),
                    builder.Regex("RequestId", new BsonRegularExpression(text, "i")));
                baseFilter &= textFilter;
            }
            if (filter.Statuses?.Count > 0)
            {
                var statusStrings = filter.Statuses.Select(s => s.ToString()).ToList();
                baseFilter &= builder.In("Status", statusStrings);
            }
            if (filter.Types?.Count > 0)
                baseFilter &= builder.In("Type", filter.Types);
        }

        var sort = Builders<BsonDocument>.Sort.Descending("Start");

        var docs = await collection
            .Find(baseFilter)
            .Sort(sort)
            .Limit(count)
            .ToListAsync(ct);

        return docs.Select(MapToBatchSummary).ToList();
    }

    public async Task<bool> CancelBatchAsync(string env, string runId, CancellationToken ct = default)
    {
        // TODO: implement batch cancellation via your domain logic
        // This might call a separate cancellation collection or send a Kafka message
        await Task.CompletedTask;
        return true;
    }

    // ── Mapping ──────────────────────────────────────────────────────────

    private static BatchSummary MapToBatchSummary(BsonDocument doc)
    {
        return new BatchSummary
        {
            RunId     = doc.GetValue("RunId",     BsonNull.Value).AsString     ?? string.Empty,
            BatchName = doc.GetValue("BatchName", BsonNull.Value).AsString     ?? string.Empty,
            Type      = doc.GetValue("Type",      BsonNull.Value).AsString     ?? string.Empty,
            Status    = ParseStatus(doc.GetValue("Status", BsonNull.Value).AsString ?? string.Empty),
            Start     = doc.GetValue("Start",     BsonNull.Value).ToUniversalTime(),
            End       = doc.Contains("End") && !doc["End"].IsBsonNull
                            ? doc["End"].ToUniversalTime()
                            : null,
        };
    }

    private static BatchStatus ParseStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "running"   => BatchStatus.Running,
            "completed" => BatchStatus.Completed,
            "failed"    => BatchStatus.Failed,
            _           => BatchStatus.Unknown
        };
}
