using BulkUploader.Core;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace BulkUploader.Kafka;

/// <summary>
/// Uploads records of type <typeparamref name="T"/> to a single Kafka topic
/// using the Confluent.Kafka producer with fire-and-confirm batch semantics.
///
/// ────────────────────────────────────────────────────────────────────────────
/// KAFKA VS SQL/MONGO — KEY DIFFERENCES
/// ────────────────────────────────────────────────────────────────────────────
///
/// 1. NO TRUE BULK INSERT
///    Kafka has no batch-insert API. Records are individually produced via
///    ProduceAsync. What makes this efficient is:
///      (a) acks=all with linger.ms > 0 — the broker batches on its side.
///      (b) We call ProduceAsync (fire) for every record and then flush once
///          at the end of the batch. This keeps the producer's internal send
///          buffer busy and maximises broker-side batching.
///
/// 2. DELIVERY CONFIRMATION
///    We use the fire-and-collect pattern:
///      • ProduceAsync for all records → collects DeliveryResult tasks.
///      • Await all delivery results before returning from UploadBatchAsync.
///    This gives us genuine at-least-once delivery guarantees and surfaces
///    any broker-side error back to the retry logic in the base class.
///
/// 3. PARTITIONING
///    An optional <see cref="KeySelector"/> delegate provides the Kafka message
///    key (byte[]). Records with the same key always land on the same partition,
///    preserving ordering guarantees within a key. Pass null to use random
///    round-robin partitioning.
///
/// 4. SERIALIZATION
///    A <see cref="ValueSerializer"/> delegate converts T → byte[].
///    Keep it fast and allocation-light (e.g. System.Text.Json, MessagePack,
///    Protobuf). It is called once per record inside UploadBatchAsync — always
///    on the single uploader task, so no thread-safety concerns.
///
/// 5. CONNECTION / DISCONNECT SEMANTICS
///    IProducer is thread-safe and long-lived by design. "Connect" means
///    creating the producer. "Disconnect" means Flush + close, draining any
///    messages still in the internal send buffer. Idle disconnect is therefore
///    slightly more expensive than for SQL/Mongo — tune IdleTimeoutMs
///    accordingly (e.g. 30 000 ms rather than 10 000 ms).
///
/// 6. HEADERS
///    An optional <see cref="HeadersBuilder"/> delegate can attach Kafka
///    message headers (e.g. trace IDs, schema version, content-type).
///
/// ────────────────────────────────────────────────────────────────────────────
/// DELIVERY GUARANTEES
/// ────────────────────────────────────────────────────────────────────────────
/// With acks=all and enable.idempotence=true (both set by default here):
///   • Each message is written to all in-sync replicas before ack.
///   • The producer retries transparently on transient broker errors.
///   • Exactly-once requires an external transaction coordinator; this
///     implementation provides at-least-once.
///
/// ────────────────────────────────────────────────────────────────────────────
/// RECOMMENDED PRODUCER CONFIG FOR THROUGHPUT
/// ────────────────────────────────────────────────────────────────────────────
///   linger.ms          = 5–20     (allows broker-side batching)
///   batch.size         = 1048576  (1 MB broker batch)
///   compression.type   = lz4      (best throughput/ratio trade-off)
///   acks               = all
///   enable.idempotence = true
/// ────────────────────────────────────────────────────────────────────────────
/// </summary>
public sealed class KafkaTopicUploader<T> : DestinationUploader<T>
{
    // ── Configuration ─────────────────────────────────────────────────────────

    private readonly ProducerConfig              _config;
    private readonly string                      _topic;
    private readonly Func<T, byte[]>             _valueSerializer;
    private readonly Func<T, byte[]?>?           _keySelector;        // null = round-robin
    private readonly Func<T, Headers>?           _headersBuilder;     // null = no headers
    private readonly TimeSpan                    _flushTimeout;

    // ── Runtime state (uploader task only) ───────────────────────────────────

    private IProducer<byte[], byte[]>? _producer;

