using BulkUploader.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BulkUploader.MongoDb;

/// <summary>
/// Concrete <see cref="IMongoUploaderFactory"/> implementation.
/// Register via <see cref="MongoDbServiceCollectionExtensions.AddMongoBulkUploader"/>.
/// </summary>
public sealed class MongoUploaderFactory : IMongoUploaderFactory
{
    private readonly MongoUploaderOptions _options;
    private readonly ILoggerFactory       _loggerFactory;

    public MongoUploaderFactory(
        IOptions<MongoUploaderOptions> options,
        ILoggerFactory                 loggerFactory)
    {
        _options       = options.Value;
        _loggerFactory = loggerFactory;

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException(
                $"{nameof(MongoUploaderOptions)}.{nameof(MongoUploaderOptions.ConnectionString)} " +
                "must be configured before using IMongoUploaderFactory.");
    }

    /// <inheritdoc />
    public MongoCollectionUploader<T> Create<T>(
        string              collectionName,
        string?             databaseName = null,
        UploaderParameters? parameters   = null,
        BatchSizeTuner?     tuner        = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var db = databaseName ?? _options.DatabaseName;

        if (string.IsNullOrWhiteSpace(db))
            throw new InvalidOperationException(
                $"No database name provided and " +
                $"{nameof(MongoUploaderOptions)}.{nameof(MongoUploaderOptions.DatabaseName)} " +
                "is not configured.");

        return new MongoCollectionUploader<T>(
            connectionString:      _options.ConnectionString,
            databaseName:          db,
            collectionName:        collectionName,
            parameters:            parameters ?? BuildDefaultParameters(),
            tuner:                 tuner      ?? BuildDefaultTuner(),
            logger:                _loggerFactory.CreateLogger<MongoCollectionUploader<T>>(),
            maxConnectionPoolSize: _options.MaxConnectionPoolSize);
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
