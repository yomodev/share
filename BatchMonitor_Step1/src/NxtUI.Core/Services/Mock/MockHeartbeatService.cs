using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Mock;

public class MockHeartbeatService : IHeartbeatService
{
    // Hosts match App:Environments[].Servers in appsettings.json (DEV1 env)
    private static readonly (string Service, string Host, int Pid)[] _instances =
    [
        ("Loader",      "dev1-srv-01", 1001),
        ("Loader",      "dev1-srv-02", 1002),
        ("Transformer", "dev1-srv-01", 2001),
        ("Transformer", "dev1-srv-02", 2002),
        ("Exporter",    "dev1-srv-02", 3001),
        ("Validator",   "dev1-srv-01", 4001),
        ("Notifier",    "dev1-srv-02", 5001),
        ("Scheduler",   "dev1-srv-01", 6001),
    ];

    // Simulate Notifier on dev1-srv-02 going stale after a while
    private static readonly DateTime _notifierLastSeen = DateTime.UtcNow.AddSeconds(-90);

    public Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default)
    {
        var now     = DateTime.UtcNow;
        var statuses = _instances.Select((inst, i) =>
        {
            var updated = (inst.Service == "Notifier" && inst.Host == "dev1-srv-02")
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
