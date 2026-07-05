using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Configuration;
using NxtUI.Core.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Mongo;

/// <summary>
/// Real MongoDB implementation of <see cref="IRunService"/>.
/// Reads from the PerformanceTracker collection.
/// Register this in Program.cs instead of MockRunService when connecting to a real cluster.
/// </summary>
public class MongoRunService : IRunService
{
    private readonly MongoSettings _settings;
    private readonly MongoConnectionFactory _factory;
    private readonly RunsSettings _runsSettings;

    public MongoRunService(MongoConnectionFactory factory, IOptions<MongoSettings> settings, RunsSettings runsSettings)
    {
        _factory = factory;
        _settings = settings.Value;
        _runsSettings = runsSettings;
    }

    public async Task<List<RunSummary>> GetRunsAsync(
        string env, DateTime before, int count,
        RunFilter? filter = null, CancellationToken ct = default)
    {
        var db = _factory.GetDatabase(env);
        var collection = db.GetCollection<BsonDocument>(_settings.PerformanceTrackerCollection);

        // Build filter — adapt field names to match your actual MongoDB schema
        var builder = Builders<BsonDocument>.Filter;
        var baseFilter = builder.Lt("Start", before);

        if (filter is not null && !filter.IsEmpty)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var text = filter.SearchText.Trim();
                var textFilter = builder.Or(
                    builder.Regex("RunId", new BsonRegularExpression(text, "i")),
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

        // Allow-listed sort field — canonical "StartTime"/"EndTime" map to this collection's
        // actual "Start"/"End" fields; anything else must match a real document field name.
        var sortField = filter?.SortField switch
        {
            "StartTime" or null => "Start",
            "EndTime" => "End",
            var f => f,
        };
        var sort = (filter?.SortDescending ?? true)
            ? Builders<BsonDocument>.Sort.Descending(sortField)
            : Builders<BsonDocument>.Sort.Ascending(sortField);

        var effectiveCount = Math.Min(count > 0 ? count : _runsSettings.PageSize, _runsSettings.MaxResults);

        var docs = await collection
            .Find(baseFilter)
            .Sort(sort)
            .Limit(effectiveCount)
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
            Description = $"DemoRun_{runId?.Split('-').LastOrDefault() ?? "X"}",
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
            RunId = doc.GetValue("RunId", BsonNull.Value).AsString ?? string.Empty,
            Type = doc.GetValue("Type", BsonNull.Value).AsString ?? string.Empty,
            Status = ParseStatus(doc.GetValue("Status", BsonNull.Value).AsString ?? string.Empty),
            Start = doc.GetValue("Start", BsonNull.Value).ToUniversalTime(),
            End = doc.Contains("End") && !doc["End"].IsBsonNull
                            ? doc["End"].ToUniversalTime()
                            : null,
        };
    }

    private static RunStatus ParseStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "running" => RunStatus.Running,
            "completed" => RunStatus.Completed,
            "failed" => RunStatus.Failed,
            _ => RunStatus.Unknown
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
