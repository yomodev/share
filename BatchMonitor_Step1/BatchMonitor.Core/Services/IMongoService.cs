using BatchMonitor.Core.Models;

namespace BatchMonitor.Core.Services;

public interface IMongoService
{
    Task<IReadOnlyList<MongoDatabaseInfo>>     GetDatabasesAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<MongoCollectionSummary>> GetCollectionsAsync(string env, string database, CancellationToken ct = default);
    Task<(IReadOnlyList<MongoDocument> Documents, long TotalCount)> GetDocumentsAsync(
        string env, string database, string collection,
        string? search, int skip, int limit, CancellationToken ct = default);
    Task<MongoCollectionDetails> GetCollectionDetailsAsync(string env, string database, string collection, CancellationToken ct = default);
    Task DropCollectionAsync(string env, string database, string collection, CancellationToken ct = default);
}
