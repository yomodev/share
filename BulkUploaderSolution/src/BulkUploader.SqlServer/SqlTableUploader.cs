using System.Data;
using BulkUploader.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BulkUploader.SqlServer;

/// <summary>
/// Uploads records of type <typeparamref name="T"/> to a single SQL Server table
/// using <see cref="SqlBulkCopy"/>.
///
/// LIFECYCLE
/// ─────────
/// ConnectAsync    → opens <see cref="SqlConnection"/>, creates <see cref="SqlBulkCopy"/>
///                   with <see cref="SqlBulkCopyOptions.TableLock"/> for maximum throughput.
/// UploadBatchAsync → clones the schema table, maps records via <c>rowMapper</c>,
///                    calls <see cref="SqlBulkCopy.WriteToServerAsync"/>.
/// DisconnectAsync  → closes and disposes connection and bulk-copy objects.
///
/// THREAD SAFETY
/// ─────────────
/// <see cref="SqlBulkCopy"/> is not thread-safe. This is not a concern here because
/// all three methods are called exclusively from the single uploader task.
///
/// SCHEMA TABLE
/// ────────────
/// Pass a <see cref="DataTable"/> that describes the target table columns.
/// Only the schema (column names and types) is used; rows are ignored.
/// The <c>rowMapper</c> delegate populates a new <see cref="DataRow"/> for each record.
/// Column mappings are established by name, not by ordinal.
/// </summary>
public sealed class SqlTableUploader<T> : DestinationUploader<T>
{
    private readonly string                    _connectionString;
    private readonly string                    _tableName;
    private readonly DataTable                 _schemaTable;
    private readonly Func<T, DataRow, DataRow> _rowMapper;

    private SqlConnection? _connection;
    private SqlBulkCopy?   _bulkCopy;

    public SqlTableUploader(
        string                       tableName,
        string                       connectionString,
        DataTable                    schemaTable,
        Func<T, DataRow, DataRow>    rowMapper,
        UploaderParameters           parameters,
        BatchSizeTuner               tuner,
        ILogger<SqlTableUploader<T>> logger)
        : base($"SQL:{tableName}", parameters, tuner, logger)
    {
        _tableName        = tableName;
        _connectionString = connectionString;
        _schemaTable      = schemaTable.Clone(); // defensive copy
        _rowMapper        = rowMapper;
    }

    // ── Connection state ──────────────────────────────────────────────────────

    protected override bool IsConnected =>
        _connection?.State == ConnectionState.Open;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        _bulkCopy = new SqlBulkCopy(
            _connection,
            SqlBulkCopyOptions.TableLock,   // avoids row-level lock escalation overhead
            externalTransaction: null)
        {
            DestinationTableName = _tableName,
            BulkCopyTimeout      = 120,
            EnableStreaming       = true,
        };

        // Schema-driven column mappings — never relies on ordinal position.
        foreach (DataColumn col in _schemaTable.Columns)
            _bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
    }

    protected override async Task DisconnectAsync()
    {
        if (_bulkCopy is not null)
        {
            _bulkCopy.Close();
            _bulkCopy = null;
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    protected override async Task UploadBatchAsync(IReadOnlyList<T> batch, CancellationToken ct)
    {
        // Clone schema each batch (structure only, no rows) — fresh DataTable per upload.
        var dt = _schemaTable.Clone();
        foreach (var record in batch)
        {
            var row = dt.NewRow();
            _rowMapper(record, row);
            dt.Rows.Add(row);
        }

        await _bulkCopy!.WriteToServerAsync(dt, ct).ConfigureAwait(false);
    }
}
