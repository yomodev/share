// ─────────────────────────────────────────────────────────────────────────────
// Factory pattern usage — realistic example with DI, 30 SQL tables, 10 Mongo
// collections, and several chunk processors running concurrently.
// ─────────────────────────────────────────────────────────────────────────────

using System.Data;
using System.Runtime.CompilerServices;
using BulkUploader.MongoDb;
using BulkUploader.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ── 1. Host setup ─────────────────────────────────────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        // Register both factories — one call each, reads from appsettings.json.
        services.AddSqlBulkUploader(ctx.Configuration);
        services.AddMongoBulkUploader(ctx.Configuration);

        // Register the services that use the factories.
        services.AddSingleton<OrderPipeline>();
        services.AddSingleton<AnalyticsPipeline>();
        services.AddHostedService<ChunkDispatcher>();
    })
    .Build();

await host.RunAsync();

// ─────────────────────────────────────────────────────────────────────────────
// appsettings.json (shown as code for clarity):
//
// "BulkUploader": {
//   "SqlServer": {
//     "ConnectionString": "Server=.;Database=MyDb;Trusted_Connection=True;",
//     "DefaultParameters": {
//       "MaxRetries": 3, "RetryBaseDelayMs": 500,
//       "IdleTimeoutMs": 10000, "FlushAfterIdleMs": 2000
//     },
//     "DefaultTuner": { "Initial": 2000, "Min": 100, "Max": 20000 }
//   },
//   "MongoDb": {
//     "ConnectionString": "mongodb://localhost:27017",
//     "DatabaseName": "analytics",
//     "DefaultParameters": { "FlushAfterIdleMs": 1000 },
//     "DefaultTuner": { "Initial": 500, "Min": 50, "Max": 10000 }
//   }
// }
// ─────────────────────────────────────────────────────────────────────────────


// ── 2. SQL pipeline — owns all SQL table uploaders ────────────────────────────

/// <summary>
/// Owns uploaders for all SQL Server tables. Inject ISqlUploaderFactory,
/// call Create once per table in the constructor, dispose them on shutdown.
///
/// This is the natural home for the factory — one class that knows all the
/// schemas and row mappers, wires them up at startup, and exposes typed
/// EnqueueJobAsync methods to the rest of the application.
/// </summary>
sealed class OrderPipeline : IAsyncDisposable
{
    // One uploader per table — each runs its own three-task pipeline.
    private readonly SqlTableUploader<OrderRecord>     _orders;
    private readonly SqlTableUploader<OrderLineRecord> _orderLines;
    private readonly SqlTableUploader<CustomerRecord>  _customers;
    // ... up to 30 tables, all created the same way.

    public OrderPipeline(ISqlUploaderFactory factory)
    {
        // ── Orders ────────────────────────────────────────────────────────────
        var ordersSchema = new DataTable();
        ordersSchema.Columns.Add("OrderId",    typeof(long));
        ordersSchema.Columns.Add("CustomerId", typeof(int));
        ordersSchema.Columns.Add("Amount",     typeof(decimal));
        ordersSchema.Columns.Add("CreatedAt",  typeof(DateTime));

        _orders = factory.Create<OrderRecord>(
            tableName: "dbo.Orders",
            schema:    ordersSchema,
            rowMapper: (r, row) =>
            {
                row["OrderId"]    = r.OrderId;
                row["CustomerId"] = r.CustomerId;
                row["Amount"]     = r.Amount;
                row["CreatedAt"]  = r.CreatedAt;
                return row;
            });

        // ── Order lines ───────────────────────────────────────────────────────
        var linesSchema = new DataTable();
        linesSchema.Columns.Add("LineId",    typeof(long));
        linesSchema.Columns.Add("OrderId",   typeof(long));
        linesSchema.Columns.Add("ProductId", typeof(int));
        linesSchema.Columns.Add("Qty",       typeof(int));
        linesSchema.Columns.Add("UnitPrice", typeof(decimal));

        _orderLines = factory.Create<OrderLineRecord>(
            tableName: "dbo.OrderLines",
            schema:    linesSchema,
            rowMapper: (r, row) =>
            {
                row["LineId"]    = r.LineId;
                row["OrderId"]   = r.OrderId;
                row["ProductId"] = r.ProductId;
                row["Qty"]       = r.Qty;
                row["UnitPrice"] = r.UnitPrice;
                return row;
            });

        // ── Customers ─────────────────────────────────────────────────────────
        var customersSchema = new DataTable();
        customersSchema.Columns.Add("CustomerId", typeof(int));
        customersSchema.Columns.Add("Name",       typeof(string));
        customersSchema.Columns.Add("Email",      typeof(string));

        _customers = factory.Create<CustomerRecord>(
            tableName: "dbo.Customers",
            schema:    customersSchema,
            rowMapper: (r, row) =>
            {
                row["CustomerId"] = r.CustomerId;
                row["Name"]       = r.Name;
                row["Email"]      = r.Email;
                return row;
            },
            // Per-table override: customers are low-volume, small tuner range.
            tuner: new BulkUploader.Core.BatchSizeTuner(initial: 200, min: 50, max: 2_000));
    }

    // Expose typed enqueue methods — callers never touch the uploaders directly.
    public Task EnqueueOrdersAsync(Func<IAsyncEnumerable<OrderRecord>> producer, CancellationToken ct = default)
        => _orders.EnqueueJobAsync(producer, ct);

