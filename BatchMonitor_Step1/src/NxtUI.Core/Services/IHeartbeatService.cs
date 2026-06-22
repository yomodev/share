using NxtUI.Models;

namespace NxtUI.Services;

public interface IHeartbeatService
{
    Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default);
}
