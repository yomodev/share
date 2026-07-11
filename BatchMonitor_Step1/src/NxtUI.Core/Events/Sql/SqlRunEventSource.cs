using Microsoft.Data.SqlClient;

namespace NxtUI.Core.Events.Sql;

/// <summary>
/// Reads events for a run out of the <c>dbo.RunEventLog</c> table (see
/// <see cref="SqlRunEventSink"/>), resuming from the last IDENTITY <c>LogId</c> seen. This
/// is the counterpart source for anything that logs via the table — a C# service, or a DTSX
/// package writing rows from its event handlers.
/// </summary>
/// <param name="connectionString">Connection to the DB holding the log table.</param>
/// <param name="table">Fully-qualified table name.</param>
/// <param name="safetyLagSeconds">
/// Only read rows older than this many seconds. Guards against the IDENTITY out-of-order-commit
/// gap (a higher LogId becoming visible before a lower one commits) when multiple writers
/// append concurrently — see docs/10. 0 disables it (fine for a single sequential writer).
/// </param>
public sealed class SqlRunEventSource(string connectionString, string table = "dbo.RunEventLog", int safetyLagSeconds = 0)
    : IEventSource
{
    public string Name => "sql-log";

    public async Task<EventBatch> PollAsync(string runId, EventCursor? cursor, CancellationToken ct = default)
    {
        var lastId = cursor?.LastId ?? 0;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT LogId, EventId, CorrelationId, Service, Server, ProcessId, Pipeline, EventKind, " +
            $"Source, Target, EventTimeUtc, FinishTimeUtc, Status, RecordCount, ErrorMessage, Info " +
            $"FROM {table} WHERE RunId = @RunId AND LogId > @LastId " +
            (safetyLagSeconds > 0 ? "AND LoggedAtUtc < DATEADD(second, -@Lag, SYSUTCDATETIME()) " : "") +
            "ORDER BY LogId";
        cmd.Parameters.AddWithValue("@RunId", runId);
        cmd.Parameters.AddWithValue("@LastId", lastId);
        if (safetyLagSeconds > 0) cmd.Parameters.AddWithValue("@Lag", safetyLagSeconds);

        var events = new List<RunEvent>();
        var maxId = lastId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var logId = reader.GetInt64(0);
            if (logId > maxId) maxId = logId;

            events.Add(new RunEvent
            {
                RunId = runId,
                EventId = reader.GetString(1),
                CorrelationId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Service = reader.GetString(3),
                Server = reader.GetString(4),
                ProcessId = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Pipeline = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Kind = ParseKind(reader.GetString(7)),
                Source = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                Target = reader.IsDBNull(9) ? null : reader.GetString(9),
                TimestampUtc = reader.GetDateTime(10),
                FinishUtc = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                Status = ParseStatus(reader.IsDBNull(12) ? null : reader.GetString(12)),
                RecordCount = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                Error = reader.IsDBNull(14) ? null : reader.GetString(14),
                Info = reader.IsDBNull(15) ? null : reader.GetString(15),
            });
        }

        return new EventBatch(events, new EventCursor(LastId: maxId));
    }

    private static EventKind ParseKind(string s) =>
        Enum.TryParse<EventKind>(s, ignoreCase: true, out var k) ? k : EventKind.Info;

    private static EventStatus ParseStatus(string? s) =>
        !string.IsNullOrEmpty(s) && Enum.TryParse<EventStatus>(s, ignoreCase: true, out var st) ? st : EventStatus.None;
}
