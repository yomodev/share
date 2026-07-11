using Microsoft.Data.SqlClient;

namespace NxtUI.Core.Events.Sql;

/// <summary>
/// Reads events straight out of the SSISDB catalog for one package execution — the
/// "no custom table, no package edits" option from <c>docs/10_DTSX_SQL_Event_Logging.md</c>.
/// Tails <c>catalog.event_messages</c> (resumable by its monotonic <c>event_message_id</c>)
/// and maps SSIS message types onto the model.
///
/// <para><b>Known ceiling</b> (see docs/10, "Is it a good idea?"): SSISDB has no business
/// correlation id, so cross-package edges can't be inferred — this source synthesizes a
/// per-task correlation from the execution path only so the existing node/row/span pipeline
/// lights up. It also can't recover the real Target (table/file) or row counts from
/// event_messages alone; true spans + row counts live in <c>catalog.executable_statistics</c>
/// and <c>catalog.execution_data_statistics</c> and would be joined in for a fuller feed.</para>
/// </summary>
/// <param name="ssisDbConnectionString">Connection to the SSISDB database.</param>
/// <param name="executionId">The <c>catalog.executions.execution_id</c> this run maps to.</param>
public sealed class SsisDbEventSource(string ssisDbConnectionString, long executionId) : IEventSource
{
    // catalog.event_messages.message_type numeric codes (subset we care about).
    private const int OnPreExecute = 30;
    private const int OnPostExecute = 40;
    private const int OnInformation = 70;
    private const int OnError = 120;
    private const int OnTaskFailed = 130;

    public string Name => "ssisdb";

    public async Task<EventBatch> PollAsync(string runId, EventCursor? cursor, CancellationToken ct = default)
    {
        var lastId = cursor?.LastId ?? 0;

        await using var conn = new SqlConnection(ssisDbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT em.event_message_id, em.message_time, em.message_type, em.package_name, " +
            "       em.message_source_name, em.execution_path, em.message, ex.server_name, ex.process_id " +
            "FROM catalog.event_messages em " +
            "JOIN catalog.executions ex ON ex.execution_id = em.operation_id " +
            "WHERE em.operation_id = @exec AND em.event_message_id > @lastId " +
            "ORDER BY em.event_message_id";
        cmd.Parameters.AddWithValue("@exec", executionId);
        cmd.Parameters.AddWithValue("@lastId", lastId);

        var events = new List<RunEvent>();
        var maxId = lastId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var msgId = reader.GetInt64(0);
            if (msgId > maxId) maxId = msgId;

            var messageType = reader.GetInt32(2);
            var kind = MapKind(messageType);
            if (kind is null) continue; // uninteresting message type (validate/progress/etc.)

            var when = reader.GetDateTimeOffset(1).UtcDateTime;
            var packageName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var sourceName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var execPath = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var message = reader.IsDBNull(6) ? null : reader.GetString(6);
            var serverName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var processId = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);

            var (pipeline, correlation) = ParsePath(execPath, executionId);
            var completed = kind == EventKind.Process && messageType == OnPostExecute;

            events.Add(new RunEvent
            {
                RunId = runId,
                // Stable per (execution, task) so a task's OnPreExecute and OnPostExecute
                // upsert onto one span (loop iterations collapse — a documented limitation).
                EventId = Hash16($"{executionId}:{execPath}"),
                CorrelationId = correlation,
                Service = packageName,
                Server = serverName,
                ProcessId = processId,
                Pipeline = pipeline,
                Kind = kind.Value,
                Source = sourceName,
                TimestampUtc = when,
                FinishUtc = completed ? when : null,
                Status = kind == EventKind.Error ? EventStatus.Failed
                        : completed ? EventStatus.Success : EventStatus.None,
                Error = kind == EventKind.Error ? message : null,
                Info = kind == EventKind.Info ? message : null,
            });
        }

        return new EventBatch(events, new EventCursor(LastId: maxId));
    }

    private static EventKind? MapKind(int messageType) => messageType switch
    {
        OnPreExecute or OnPostExecute => EventKind.Process,
        OnError or OnTaskFailed => EventKind.Error,
        OnInformation => EventKind.Info,
        _ => null,
    };

    // execution_path looks like "\PackageName\Data Flow Task\Component". The first segment
    // under the package is the pipeline (a data flow / container); shallower paths (a plain
    // task directly under the package) fall back to the default pipeline (empty).
    private static (string Pipeline, string Correlation) ParsePath(string execPath, long executionId)
    {
        var segments = execPath.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var pipeline = segments.Length >= 3 ? segments[1] : string.Empty;
        // No business key in SSISDB — synthesize one per task-attempt so nodes/rows/spans
        // render. This is why cross-package edges can't be inferred from SSISDB alone.
        var correlation = $"{executionId}:{execPath}";
        return (pipeline, correlation);
    }

    private static string Hash16(string s)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16];
    }
}
