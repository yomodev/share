using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>Read-only Mongo browsing operations. Safe to inject in any component.</summary>
public interface IMongoReader
{
    /// <summary>Returns database names only — no stats. Fast even with 100+ databases.</summary>
    Task<IReadOnlyList<string>> GetDatabaseNamesAsync(string env, CancellationToken ct = default);

    /// <summary>
    /// The database this environment is actually configured to use (Mongo:DatabaseName),
    /// as opposed to just any database visible on the server. Pure config lookup, no I/O —
    /// null if the environment has no configured default (e.g. mock/dev mode).
    /// </summary>
    string? GetDefaultDatabaseName(string env);
    Task<IReadOnlyList<MongoDatabaseInfo>>      GetDatabasesAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<string>>                 GetCollectionNamesAsync(string env, string database, CancellationToken ct = default);
    Task<MongoCollectionSummary?>               GetCollectionStatsAsync(string env, string database, string name, CancellationToken ct = default);
    Task<IReadOnlyList<MongoCollectionSummary>> GetCollectionsAsync(string env, string database, CancellationToken ct = default);
    /// <summary>
    /// Returns one page of collections matching an optional name filter, with stats
    /// loaded only for the returned page. Suitable for large databases (50k+ collections).
    /// </summary>
    Task<(IReadOnlyList<MongoCollectionSummary> Collections, long TotalCount)> GetCollectionPageAsync(
        string env, string database, string? search, int skip, int limit, CancellationToken ct = default);
    Task<(IReadOnlyList<MongoDocument> Documents, long TotalCount)> GetDocumentsAsync(
        string env, string database, string collection,
        string? search, int skip, int limit,
        string? sortField = null, bool sortDesc = false,
        CancellationToken ct = default, bool useUtc = true);
    Task<MongoCollectionDetails> GetCollectionDetailsAsync(string env, string database, string collection, CancellationToken ct = default);
}

/// <summary>Destructive Mongo admin operations. Inject only where mutations are explicitly needed.</summary>
public interface IMongoAdmin
{
    Task DropCollectionAsync(string env, string database, string collection, CancellationToken ct = default);
}

/// <summary>Combined interface implemented by full Mongo service implementations.</summary>
public interface IMongoService : IMongoReader, IMongoAdmin { }
