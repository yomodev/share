using BatchMonitor.Models;
using BatchMonitor.Services;

namespace BatchMonitor.Core.Services;

public class MockHeartbeatService : IHeartbeatService
{
    private static readonly (string Service, string Host, int Pid)[] _instances =
    [
        ("Loader",      "srv-01", 1001),
        ("Loader",      "srv-02", 1002),
        ("Transformer", "srv-01", 2001),
        ("Transformer", "srv-03", 2002),
        ("Exporter",    "srv-02", 3001),
        ("Validator",   "srv-04", 4001),
        ("Notifier",    "srv-03", 5001),
        ("Scheduler",   "srv-01", 6001),
    ];

    // Simulate Notifier on srv-03 going stale after a while
    private static readonly DateTime _notifierLastSeen = DateTime.UtcNow.AddSeconds(-90);

    public Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default)
    {
        var now     = DateTime.UtcNow;
        var statuses = _instances.Select((inst, i) =>
        {
            var updated = (inst.Service == "Notifier" && inst.Host == "srv-03")
                ? _notifierLastSeen
                : now.AddSeconds(-(i % 3) * 10); // stagger slightly

            return new ServiceStatus
            {
                ServiceName     = inst.Service,
                HostName        = inst.Host,
                ProcessId       = inst.Pid,
                UpdatedDateTime = updated,
                CreatedDateTime = now.AddHours(-i - 1),
                IsOnline        = (now - updated).TotalSeconds <= 60,
            };
        }).ToList();

        return Task.FromResult(statuses);
    }
}
