using BatchMonitor.Models;

namespace BatchMonitor.Services;

public interface IHeartbeatService
{
    Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default);
}