    public Task EnqueueOrderLinesAsync(Func<IAsyncEnumerable<OrderLineRecord>> producer, CancellationToken ct = default)
        => _orderLines.EnqueueJobAsync(producer, ct);

    public Task EnqueueCustomersAsync(Func<IAsyncEnumerable<CustomerRecord>> producer, CancellationToken ct = default)
        => _customers.EnqueueJobAsync(producer, ct);

    public async ValueTask DisposeAsync()
    {
        // Drain all pipelines in parallel before shutting down.
        await Task.WhenAll(
            _orders.DisposeAsync().AsTask(),
            _orderLines.DisposeAsync().AsTask(),
            _customers.DisposeAsync().AsTask());
    }
}


// ── 3. Mongo pipeline — owns all collection uploaders ─────────────────────────

sealed class AnalyticsPipeline : IAsyncDisposable
{
    private readonly MongoCollectionUploader<EventDocument>  _events;
    private readonly MongoCollectionUploader<MetricDocument> _metrics;
    // ... up to 10 collections.

    public AnalyticsPipeline(IMongoUploaderFactory factory)
    {
        // No schema or mapper needed — MongoDB is schemaless.
        // Just provide the collection name; factory supplies connection + defaults.
        _events  = factory.Create<EventDocument>("events");
        _metrics = factory.Create<MetricDocument>("metrics",
            // Override database for this collection only.
            databaseName: "metrics_db");
    }

    public Task EnqueueEventsAsync(Func<IAsyncEnumerable<EventDocument>> producer, CancellationToken ct = default)
        => _events.EnqueueJobAsync(producer, ct);

    public Task EnqueueMetricsAsync(Func<IAsyncEnumerable<MetricDocument>> producer, CancellationToken ct = default)
        => _metrics.EnqueueJobAsync(producer, ct);

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            _events.DisposeAsync().AsTask(),
            _metrics.DisposeAsync().AsTask());
    }
}


// ── 4. Chunk dispatcher — uses both pipelines ─────────────────────────────────

sealed class ChunkDispatcher(
    OrderPipeline     orders,
    AnalyticsPipeline analytics,
    ILogger<ChunkDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Simulate chunks arriving from an external source.
        var chunkTasks = Enumerable.Range(0, 20).Select(chunkId =>
            Task.Run(() => ProcessChunkAsync(chunkId, ct), ct));

        await Task.WhenAll(chunkTasks);
        logger.LogInformation("All chunks committed.");
    }

    private async Task ProcessChunkAsync(int chunkId, CancellationToken ct)
    {
        // Fan-out to all destinations concurrently within the chunk.
        // Each EnqueueJobAsync blocks this task until all records are committed.
        await Task.WhenAll(
            orders.EnqueueOrdersAsync(
                () => GenerateOrdersAsync(chunkId, ct), ct),
            orders.EnqueueOrderLinesAsync(
                () => GenerateOrderLinesAsync(chunkId, ct), ct),
            orders.EnqueueCustomersAsync(
                () => GenerateCustomersAsync(chunkId, ct), ct),
            analytics.EnqueueEventsAsync(
                () => GenerateEventsAsync(chunkId, ct), ct),
            analytics.EnqueueMetricsAsync(
                () => GenerateMetricsAsync(chunkId, ct), ct));

        logger.LogInformation("[Chunk {Id}] fully committed.", chunkId);
    }

    // ── Lazy producers ────────────────────────────────────────────────────────
    // In production these would read from a network stream or message bus.

    static async IAsyncEnumerable<OrderRecord> GenerateOrdersAsync(
        int chunkId, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < 10_000; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new OrderRecord(chunkId * 10_000L + i, i % 100, i * 9.99m, DateTime.UtcNow);
            if (i % 1_000 == 0) await Task.Yield();
        }
    }

    static async IAsyncEnumerable<OrderLineRecord> GenerateOrderLinesAsync(
        int chunkId, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < 50_000; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new OrderLineRecord(chunkId * 50_000L + i, chunkId * 10_000L + (i / 5), i % 200, i % 10 + 1, 19.99m);
            if (i % 1_000 == 0) await Task.Yield();
        }
    }

    static async IAsyncEnumerable<CustomerRecord> GenerateCustomersAsync(
        int chunkId, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < 500; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new CustomerRecord(chunkId * 500 + i, $"Customer {i}", $"c{i}@example.com");
            if (i % 100 == 0) await Task.Yield();
        }
    }

    static async IAsyncEnumerable<EventDocument> GenerateEventsAsync(
        int chunkId, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < 20_000; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new EventDocument($"{chunkId}-{i}", "purchase", DateTime.UtcNow);
            if (i % 1_000 == 0) await Task.Yield();
        }
    }

    static async IAsyncEnumerable<MetricDocument> GenerateMetricsAsync(
        int chunkId, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < 5_000; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new MetricDocument($"metric.{i % 20}", i * 0.1, DateTime.UtcNow);
            if (i % 500 == 0) await Task.Yield();
        }
    }
}

// ── Record / document types ───────────────────────────────────────────────────

record OrderRecord(long OrderId, int CustomerId, decimal Amount, DateTime CreatedAt);
record OrderLineRecord(long LineId, long OrderId, int ProductId, int Qty, decimal UnitPrice);
record CustomerRecord(int CustomerId, string Name, string Email);
record EventDocument(string EventId, string Type, DateTime Timestamp);
record MetricDocument(string Name, double Value, DateTime Timestamp);
