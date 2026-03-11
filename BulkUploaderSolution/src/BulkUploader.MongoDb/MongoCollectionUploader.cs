using BulkUploader.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace BulkUploader.MongoDb;

/// <summary>
/// Uploads documents of type <typeparamref name="T"/> to a single MongoDB collection
/// using <see cref="IMongoCollection{TDocument}.InsertManyAsync"/>.
///
/// LIFECYCLE
/// ─────────
/// ConnectAsync     → creates <see cref="MongoClient"/> and acquires the collection handle.
///                    The client manages its own connection pool and reconnects transparently.
/// UploadBatchAsync → calls <c>InsertManyAsync</c> with <c>IsOrdered = false</c>,
///                    allowing the server to parallelise inserts internally.
/// DisconnectAsync  → disposes the client, draining the connection pool.
///
/// THREAD SAFETY
/// ─────────────
/// <see cref="MongoClient"/> is thread-safe, but all calls here come from the single
/// uploader task so there is no contention regardless.
/// </summary>
public sealed class MongoCollectionUploader<T> : DestinationUploader<T>
{
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly string _collectionName;
    private readonly int    _maxConnectionPoolSize;

    private MongoClient?         _client;
    private IMongoCollection<T>? _collection;

    private static readonly InsertManyOptions InsertOptions = new()
    {
        IsOrdered                = false,  // server parallelises; best throughput
        BypassDocumentValidation = false,
    };

    public MongoCollectionUploader(
        string                              connectionString,
        string                              databaseName,
        string                              collectionName,
        UploaderParameters                  parameters,
        BatchSizeTuner                      tuner,
        ILogger<MongoCollectionUploader<T>> logger,
        int                                 maxConnectionPoolSize = 10)
        : base($"Mongo:{databaseName}/{collectionName}", parameters, tuner, logger)
    {
        _connectionString      = connectionString;
        _databaseName          = databaseName;
        _collectionName        = collectionName;
        _maxConnectionPoolSize = maxConnectionPoolSize;
    }

    // ── Connection state ──────────────────────────────────────────────────────

    protected override bool IsConnected => _collection is not null;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override Task ConnectAsync(CancellationToken ct)
    {
        var settings = MongoClientSettings.FromConnectionString(_connectionString);
        settings.MaxConnectionPoolSize = _maxConnectionPoolSize;

        _client     = new MongoClient(settings);
        _collection = _client.GetDatabase(_databaseName).GetCollection<T>(_collectionName);
        return Task.CompletedTask;
    }

    protected override Task DisconnectAsync()
    {
        (_client as IDisposable)?.Dispose();
        _client     = null;
        _collection = null;
        return Task.CompletedTask;
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    protected override async Task UploadBatchAsync(IReadOnlyList<T> batch, CancellationToken ct)
    {
        await _collection!.InsertManyAsync(batch, InsertOptions, ct).ConfigureAwait(false);
    }
}
