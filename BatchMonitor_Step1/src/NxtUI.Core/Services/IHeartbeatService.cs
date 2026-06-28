using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

public interface IHeartbeatService
{
    Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default);
}
