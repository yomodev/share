using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Core.Services.Mock;

public class MockMongoService : IMongoService
{
    private readonly HashSet<string> _dropped = new();

    private static readonly IReadOnlyList<MongoDatabaseInfo> _databases =
    [
        new() { Name = "bm-core",      CollectionCount = 6, SizeBytes = 1_482_000_000L },
        new() { Name = "bm-analytics", CollectionCount = 4, SizeBytes =   312_000_000L },
        new() { Name = "bm-config",    CollectionCount = 3, SizeBytes =     2_800_000L },
    ];

    private static readonly Dictionary<string, IReadOnlyList<MongoCollectionSummary>> _collections = new()
    {
        ["bm-core"] =
        [
            new() { Name = "batches",        DocumentCount = 1_248_032, AvgDocSizeBytes =  2_048, StorageSizeBytes =  524_288_000, IndexCount = 4 },
            new() { Name = "batch_steps",    DocumentCount = 8_921_441, AvgDocSizeBytes =    512, StorageSizeBytes =  734_003_200, IndexCount = 3 },
            new() { Name = "events",         DocumentCount = 4_102_887, AvgDocSizeBytes =    384, StorageSizeBytes =  201_326_592, IndexCount = 2 },
            new() { Name = "configurations", DocumentCount =        42, AvgDocSizeBytes =  4_096, StorageSizeBytes =      172_032, IndexCount = 2 },
            new() { Name = "users",          DocumentCount =       318, AvgDocSizeBytes =  1_024, StorageSizeBytes =      325_632, IndexCount = 3 },
            new() { Name = "audit_log",      DocumentCount = 2_890_112, AvgDocSizeBytes =    256, StorageSizeBytes =   22_528_000, IndexCount = 2 },
        ],
        ["bm-analytics"] =
        [
            new() { Name = "daily_metrics",  DocumentCount =     3_650, AvgDocSizeBytes = 12_288, StorageSizeBytes =   44_826_624, IndexCount = 2 },
            new() { Name = "hourly_stats",   DocumentCount =    87_600, AvgDocSizeBytes =  4_096, StorageSizeBytes =  258_998_272, IndexCount = 2 },
            new() { Name = "error_reports",  DocumentCount =     1_204, AvgDocSizeBytes =  6_144, StorageSizeBytes =    7_389_184, IndexCount = 2 },
            new() { Name = "dashboards",     DocumentCount =        28, AvgDocSizeBytes =  8_192, StorageSizeBytes =      229_376, IndexCount = 1 },
        ],
        ["bm-config"] =
        [
            new() { Name = "environments",   DocumentCount =  4, AvgDocSizeBytes = 2_048, StorageSizeBytes =   8_192, IndexCount = 1 },
            new() { Name = "feature_flags",  DocumentCount = 37, AvgDocSizeBytes =   512, StorageSizeBytes =  18_944, IndexCount = 1 },
            new() { Name = "alert_rules",    DocumentCount = 24, AvgDocSizeBytes = 1_024, StorageSizeBytes =  24_576, IndexCount = 2 },
        ],
    };

    private static readonly string[] Statuses     = ["Running", "Completed", "Failed"];
    private static readonly string[] EventTypes   = ["BATCH_STARTED", "BATCH_COMPLETED", "STEP_FAILED", "RETRY", "CANCELLED"];
    private static readonly string[] ConfigKeys   = ["maxRetries", "timeoutSec", "parallelism", "alertThreshold", "batchSize"];
    private static readonly string[] Roles        = ["admin", "operator", "viewer", "developer"];
    private static readonly string[] AuditActions = ["LOGIN", "LOGOUT", "READ", "WRITE", "DELETE", "STOP_BATCH"];
    private static readonly string[] Resources    = ["batches", "users", "config", "events", "reports"];
    private static readonly string[] FlagNames    = ["newUi", "kafka_v2", "analytics", "realtime", "darkMode", "export_csv"];
    private static readonly string[] Severities   = ["critical", "warning", "info"];
    private static readonly string[] EnvNames     = ["prod", "staging", "dev", "uat"];

