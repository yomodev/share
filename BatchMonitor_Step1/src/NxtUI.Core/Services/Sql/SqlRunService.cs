using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NxtUI.Configuration;
using NxtUI.Core.Filtering;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Sql;

/// <summary>
/// SQL Server implementation of <see cref="IRunService"/>.
/// Reads from <c>[schema].[RunsTable]</c> (configured per environment in <c>config/{env}.json</c>).
/// Register this in Program.cs instead of MockRunService when connecting to a real SQL Server.
/// </summary>
public class SqlRunService(
    EnvironmentConfigLoader configLoader,
    RunsSettings runsSettings,
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
                    .SelectMany(s => sqlCfg.StatusValues.TryGetValue(s.ToString(), out var v) ? v : [])
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
                Status = ParseStatus(reader.IsDBNull(1) ? null : reader.GetString(1)),
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

    public Task<List<PerformanceEvent>> GetRunEventsAsync(
        string env, string runId, DateTime from, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<Topology> GetRunTopologyAsync(string env, string runId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Maps whatever the DB stores → RunStatus. Covers common short codes and full names.
    private static RunStatus ParseStatus(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "CREATED" => RunStatus.Running,
            "SUCCESS" => RunStatus.Completed,
            "FAILED" => RunStatus.Failed,
            "TERMINATED" => RunStatus.Terminated,
            "PURGED" => RunStatus.Purged,
            _ => RunStatus.Unknown,
        };

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
