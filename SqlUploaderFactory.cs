using System.Data;
using BulkUploader.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BulkUploader.SqlServer;

/// <summary>
/// Concrete <see cref="ISqlUploaderFactory"/> implementation.
///
/// Register via <see cref="SqlServerServiceCollectionExtensions.AddSqlBulkUploader"/>.
/// The factory is a singleton: it resolves connection string and defaults once
/// from <see cref="SqlUploaderOptions"/> and re-uses them for every
/// <see cref="Create{T}"/> call.
///
/// LIFETIME RESPONSIBILITY
/// ───────────────────────
/// Each uploader returned by <see cref="Create{T}"/> owns three background tasks
/// and a database connection. The caller must dispose it:
///
///   • Short-lived: <c>await using var uploader = factory.Create&lt;T&gt;(...);</c>
///   • Long-lived: store it in a field and dispose in <c>IAsyncDisposable.DisposeAsync</c>
///     or register with the DI container lifetime.
/// </summary>
public sealed class SqlUploaderFactory : ISqlUploaderFactory
{
    private readonly string             _connectionString;
    private readonly SqlUploaderOptions _options;
    private readonly ILoggerFactory     _loggerFactory;

    public SqlUploaderFactory(
        IOptions<SqlUploaderOptions> options,
        ILoggerFactory               loggerFactory)
    {
        _options          = options.Value;
        _loggerFactory    = loggerFactory;
        _connectionString = _options.ConnectionString;

        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException(
                $"{nameof(SqlUploaderOptions)}.{nameof(SqlUploaderOptions.ConnectionString)} " +
                "must be configured before using ISqlUploaderFactory.");
    }

    /// <inheritdoc />
    public SqlTableUploader<T> Create<T>(
        string                    tableName,
        DataTable                 schema,
        Func<T, DataRow, DataRow> rowMapper,
        UploaderParameters?       parameters = null,
        BatchSizeTuner?           tuner      = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rowMapper);

        return new SqlTableUploader<T>(
            tableName:        tableName,
            connectionString: _connectionString,
            schemaTable:      schema,
            rowMapper:        rowMapper,
            parameters:       parameters ?? BuildDefaultParameters(),
            tuner:            tuner      ?? BuildDefaultTuner(),
            logger:           _loggerFactory.CreateLogger<SqlTableUploader<T>>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private UploaderParameters BuildDefaultParameters()
    {
        var o = _options.DefaultParameters;
        return new UploaderParameters(
            jobChannelCapacity:    o.JobChannelCapacity,
            recordChannelCapacity: o.RecordChannelCapacity,
            batchChannelCapacity:  o.BatchChannelCapacity,
            maxRetries:            o.MaxRetries,
            retryBaseDelayMs:      o.RetryBaseDelayMs,
            idleTimeoutMs:         o.IdleTimeoutMs,
            flushAfterIdleMs:      o.FlushAfterIdleMs);
    }

    private BatchSizeTuner BuildDefaultTuner()
    {
        var o = _options.DefaultTuner;
        return new BatchSizeTuner(
            initial:          o.Initial,
            min:              o.Min,
            max:              o.Max,
            stepFraction:     o.StepFraction,
            deadBandFraction: o.DeadBandFraction);
    }
}