    private static MongoDocument MakeDoc(string id, string json, DateTime? ts) =>
        new() { Id = id, Json = json, Timestamp = ts };

    private static string NewId(int i) => $"{unchecked((long)0xDEAD_BEEF_0000_0000UL + i):x16}{i:x8}";
    private static string ShortGuid()  => Guid.NewGuid().ToString("N")[..8];
    private static string Bool(bool b) => b ? "true" : "false";

    private MongoDocument GenerateDoc(string collection, int i, string id) => collection switch
    {
        "batches" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"Name\":\"BatchJob-{i % 18 + 1}\",\"runId\":\"{ShortGuid()}\",\"status\":\"{Statuses[i % 3]}\",\"startedAt\":\"{DateTime.UtcNow.AddMinutes(-i * 3):o}\",\"durationMs\":{(i % 60 + 1) * 1800},\"environment\":\"prod\",\"retries\":{i % 3}}}",
            DateTime.UtcNow.AddMinutes(-i * 3)),

        "batch_steps" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"batchId\":\"{ShortGuid()}\",\"stepName\":\"Step-{i % 8 + 1}\",\"status\":\"{Statuses[i % 3]}\",\"startedAt\":\"{DateTime.UtcNow.AddMinutes(-i):o}\",\"durationMs\":{(i % 30 + 1) * 500}}}",
            DateTime.UtcNow.AddMinutes(-i)),

        "events" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"eventType\":\"{EventTypes[i % EventTypes.Length]}\",\"entityId\":\"{ShortGuid()}\",\"timestamp\":\"{DateTime.UtcNow.AddMinutes(-i * 0.5):o}\",\"payload\":{{\"field_{i % 5}\":\"value_{i}\",\"amount\":{i * 7 + 10}}},\"source\":\"batch-svc\"}}",
            DateTime.UtcNow.AddMinutes(-i * 0.5)),

        "configurations" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"key\":\"config.{ConfigKeys[i % ConfigKeys.Length]}\",\"value\":\"{Bool(i % 2 == 0)}\",\"description\":\"System configuration entry {i}\",\"updatedAt\":\"{DateTime.UtcNow.AddDays(-i):o}\",\"updatedBy\":\"admin\"}}",
            DateTime.UtcNow.AddDays(-i)),

        "users" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"username\":\"user{i:D3}\",\"email\":\"user{i}@example.com\",\"role\":\"{Roles[i % Roles.Length]}\",\"lastLogin\":\"{DateTime.UtcNow.AddHours(-i * 2):o}\",\"active\":{Bool(i % 5 != 0)}}}",
            DateTime.UtcNow.AddHours(-i * 2)),

        "audit_log" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"action\":\"{AuditActions[i % AuditActions.Length]}\",\"userId\":\"user{i % 20:D3}\",\"resource\":\"/{Resources[i % Resources.Length]}/{i % 100}\",\"timestamp\":\"{DateTime.UtcNow.AddMinutes(-i * 2):o}\",\"ip\":\"10.0.{i % 10}.{i % 255}\",\"success\":{Bool(i % 10 != 0)}}}",
            DateTime.UtcNow.AddMinutes(-i * 2)),

        "daily_metrics" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"date\":\"{DateTime.UtcNow.AddDays(-i):yyyy-MM-dd}\",\"totalBatches\":{i * 3 + 42},\"succeeded\":{i * 3 + 40},\"failed\":{i % 3},\"avgDurationMs\":{45_000 + i * 100},\"p99DurationMs\":{120_000 + i * 200}}}",
            DateTime.UtcNow.AddDays(-i)),

        "hourly_stats" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"hour\":\"{DateTime.UtcNow.AddHours(-i):yyyy-MM-ddTHH:00:00}\",\"eventsProcessed\":{i * 120 + 800},\"errorsCount\":{i % 5},\"throughputPerSec\":{22.4 + i * 0.1:F1}}}",
            DateTime.UtcNow.AddHours(-i)),

        "error_reports" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"errorCode\":\"ERR-{1000 + i % 20}\",\"message\":\"Unexpected failure in step {i % 8 + 1}\",\"batchId\":\"{ShortGuid()}\",\"occurredAt\":\"{DateTime.UtcNow.AddHours(-i):o}\",\"stack\":\"at BatchStep.Execute() line {i % 200 + 1}\",\"resolved\":{Bool(i % 3 == 0)}}}",
            DateTime.UtcNow.AddHours(-i)),

        "environments" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"name\":\"{EnvNames[i % 4]}\",\"brokers\":[\"kafka-1:9092\",\"kafka-2:9092\"],\"mongoUri\":\"mongodb://mongo-{i % 4}:27017\",\"active\":true}}",
            null),

        "feature_flags" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"flag\":\"feature.{FlagNames[i % FlagNames.Length]}\",\"enabled\":{Bool(i % 3 != 2)},\"rolloutPercent\":{(i % 4 + 1) * 25},\"description\":\"Feature flag {i}\",\"updatedAt\":\"{DateTime.UtcNow.AddDays(-i / 3):o}\"}}",
            null),

        "alert_rules" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"name\":\"Alert-{i:D3}\",\"condition\":\"failedBatches > {i % 5 + 1}\",\"severity\":\"{Severities[i % Severities.Length]}\",\"notifySlack\":{Bool(i % 2 == 0)},\"enabled\":true}}",
            null),

        "dashboards" => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"title\":\"Dashboard {i + 1}\",\"panels\":{i % 6 + 2},\"owner\":\"user{i % 5:D3}\",\"createdAt\":\"{DateTime.UtcNow.AddDays(-i * 7):o}\"}}",
            null),

        _ => MakeDoc(id,
            $"{{\"_id\":\"{id}\",\"index\":{i},\"collection\":\"{collection}\"}}",
            DateTime.UtcNow.AddMinutes(-i)),
    };

    private static readonly Dictionary<string, IReadOnlyList<MongoIndexInfo>> _indexes = new()
    {
        ["batches"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "batchName_status",    Keys = "{ Name: 1, status: 1 }",             Unique = false },
            new() { Name = "startedAt",           Keys = "{ startedAt: -1 }",                       Unique = false },
            new() { Name = "environment_status",  Keys = "{ environment: 1, status: 1 }",           Unique = false },
        ],
        ["batch_steps"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "batchId",            Keys = "{ batchId: 1 }",                          Unique = false },
            new() { Name = "stepName_status",    Keys = "{ stepName: 1, status: 1 }",              Unique = false },
        ],
        ["events"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "timestamp",          Keys = "{ timestamp: -1 }",                       Unique = false },
        ],
        ["configurations"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "key_unique",         Keys = "{ key: 1 }",                              Unique = true  },
        ],
        ["users"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "username_unique",    Keys = "{ username: 1 }",                         Unique = true  },
            new() { Name = "email_unique",       Keys = "{ email: 1 }",                            Unique = true  },
        ],
        ["audit_log"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "timestamp",          Keys = "{ timestamp: -1 }",                       Unique = false },
        ],
        ["daily_metrics"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "date_unique",        Keys = "{ date: 1 }",                             Unique = true  },
        ],
        ["hourly_stats"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "hour_unique",        Keys = "{ hour: 1 }",                             Unique = true  },
        ],
        ["error_reports"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "occurredAt",         Keys = "{ occurredAt: -1 }",                      Unique = false },
        ],
        ["environments"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
        ],
        ["feature_flags"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
        ],
        ["alert_rules"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
            new() { Name = "severity",           Keys = "{ severity: 1 }",                         Unique = false },
        ],
        ["dashboards"] =
        [
            new() { Name = "_id_",               Keys = "{ _id: 1 }",                              Unique = true  },
        ],
    };

    public Task<IReadOnlyList<string>> GetDatabaseNamesAsync(string env, CancellationToken ct = default)
    {
        IReadOnlyList<string> names = _databases.Select(d => d.Name).ToList();
        return Task.FromResult(names);
    }

    public Task<(IReadOnlyList<MongoCollectionSummary> Collections, long TotalCount)> GetCollectionPageAsync(
        string env, string database, string? search, int skip, int limit, CancellationToken ct = default)
    {
        var all = _collections.TryGetValue(database, out var cols)
            ? cols.Where(c => !_dropped.Contains($"{database}/{c.Name}")).ToList()
            : [];

        if (!string.IsNullOrWhiteSpace(search))
        {
            all = all.Where(c => c.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        all = all.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var total = (long)all.Count;
        IReadOnlyList<MongoCollectionSummary> page = all.Skip(skip).Take(limit)
            .Select(c => c with { StatsLoaded = true }).ToList();
        return Task.FromResult((page, total));
    }

    public Task<IReadOnlyList<MongoDatabaseInfo>> GetDatabasesAsync(string env, CancellationToken ct = default)
        => Task.FromResult(_databases);

    public Task<IReadOnlyList<string>> GetCollectionNamesAsync(string env, string database, CancellationToken ct = default)
    {
        var names = _collections.TryGetValue(database, out var cols)
            ? (IReadOnlyList<string>)cols.Where(c => !_dropped.Contains($"{database}/{c.Name}")).Select(c => c.Name).ToList()
            : (IReadOnlyList<string>)[];
        return Task.FromResult(names);
    }

    public Task<MongoCollectionSummary?> GetCollectionStatsAsync(string env, string database, string name, CancellationToken ct = default)
    {
        MongoCollectionSummary? result = null;
        if (_collections.TryGetValue(database, out var cols))
            result = cols.FirstOrDefault(c => c.Name == name && !_dropped.Contains($"{database}/{c.Name}"));
        return Task.FromResult(result is null ? null : result with { StatsLoaded = true });
    }

    public Task<IReadOnlyList<MongoCollectionSummary>> GetCollectionsAsync(string env, string database, CancellationToken ct = default)
    {
        var result = _collections.TryGetValue(database, out var cols)
            ? (IReadOnlyList<MongoCollectionSummary>)cols.Where(c => !_dropped.Contains($"{database}/{c.Name}"))
                                                         .Select(c => c with { StatsLoaded = true }).ToList()
            : (IReadOnlyList<MongoCollectionSummary>)[];
        return Task.FromResult(result);
    }

    public Task<MongoCollectionDetails> GetCollectionDetailsAsync(string env, string database, string collection, CancellationToken ct = default)
    {
        var summary = _collections.TryGetValue(database, out var cols)
            ? cols.FirstOrDefault(c => c.Name == collection) ?? new MongoCollectionSummary { Name = collection }
            : new MongoCollectionSummary { Name = collection };
        var indexes = _indexes.TryGetValue(collection, out var idx) ? idx : (IReadOnlyList<MongoIndexInfo>)[new() { Name = "_id_", Keys = "{ _id: 1 }", Unique = true }];
        return Task.FromResult(new MongoCollectionDetails { Summary = summary, Indexes = indexes });
    }

    public Task DropCollectionAsync(string env, string database, string collection, CancellationToken ct = default)
    {
        _dropped.Add($"{database}/{collection}");
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<MongoDocument> Documents, long TotalCount)> GetDocumentsAsync(
        string env, string database, string collection,
        string? search, int skip, int limit,
        string? sortField = null, bool sortDesc = false,
        CancellationToken ct = default)
    {
        var colMeta  = _collections.TryGetValue(database, out var cols)
            ? cols.FirstOrDefault(c => c.Name == collection) : null;
        var total    = colMeta?.DocumentCount ?? 100;
        var genLimit = (int)Math.Min(total, 500);

        var allDocs = Enumerable.Range(0, genLimit)
            .Select(i => GenerateDoc(collection, i, NewId(i)))
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            allDocs = allDocs
                .Where(d => d.Json.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            total = allDocs.Count;
        }

        var page = allDocs.Skip(skip).Take(limit).ToList();
        return Task.FromResult(((IReadOnlyList<MongoDocument>)page, total));
    }
}
