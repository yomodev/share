using Microsoft.Data.SqlClient;

namespace NxtUI.Core.Events.Sql;

/// <summary>
/// Writes events to the <c>dbo.RunEventLog</c> table described in
/// <c>docs/10_DTSX_SQL_Event_Logging.md</c> (append-only; the IDENTITY <c>LogId</c> is the
/// resume cursor for <see cref="SqlRunEventSource"/>). This is the sink a C# service (via
/// <see cref="RunEventReporter"/>) or a DTSX Script Task would target.
///
/// Expected schema (spans stored as one row via the nullable <c>FinishTimeUtc</c> column —
/// a superset of the doc's append-only Start/Finish variant):
/// <code>
/// CREATE TABLE dbo.RunEventLog (
///   LogId BIGINT IDENTITY(1,1) PRIMARY KEY, RunId VARCHAR(64) NOT NULL, EventId CHAR(32) NOT NULL,
///   CorrelationId VARCHAR(128) NULL, Service VARCHAR(128) NOT NULL, Server VARCHAR(128) NOT NULL,
///   ProcessId INT NULL, Pipeline VARCHAR(128) NULL, EventKind VARCHAR(24) NOT NULL,
///   Source VARCHAR(256) NULL, Target VARCHAR(512) NULL, EventTimeUtc DATETIME2(3) NOT NULL,
///   FinishTimeUtc DATETIME2(3) NULL, Status VARCHAR(24) NULL, RecordCount BIGINT NULL,
///   ErrorMessage NVARCHAR(MAX) NULL, Info NVARCHAR(1024) NULL,
///   LoggedAtUtc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME());
/// </code>
/// </summary>
public sealed class SqlRunEventSink(string connectionString, string table = "dbo.RunEventLog") : IRunEventSink
{
    public async Task WriteAsync(IReadOnlyList<RunEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        const string columns =
            "RunId, EventId, CorrelationId, Service, Server, ProcessId, Pipeline, EventKind, " +
            "Source, Target, EventTimeUtc, FinishTimeUtc, Status, RecordCount, ErrorMessage, Info";

        foreach (var e in events)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO {table} ({columns}) VALUES " +
                "(@RunId, @EventId, @CorrelationId, @Service, @Server, @ProcessId, @Pipeline, @EventKind, " +
                "@Source, @Target, @EventTimeUtc, @FinishTimeUtc, @Status, @RecordCount, @ErrorMessage, @Info)";

            cmd.Parameters.AddWithValue("@RunId", e.RunId);
            cmd.Parameters.AddWithValue("@EventId", e.EventId);
            cmd.Parameters.AddWithValue("@CorrelationId", (object?)e.CorrelationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Service", e.Service);
            cmd.Parameters.AddWithValue("@Server", e.Server);
            cmd.Parameters.AddWithValue("@ProcessId", e.ProcessId);
            cmd.Parameters.AddWithValue("@Pipeline", (object?)NullIfEmpty(e.Pipeline) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@EventKind", e.Kind.ToString());
            cmd.Parameters.AddWithValue("@Source", (object?)NullIfEmpty(e.Source) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Target", (object?)e.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@EventTimeUtc", e.TimestampUtc);
            cmd.Parameters.AddWithValue("@FinishTimeUtc", (object?)e.FinishUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", e.Status == EventStatus.None ? DBNull.Value : e.Status.ToString());
            cmd.Parameters.AddWithValue("@RecordCount", (object?)e.RecordCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)e.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Info", (object?)e.Info ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
