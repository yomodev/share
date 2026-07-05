using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Configuration;
using NxtUI.Core.Configuration;
using NxtUI.Core.Filtering;
using NxtUI.Core.Models;
using NxtUI.Core.Services.Mongo;

namespace NxtUI.Core.Services.Sql;

/// <summary>
/// SQL Server implementation of <see cref="IRunService"/>.
/// Run metadata (list/details) comes from <c>[schema].[RunsTable]</c> (configured per environment
/// in <c>config/{env}.json</c>). Per-chunk performance events only ever exist in MongoDB's
/// PerformanceTracker collection — no pipeline writes them to SQL — so this service is a
/// deliberate hybrid: SQL for run metadata, Mongo for the event stream. See docs/xx for the split.
/// Register this in Program.cs instead of MockRunService when connecting to a real SQL Server.
/// </summary>
public class SqlRunService(
    EnvironmentConfigLoader configLoader,
    RunsSettings runsSettings,
    MongoConnectionFactory mongoFactory,
    IOptions<MongoSettings> mongoSettings,
    ILogger<SqlRunService> log)
    : IRunService
{
    // Fields bare filter terms expand to (e.g. "myrun" → matches RunId OR Description).
    private static readonly string[] SearchableFields = ["RunId", "Description"];
    private static readonly FilterParser _parser = new(SearchableFields);

    public async Task<List<RunSummary>> GetRunsAsync(
        string env, DateTime before, int count,
        RunFilter? filter = null, CancellationToken ct = default)
    {
        var sqlCfg = configLoader.GetSql(env);
        if (string.IsNullOrWhiteSpace(sqlCfg.ConnectionString))
        {
            log.LogWarning("No SQL connection string configured for environment {Env}", env);
            return [];
        }

        var windowStart = before.AddMonths(-runsSettings.WindowMonths);
        var effectiveCount = Math.Min(count > 0 ? count : runsSettings.PageSize, runsSettings.MaxResults);

        // An explicit StartTime term in the filter (e.g. "start:2024-01-01..2024-03-01") means the
        // user is deliberately reaching outside the default lookback window — honor it instead of
        // silently clipping to @windowStart.
        var hasExplicitStartFilter = SqlFilterBuilder.ReferencesField(filter?.FilterAst, "StartTime");

        // ── Build WHERE fragments ──────────────────────────────────────────────

        var allParams = new List<SqlParameter>
        {
            new("@count",       effectiveCount),
            new("@before",      before.ToUniversalTime()),
        };

        var clauses = new List<string>
        {
            "r.StartTime < @before",
        };

        if (!hasExplicitStartFilter)
        {
            clauses.Add("r.StartTime >= @windowStart");
            allParams.Add(new SqlParameter("@windowStart", windowStart.ToUniversalTime()));
        }

        // FilterAst — full AST-based WHERE (when the caller passes a parsed filter)
        if (filter?.FilterAst is not null)
        {
            var (astSql, astParams) = SqlFilterBuilder.Build(filter.FilterAst);
            if (!string.IsNullOrEmpty(astSql))
            {
                clauses.Add(astSql);
                allParams.AddRange(astParams);
            }
        }
        else
        {
            // Fallback: translate the simple RunFilter fields directly

            if (!string.IsNullOrWhiteSpace(filter?.SearchText))
            {
                var pat = $"%{EscapeLike(filter.SearchText)}%";
                clauses.Add("(r.RunId LIKE @txt ESCAPE '\\' OR r.Description LIKE @txt ESCAPE '\\')");
                allParams.Add(new SqlParameter("@txt", pat));
            }

            if (filter?.Statuses?.Count > 0)
            {
                var dbValues = filter.Statuses
                    .SelectMany(sqlCfg.StatusValuesFor)
                    .Distinct()
                    .ToList();

                if (dbValues.Count > 0)
                {
                    var names = dbValues.Select((_, i) => $"@sv{i}").ToList();
                    clauses.Add($"r.Status IN ({string.Join(", ", names)})");
                    for (var i = 0; i < dbValues.Count; i++)
                        allParams.Add(new SqlParameter($"@sv{i}", dbValues[i]));
                }
            }

            if (filter?.Types?.Count > 0)
            {
                var names = filter.Types.Select((_, i) => $"@tp{i}").ToList();
                clauses.Add($"r.Type IN ({string.Join(", ", names)})");
                for (var i = 0; i < filter.Types.Count; i++)
                    allParams.Add(new SqlParameter($"@tp{i}", filter.Types[i]));
            }
        }

        // ── Assemble query ────────────────────────────────────────────────────

        // Sort column comes from an allow-listed lookup (SqlFilterBuilder.TryGetColumn) —
        // never interpolate the caller-supplied field name directly into ORDER BY.
        var sortColumn = SqlFilterBuilder.TryGetColumn(filter?.SortField, out var col) ? col : "r.StartTime";
        var sortDir = (filter?.SortDescending ?? true) ? "DESC" : "ASC";

        var where = string.Join("\n  AND ", clauses);
        var sql = $"""
            SELECT TOP (@count)
                r.RunId,
                r.Status,
                r.Type,
                r.Description,
                r.StartTime,
                r.EndTime
            FROM [{sqlCfg.Schema}].[{sqlCfg.RunsTable}] r
            WHERE {where}
            ORDER BY {sortColumn} {sortDir}
            """;

        // ── Execute ────────────────────────────────────────────────────────────

        log.LogDebug(
            "sql [{Env}]: ast={Ast} -> sql={Sql} params={@Params}",
            env, filter?.FilterAst, sql, allParams.Select(p => $"{p.ParameterName}={p.Value}"));

        await using var conn = new SqlConnection(sqlCfg.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddRange(allParams.ToArray());

        var results = new List<RunSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunSummary
            {
                RunId = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Status = sqlCfg.ParseStatus(reader.IsDBNull(1) ? null : reader.GetString(1)),
                Type = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Start = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                End = reader.IsDBNull(5) ? null
                              : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            });
        }

        return results;
    }

    public Task<bool> CancelRunAsync(string env, string runId, CancellationToken ct = default) =>
        throw new NotImplementedException("SQL cancellation not implemented — adapt to your domain.");

    public Task<RunDetails> GetRunDetailsAsync(string env, string runId, CancellationToken ct = default) =>
        throw new NotImplementedException("Run details not available from this SQL service.");

    public async Task<List<PerformanceEvent>> GetRunEventsAsync(
        string env, string runId, DateTime from, CancellationToken ct = default)
    {
        var db = mongoFactory.GetDatabase(env);
        var col = db.GetCollection<BsonDocument>(mongoSettings.Value.PerformanceTrackerCollection);

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("RunId", runId),
            Builders<BsonDocument>.Filter.Gte("Start", from.ToUniversalTime()));

        var docs = await col.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("Start"))
            .ToListAsync(ct);

        return docs.Select(MapToPerformanceEvent).ToList();
    }

    private static PerformanceEvent MapToPerformanceEvent(BsonDocument d) => new()
    {
        Id = d.Contains("Id") && !d["Id"].IsBsonNull ? d["Id"].AsString : d["_id"].ToString() ?? string.Empty,
        Name = d.GetValue("ChunkId", BsonNull.Value).AsString ?? string.Empty,
        Service = d.GetValue("Service", BsonNull.Value).AsString ?? string.Empty,
        Pipeline = d.GetValue("Pipeline", BsonNull.Value).AsString ?? string.Empty,
        Server = d.GetValue("Server", BsonNull.Value).AsString ?? string.Empty,
        ProcessId = d.GetValue("ProcessId", BsonNull.Value).AsString ?? string.Empty,
        Start = d.GetValue("Start", BsonNull.Value).ToUniversalTime(),
        Finish = d.Contains("Finish") && !d["Finish"].IsBsonNull ? d["Finish"].ToUniversalTime() : null,
        Error = d.Contains("Error") && !d["Error"].IsBsonNull ? d["Error"].AsString : null,
        Source = d.GetValue("Source", BsonNull.Value).AsString ?? string.Empty,
        RecordCount = d.Contains("RecordCount") && !d["RecordCount"].IsBsonNull ? d["RecordCount"].ToInt32() : 0,
        Info = d.GetValue("Info", BsonNull.Value).AsString ?? string.Empty,
        Value = d.Contains("Value") && !d["Value"].IsBsonNull ? d["Value"].ToDouble() : null,
        LastUpdate = d.Contains("LastUpdate") && !d["LastUpdate"].IsBsonNull
            ? d["LastUpdate"].ToUniversalTime()
            : DateTime.UtcNow,
    };

    public Task<Topology> GetRunTopologyAsync(string env, string runId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
