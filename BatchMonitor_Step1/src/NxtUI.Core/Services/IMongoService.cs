using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>Read-only Mongo browsing operations. Safe to inject in any component.</summary>
public interface IMongoReader
{
    Task<IReadOnlyList<MongoDatabaseInfo>>      GetDatabasesAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<string>>                 GetCollectionNamesAsync(string env, string database, CancellationToken ct = default);
    Task<MongoCollectionSummary?>               GetCollectionStatsAsync(string env, string database, string name, CancellationToken ct = default);
    Task<IReadOnlyList<MongoCollectionSummary>> GetCollectionsAsync(string env, string database, CancellationToken ct = default);
    Task<(IReadOnlyList<MongoDocument> Documents, long TotalCount)> GetDocumentsAsync(
        string env, string database, string collection,
        string? search, int skip, int limit, CancellationToken ct = default);
    Task<MongoCollectionDetails> GetCollectionDetailsAsync(string env, string database, string collection, CancellationToken ct = default);
}

/// <summary>Destructive Mongo admin operations. Inject only where mutations are explicitly needed.</summary>
public interface IMongoAdmin
{
    Task DropCollectionAsync(string env, string database, string collection, CancellationToken ct = default);
}

/// <summary>Combined interface implemented by full Mongo service implementations.</summary>
public interface IMongoService : IMongoReader, IMongoAdmin { }
