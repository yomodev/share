using NxtUI.Configuration;
using NxtUI.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace NxtUI.Core.Services;

/// <summary>
/// Real MongoDB implementation of <see cref="IRunService"/>.
/// Reads from the PerformanceTracker collection.
/// Register this in Program.cs instead of MockRunService when connecting to a real cluster.
/// </summary>
public class MongoRunService : IRunService
{
    private readonly MongoSettings _settings;
    private readonly MongoClient _client;

    public MongoRunService(IOptions<MongoSettings> settings)
    {
        _settings = settings.Value;
        _client   = new MongoClient(_settings.ConnectionString);
    }

    public async Task<List<RunSummary>> GetRunsAsync(
        string env, DateTime before, int count,
        RunFilter? filter = null, CancellationToken ct = default)
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
                    builder.Regex("Name", new BsonRegularExpression(text, "i")),
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

        return docs.Select(MapToRunSummary).ToList();
    }

    public async Task<bool> CancelRunAsync(string env, string runId, CancellationToken ct = default)
    {
        // TODO: implement batch cancellation via your domain logic
        // This might call a separate cancellation collection or send a Kafka message
        await Task.CompletedTask;
        return true;
    }

    public Task<RunDetails> GetRunDetailsAsync(string env, string runId, CancellationToken ct = default)
    {
        // Return deterministic static details for demo
        var details = new RunDetails
        {
            RunId = runId ?? "RUN-UNKNOWN",
            Name = $"DemoRun_{runId?.Split('-').LastOrDefault() ?? "X"}",
            Type = "FullLoad",
            Status = RunStatus.Completed,
            Start = DateTime.UtcNow.AddMinutes(-42),
            End = DateTime.UtcNow.AddMinutes(-40),
            Metadata = new Dictionary<string, string>
            {
                ["Source"] = "s3://bucket/path",
                ["Target"] = "mongo://cluster/db/col",
                ["RecordsProcessed"] = "12,345",
                ["WorkerNode"] = "node-7",
                ["RequestId"] = Guid.NewGuid().ToString()
            }
        };

        // For some runIds simulate running
        if (runId?.Contains("RUN-") == true && runId.EndsWith("1"))
        {
            details.Status = RunStatus.Running;
            details.End = null;
        }

        return Task.FromResult(details);
    }

    // ── Mapping ──────────────────────────────────────────────────────────

    private static RunSummary MapToRunSummary(BsonDocument doc)
    {
        return new RunSummary
        {
            RunId     = doc.GetValue("RunId",     BsonNull.Value).AsString     ?? string.Empty,
            Name = doc.GetValue("Name", BsonNull.Value).AsString    ?? string.Empty,
            Type      = doc.GetValue("Type",      BsonNull.Value).AsString     ?? string.Empty,
            Status    = ParseStatus(doc.GetValue("Status", BsonNull.Value).AsString ?? string.Empty),
            Start     = doc.GetValue("Start",     BsonNull.Value).ToUniversalTime(),
            End       = doc.Contains("End") && !doc["End"].IsBsonNull
                            ? doc["End"].ToUniversalTime()
                            : null,
        };
    }

    private static RunStatus ParseStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "running"   => RunStatus.Running,
            "completed" => RunStatus.Completed,
            "failed"    => RunStatus.Failed,
            _           => RunStatus.Unknown
        };

    public Task<List<PerformanceEvent>> GetRunEventsAsync(string env, string runId, DateTime from, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<Topology> GetRunTopologyAsync(string env, string runId, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
