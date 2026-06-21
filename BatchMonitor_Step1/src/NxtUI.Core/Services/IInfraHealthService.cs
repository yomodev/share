using BatchMonitor.Core.Models;

namespace BatchMonitor.Core.Services;

public interface IInfraHealthService
{
    Task<KafkaHealth> CheckKafkaAsync(string env, CancellationToken ct = default);
    Task<MongoHealth> CheckMongoAsync(string env, CancellationToken ct = default);
}