    // ── Delivery tracking ─────────────────────────────────────────────────────

    // Counters exposed for diagnostics / OnBatchSucceededAsync overrides.
    private long _totalMessagesProduced;
    private long _totalBytesProduced;

    public long TotalMessagesProduced => Volatile.Read(ref _totalMessagesProduced);
    public long TotalBytesProduced    => Volatile.Read(ref _totalBytesProduced);

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="topic">Kafka topic to produce to.</param>
    /// <param name="bootstrapServers">Comma-separated list of broker addresses.</param>
    /// <param name="valueSerializer">Converts T to the Kafka message value (byte[]).</param>
    /// <param name="keySelector">
    ///   Optional. Returns the message key bytes for a record. Records with the same
    ///   key always land on the same partition. Pass null for round-robin.
    /// </param>
    /// <param name="headersBuilder">
    ///   Optional. Returns a <see cref="Headers"/> collection for each record.
    /// </param>
    /// <param name="extraConfig">
    ///   Optional extra producer config entries merged over the safe defaults.
    ///   Use to set linger.ms, compression.type, etc.
    /// </param>
    /// <param name="flushTimeoutSeconds">
    ///   How long to wait for in-flight messages when disconnecting. Default 30s.
    /// </param>
    public KafkaTopicUploader(
        string                      topic,
        string                      bootstrapServers,
        Func<T, byte[]>             valueSerializer,
        UploaderParameters          parameters,
        BatchSizeTuner              tuner,
        ILogger<KafkaTopicUploader<T>> logger,
        Func<T, byte[]?>?           keySelector          = null,
        Func<T, Headers>?           headersBuilder       = null,
        IDictionary<string, string>? extraConfig         = null,
        int                         flushTimeoutSeconds  = 30)
        : base($"Kafka:{topic}", parameters, tuner, logger)
    {
        _topic           = topic;
        _valueSerializer = valueSerializer;
        _keySelector     = keySelector;
        _headersBuilder  = headersBuilder;
        _flushTimeout    = TimeSpan.FromSeconds(flushTimeoutSeconds);

        // ── Build producer config with safe defaults ──────────────────────────
        // These defaults prioritise durability and throughput. Override via
        // extraConfig if you need different trade-offs.
        var cfg = new Dictionary<string, string>
        {
            // Durability
            ["acks"]               = "all",
            ["enable.idempotence"] = "true",

            // Throughput — broker-side batching window
            ["linger.ms"]          = "10",
            ["batch.size"]         = (1024 * 1024).ToString(), // 1 MB

            // Compression — lz4 is the best throughput/ratio for most workloads
            ["compression.type"]   = "lz4",

            // Retries — let the producer retry transient errors transparently
            ["retries"]                   = int.MaxValue.ToString(),
            ["retry.backoff.ms"]          = "250",
            ["delivery.timeout.ms"]       = "120000",  // 2 min hard deadline
            ["max.in.flight.requests.per.connection"] = "5",
        };

        // Merge caller overrides (caller wins).
        if (extraConfig is not null)
            foreach (var (k, v) in extraConfig)
                cfg[k] = v;

        cfg["bootstrap.servers"] = bootstrapServers;

        _config = new ProducerConfig(cfg);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    protected override bool IsConnected => _producer is not null;

    protected override Task ConnectAsync(CancellationToken ct)
    {
        // IProducer is created once and reused across many batches.
        // It manages its own internal send buffer, retry logic, and TCP connections.
        _producer = new ProducerBuilder<byte[], byte[]>(_config)
            .SetErrorHandler((_, e) =>
                Logger.LogError("[{Dest}] Kafka producer error: {Reason} (fatal={Fatal})",
                    DestinationName, e.Reason, e.IsFatal))
            .SetLogHandler((_, m) =>
                Logger.LogDebug("[{Dest}] Kafka: [{Level}] {Message}",
                    DestinationName, m.Level, m.Message))
            .Build();

        return Task.CompletedTask;
    }

    protected override async Task DisconnectAsync()
    {
        if (_producer is null) return;

        // Flush drains the internal send buffer — essential before close.
        // Any undelivered messages will surface errors here.
        try
        {
            _producer.Flush(_flushTimeout);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Dest}] Flush on disconnect reported errors.", DestinationName);
        }

