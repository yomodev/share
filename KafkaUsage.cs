// ─────────────────────────────────────────────────────────────────────────────
// Kafka usage examples
// ─────────────────────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BulkUploader.Core;
using BulkUploader.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

// ── Example 1: Simple JSON events ────────────────────────────────────────────
// Most common case: serialize T as JSON, use a field as the partition key.

var eventUploader = new KafkaTopicUploader<OrderEvent>(
    topic:            "order-events",
    bootstrapServers: "kafka-broker-1:9092,kafka-broker-2:9092",

    // Serialize the record to JSON bytes — called per record, keep it fast.
    valueSerializer: rec => JsonSerializer.SerializeToUtf8Bytes(rec),

    // Partition by CustomerId so all events for the same customer are ordered.
    keySelector: rec => Encoding.UTF8.GetBytes(rec.CustomerId.ToString()),

    // Attach metadata headers for downstream consumers.
    headersBuilder: rec =>
    {
        var h = new Headers();
        h.Add("content-type",   Encoding.UTF8.GetBytes("application/json"));
        h.Add("schema-version", Encoding.UTF8.GetBytes("v2"));
        h.Add("source-system",  Encoding.UTF8.GetBytes("order-processor"));
        return h;
    },

    parameters: new UploaderParameters(
        recordChannelCapacity: 200_000,
        maxRetries:            3,
        retryBaseDelayMs:      500,
        idleTimeoutMs:         30_000,   // Kafka flush on disconnect is expensive — stay connected longer
        flushAfterIdleMs:      500),     // flush small batches quickly for low-latency topics

    tuner: new BatchSizeTuner(
        initial: 1_000,
        min:     100,
        max:     10_000),               // Kafka handles large batches well

    logger: loggerFactory.CreateLogger<KafkaTopicUploader<OrderEvent>>(),

    // Override producer config for this specific topic.
    extraConfig: new Dictionary<string, string>
    {
        ["linger.ms"]        = "5",     // low-latency: flush after 5ms
        ["compression.type"] = "lz4",
    });

await using var _ = eventUploader;

// ── Example 2: Multi-format — different serializers per topic ─────────────────
// If you need Avro or Protobuf, swap the valueSerializer delegate.

// Protobuf (using Google.Protobuf):
// valueSerializer: rec => rec.ToByteArray()

// MessagePack (using MessagePack-CSharp):
// valueSerializer: rec => MessagePackSerializer.Serialize(rec)

// ── Example 3: Null key (round-robin partitioning) ────────────────────────────
// When ordering across partitions doesn't matter, omit keySelector entirely.

var metricsUploader = new KafkaTopicUploader<MetricPoint>(
    topic:            "app-metrics",
    bootstrapServers: "kafka-broker-1:9092",
    valueSerializer:  rec => JsonSerializer.SerializeToUtf8Bytes(rec),
    parameters:       new UploaderParameters(flushAfterIdleMs: 1_000),
    tuner:            new BatchSizeTuner(initial: 5_000, min: 500, max: 50_000),
    logger:           loggerFactory.CreateLogger<KafkaTopicUploader<MetricPoint>>(),

    // No keySelector → null key → Kafka round-robins across partitions.
    extraConfig: new Dictionary<string, string>
    {
        ["linger.ms"]        = "20",    // throughput-optimised: batch for 20ms
        ["compression.type"] = "snappy",
    });

await using var __ = metricsUploader;

// ── Example 4: Chunk processor pattern (same as SQL/Mongo) ───────────────────

var chunkTasks = Enumerable.Range(0, 10).Select(chunkId => Task.Run(async () =>
{
    // Each chunk processor enqueues a job. The producer delegate is lazy —
    // records are serialized inside the uploader pipeline, not here.
    await eventUploader.EnqueueJobAsync(
        () => GenerateOrderEventsAsync(chunkId, count: 5_000));

    Console.WriteLine($"[Chunk {chunkId:D2}] all events committed to Kafka.");
}));

await Task.WhenAll(chunkTasks);

// ── Example 5: Throughput diagnostics ────────────────────────────────────────
// KafkaTopicUploader exposes lifetime counters for monitoring.
Console.WriteLine($"Total produced: {eventUploader.TotalMessagesProduced:N0} msgs, " +
                  $"{eventUploader.TotalBytesProduced / 1024.0 / 1024.0:F1} MB");

// ── Tuner override (same as other destinations) ───────────────────────────────
// If you observe broker-side batch-too-large errors, dial down immediately.
eventUploader.Tuner.Override(500);

// ── Types ─────────────────────────────────────────────────────────────────────

record OrderEvent(long OrderId, int CustomerId, string EventType, decimal Amount, DateTime OccurredAt);
record MetricPoint(string Name, double Value, DateTime Timestamp, Dictionary<string, string> Tags);

// ── Producer ──────────────────────────────────────────────────────────────────

static async IAsyncEnumerable<OrderEvent> GenerateOrderEventsAsync(
    int chunkId, int count,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    for (var i = 0; i < count; i++)
    {
        ct.ThrowIfCancellationRequested();
        yield return new OrderEvent(
            OrderId:    (long)chunkId * 100_000 + i,
            CustomerId: i % 500,
            EventType:  i % 3 == 0 ? "created" : i % 3 == 1 ? "updated" : "shipped",
            Amount:     (decimal)(i * 9.99),
            OccurredAt: DateTime.UtcNow);

        if (i % 1_000 == 0) await Task.Yield();
    }
}
