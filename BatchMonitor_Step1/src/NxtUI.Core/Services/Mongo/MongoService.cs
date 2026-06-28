using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Mongo;

public class MongoService : IMongoService
{
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
                    CollectionCount = stats.GetValue("collections", 0).AsInt32,
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

    public async Task<IReadOnlyList<MongoCollectionSummary>> GetCollectionsAsync(
        string env, string database, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: listing collections in '{Db}'", env, database);
        var db      = _connection.Client.GetDatabase(database);
        var names   = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        var result  = new List<MongoCollectionSummary>(names.Count);

        foreach (var name in names)
        {
            try
            {
                var stats = await db.RunCommandAsync<BsonDocument>(
                    new BsonDocument { { "collStats", name }, { "scale", 1 } }, cancellationToken: ct);

                result.Add(new MongoCollectionSummary
                {
                    Name             = name,
                    DocumentCount    = stats.GetValue("count",       0).ToInt64(),
                    AvgDocSizeBytes  = stats.GetValue("avgObjSize",  0).ToInt64(),
                    StorageSizeBytes = stats.GetValue("storageSize", 0).ToInt64(),
                    IndexCount       = stats.GetValue("nindexes",    0).AsInt32,
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "mongo [{Env}]: failed to get stats for collection '{Db}/{Col}'", env, database, name);
                result.Add(new MongoCollectionSummary { Name = name });
            }
        }

        return result;
    }

    public async Task<(IReadOnlyList<MongoDocument> Documents, long TotalCount)> GetDocumentsAsync(
        string env, string database, string collection,
        string? search, int skip, int limit, CancellationToken ct = default)
    {
        _log.LogDebug("mongo [{Env}]: querying '{Db}/{Col}' skip={Skip} limit={Limit} search={Search}",
            env, database, collection, skip, limit, search);

        var db  = _connection.Client.GetDatabase(database);
        var col = db.GetCollection<BsonDocument>(collection);

        FilterDefinition<BsonDocument> filter = FilterDefinition<BsonDocument>.Empty;
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Try to match as JSON fragment or as a text search
            filter = Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Regex("_id", new BsonRegularExpression(search.Trim(), "i")),
                Builders<BsonDocument>.Filter.Text(search.Trim()));
        }

        var total = await col.CountDocumentsAsync(filter, cancellationToken: ct);
        var docs  = await col.Find(filter).Skip(skip).Limit(limit).ToListAsync(ct);

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
                Json      = d.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = false }),
                Timestamp = ts,
            };
        }).ToList();

        return (result, total);
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