        _producer.Dispose();
        _producer = null;
        await Task.CompletedTask; // satisfy async signature
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Upload — fire-and-collect pattern
    // ─────────────────────────────────────────────────────────────────────────

    protected override async Task UploadBatchAsync(IReadOnlyList<T> batch, CancellationToken ct)
    {
        // Phase 1 — Fire: hand all messages to the producer's internal send buffer.
        // ProduceAsync returns a Task<DeliveryResult> that resolves when the broker
        // acknowledges (or fails). We collect all tasks before awaiting any —
        // this keeps the producer pipeline full and maximises broker-side batching.

        var deliveryTasks = new List<Task<DeliveryResult<byte[], byte[]>>>(batch.Count);
        long batchBytes = 0;

        foreach (var record in batch)
        {
            ct.ThrowIfCancellationRequested();

            var value   = _valueSerializer(record);
            var key     = _keySelector?.Invoke(record);
            var headers = _headersBuilder?.Invoke(record);

            batchBytes += value.Length + (key?.Length ?? 0);

            var message = new Message<byte[], byte[]>
            {
                Key     = key ?? Array.Empty<byte>(),
                Value   = value,
                Headers = headers,
            };

            deliveryTasks.Add(_producer!.ProduceAsync(_topic, message, ct));
        }

        // Phase 2 — Confirm: await all deliveries.
        // Any single failure throws here and triggers the base class retry.
        // WhenAll aggregates all failures into an AggregateException if multiple
        // messages fail — the base class will see this and retry the entire batch.
        var results = await Task.WhenAll(deliveryTasks).ConfigureAwait(false);

        // Phase 3 — Verify: check all results for unexpected non-Error status.
        // ProduceAsync only throws on hard failures; check PersistenceStatus for
        // soft failures (e.g. PossiblyPersisted after a network blip).
        var notPersisted = results
            .Where(r => r.Status != PersistenceStatus.Persisted)
            .ToList();

        if (notPersisted.Count > 0)
        {
            throw new InvalidOperationException(
                $"{notPersisted.Count}/{batch.Count} messages not confirmed as Persisted. " +
                $"First status: {notPersisted[0].Status} offset={notPersisted[0].Offset}");
        }

        // Update diagnostic counters (Interlocked because Tuner.RecordBatch is
        // called from the uploader task, but callers may read these from outside).
        Interlocked.Add(ref _totalMessagesProduced, batch.Count);
        Interlocked.Add(ref _totalBytesProduced, batchBytes);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Optional hooks — override in subclasses for richer observability
    // ─────────────────────────────────────────────────────────────────────────

    protected override Task OnBatchSucceededAsync(int recordCount, TimeSpan elapsed, CancellationToken ct)
    {
        Logger.LogDebug(
            "[{Dest}] Produced {Count} messages in {Ms:F1}ms | total: {Total:N0} msgs / {Bytes:N0} bytes",
            DestinationName, recordCount, elapsed.TotalMilliseconds,
            TotalMessagesProduced, TotalBytesProduced);

        return Task.CompletedTask;
    }

    protected override Task OnBatchFailedAsync(int recordCount, UploadException exception, CancellationToken ct)
    {
        Logger.LogError(exception,
            "[{Dest}] Batch of {Count} messages permanently failed. " +
            "Consider checking broker health, topic existence, and ACL permissions.",
            DestinationName, recordCount);

        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync(CancellationToken ct)
    {
        Logger.LogInformation(
            "[{Dest}] Shutdown complete. Lifetime totals: {Msgs:N0} messages, {Bytes:N0} bytes.",
            DestinationName, TotalMessagesProduced, TotalBytesProduced);

        return Task.CompletedTask;
    }
}
