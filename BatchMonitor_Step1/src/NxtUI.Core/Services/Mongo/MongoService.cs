using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Configuration;
using NxtUI.Core.Models;
using NxtUI.Filtering;

namespace NxtUI.Core.Services.Mongo;

public class MongoService : IMongoService
{
    // Plain-text terms (no field prefix) search only _id. Field-prefixed terms map
    // directly to the MongoDB document field name — no alias translation needed.
    private static readonly FilterParser DocFilterParser = new(
        searchableFields: ["_id"],
        aliases: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly MongoConnection               _connection;
    private readonly ILogger<MongoService>         _log;

    public MongoService(MongoConnection connection, ILogger<MongoService> log)
    {
        _connection = connection;
        _log        = log;
    }

    public async Task<IReadOnlyList<MongoDatabaseInfo>> GetDatabasesAsync(string env, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: listing databases", env);
        var db     = _connection.GetDatabase(env);
        var client = db.Client;

        var names = await (await client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
        var result = new List<MongoDatabaseInfo>(names.Count);

        foreach (var name in names)
        {
            try
            {
                var d     = client.GetDatabase(name);
                var stats = await d.RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1), cancellationToken: ct);
                result.Add(new MongoDatabaseInfo
                {
                    Name            = name,
                    CollectionCount = stats.GetValue("collections", 0).ToInt64(),
                    SizeBytes       = stats.GetValue("storageSize", 0).ToInt64(),
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "mongo [{Env}]: failed to get stats for database '{Db}'", env, name);
                result.Add(new MongoDatabaseInfo { Name = name });
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetDatabaseNamesAsync(string env, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: listing database names", env);
        var names = await (await _connection.Client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<(IReadOnlyList<MongoCollectionSummary> Collections, long TotalCount)> GetCollectionPageAsync(
        string env, string database, string? search, int skip, int limit, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: listing collections in '{Db}' search={Search} skip={Skip} limit={Limit}",
            env, database, search, skip, limit);

        var db = _connection.Client.GetDatabase(database);

        // Build optional name filter using a case-insensitive regex.
        // ListCollectionsAsync (not ListCollectionNamesAsync) supports a filter document.
        var options = new ListCollectionsOptions();
        if (!string.IsNullOrWhiteSpace(search))
        {
            options.Filter = new BsonDocument("name",
                new BsonDocument("$regex", new BsonRegularExpression(search.Trim(), "i")));
        }

        // Fetch all matching names to get the total count, then slice the page.
        // nameOnly:true makes this metadata-only — fast even with 50k collections.
        var cursor   = await db.ListCollectionsAsync(options, ct);
        var allDocs  = await cursor.ToListAsync(ct);
        var allNames = allDocs.Select(d => d["name"].AsString).ToList();
        allNames.Sort(StringComparer.OrdinalIgnoreCase);
        var total     = (long)allNames.Count;
        var pageNames = allNames.Skip(skip).Take(limit).ToList();

        // Enrich only the page rows with $collStats — parallel, bounded to page size.
        var summaries = await Task.WhenAll(pageNames.Select(name => GetCollectionStatsAsync(env, database, name, ct)));

        return (summaries.OfType<MongoCollectionSummary>().ToList(), total);
    }

    public async Task<IReadOnlyList<string>> GetCollectionNamesAsync(
        string env, string database, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: listing collection names in '{Db}'", env, database);
        var db    = _connection.Client.GetDatabase(database);
        var names = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<MongoCollectionSummary?> GetCollectionStatsAsync(
        string env, string database, string name, CancellationToken ct = default)
    {
        try
        {
            var db       = _connection.Client.GetDatabase(database);
            var col      = db.GetCollection<BsonDocument>(name);
            var pipeline = new[]
            {
                new BsonDocument("$collStats",
                    new BsonDocument("storageStats", new BsonDocument("scale", 1)))
            };
            using var cursor = await col.AggregateAsync<BsonDocument>(
                PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline),
                cancellationToken: ct);
            var doc = await cursor.FirstOrDefaultAsync(ct);
            if (doc is null) return new MongoCollectionSummary { Name = name, StatsLoaded = true };
            var ss = doc["storageStats"].AsBsonDocument;
            return new MongoCollectionSummary
            {
                Name             = name,
                DocumentCount    = ss.GetValue("count",      0).ToInt64(),
                AvgDocSizeBytes  = ss.GetValue("avgObjSize", 0).ToInt64(),
                StorageSizeBytes = ss.GetValue("storageSize",0).ToInt64(),
                IndexCount       = ss.GetValue("nindexes",   0).AsInt32,
                StatsLoaded      = true,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "mongo [{Env}]: failed to get stats for '{Db}/{Col}'", env, database, name);
            return new MongoCollectionSummary { Name = name, StatsLoaded = true };
        }
    }

    public async Task<IReadOnlyList<MongoCollectionSummary>> GetCollectionsAsync(
        string env, string database, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: listing collections in '{Db}'", env, database);
        var names = await GetCollectionNamesAsync(env, database, ct);
        var tasks = names.Select(name => GetCollectionStatsAsync(env, database, name, ct));
        return (await Task.WhenAll(tasks))
            .OfType<MongoCollectionSummary>()
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<(IReadOnlyList<MongoDocument> Documents, long TotalCount)> GetDocumentsAsync(
        string env, string database, string collection,
        string? search, int skip, int limit,
        string? sortField = null, bool sortDesc = false,
        CancellationToken ct = default, bool useUtc = true)
    {
        _log.LogDebug("mongo [{Env}]: querying '{Db}/{Col}' skip={Skip} limit={Limit} search={Search} sort={Sort}{Desc}",
            env, database, collection, skip, limit, search, sortField, sortDesc ? " desc" : "");

        var db  = _connection.Client.GetDatabase(database);
        var col = db.GetCollection<BsonDocument>(collection);

        var filter = BuildDocumentFilter(search, useUtc);

        var total = await col.CountDocumentsAsync(filter, cancellationToken: ct);

        var cursor = col.Find(filter);
        if (!string.IsNullOrWhiteSpace(sortField))
        {
            var sortDef = sortDesc
                ? Builders<BsonDocument>.Sort.Descending(sortField)
                : Builders<BsonDocument>.Sort.Ascending(sortField);
            cursor = cursor.Sort(sortDef);
        }
        var docs = await cursor.Skip(skip).Limit(limit).ToListAsync(ct);

        var result = docs.Select(d =>
        {
            var id        = d.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty;
            DateTime? ts  = null;
            if (d.TryGetValue("timestamp",      out var t1) && t1.BsonType == BsonType.DateTime) ts = t1.ToUniversalTime();
            else if (d.TryGetValue("createdAt", out var t2) && t2.BsonType == BsonType.DateTime) ts = t2.ToUniversalTime();
            else if (d.TryGetValue("updatedAt", out var t3) && t3.BsonType == BsonType.DateTime) ts = t3.ToUniversalTime();

            return new MongoDocument
            {
                Id        = id,
                Json      = d.ToJson(new MongoDB.Bson.IO.JsonWriterSettings
                {
                    OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson,
                }),
                Timestamp = ts,
            };
        }).ToList();

        return (result, total);
    }

    // ── Filter builder ─────────────────────────────────────────────────────────

    private static FilterDefinition<BsonDocument> BuildDocumentFilter(string? search, bool useUtc = true)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return FilterDefinition<BsonDocument>.Empty;
        }

        var s = search.Trim();

        // Raw MongoDB JSON filter: { "field": "value", ... }
        if (s.StartsWith('{'))
        {
            try
            {
                return new BsonDocumentFilterDefinition<BsonDocument>(BsonDocument.Parse(s));
            }
            catch
            {
                // Fall through to field:value parser if JSON is malformed
            }
        }

        // Field:value filter language → AST → MongoDB filter
        try
        {
            var node = DocFilterParser.Parse(s, useUtc);
            return MongoFilterBuilder.Build(node);
        }
        catch
        {
            // Unparseable input: fall back to _id regex so the user sees partial results
            return Builders<BsonDocument>.Filter.Regex("_id", new BsonRegularExpression(s, "i"));
        }
    }

    public async Task<MongoCollectionDetails> GetCollectionDetailsAsync(
        string env, string database, string collection, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: getting details for '{Db}/{Col}'", env, database, collection);
        var db  = _connection.Client.GetDatabase(database);
        var col = db.GetCollection<BsonDocument>(collection);

        MongoCollectionSummary summary;
        try
        {
            var stats = await db.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "collStats", collection }, { "scale", 1 } }, cancellationToken: ct);
            summary = new MongoCollectionSummary
            {
                Name             = collection,
                DocumentCount    = stats.GetValue("count",       0).ToInt64(),
                AvgDocSizeBytes  = stats.GetValue("avgObjSize",  0).ToInt64(),
                StorageSizeBytes = stats.GetValue("storageSize", 0).ToInt64(),
                IndexCount       = stats.GetValue("nindexes",    0).AsInt32,
            };
        }
        catch
        {
            summary = new MongoCollectionSummary { Name = collection };
        }

        var indexCursor = await col.Indexes.ListAsync(ct);
        var indexDocs   = await indexCursor.ToListAsync(ct);
        var indexes = indexDocs.Select(idx => new MongoIndexInfo
        {
            Name   = idx.GetValue("name",   "").AsString,
            Keys   = idx.GetValue("key",    new BsonDocument()).ToJson(),
            Unique = idx.Contains("unique") && idx["unique"].AsBoolean,
            Sparse = idx.Contains("sparse") && idx["sparse"].AsBoolean,
        }).ToList();

        return new MongoCollectionDetails { Summary = summary, Indexes = indexes };
    }

    public async Task DropCollectionAsync(string env, string database, string collection, CancellationToken ct = default)
    {
        _log.LogInformation("mongo [{Env}]: dropping collection '{Db}/{Col}'", env, database, collection);
        var db = _connection.Client.GetDatabase(database);
        await db.DropCollectionAsync(collection, ct);
    }
}
