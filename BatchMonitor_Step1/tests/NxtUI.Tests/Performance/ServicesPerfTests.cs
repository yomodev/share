using Microsoft.Extensions.DependencyInjection;
using NxtUI.Core.Services;
using NxtUI.Web.Services;
using System.Diagnostics;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Reproduces the ServicesPage load sequence:
///   OnInitializedAsync — HeartbeatMonitor.GetServices (cache hit or wait)
///   Background         — ServiceMetricsMonitor reads log files → RAM data fills in
///
/// The page shows immediately from cache. The bottleneck when cache is cold is the
/// time for IHeartbeatService.GetServiceStatusesAsync (MongoDB heartbeats query).
/// The RAM columns fill in as ServiceMetricsMonitor reads log files from UNC paths.
/// </summary>
public sealed class ServicesPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task ServicesPage_InitialLoad_Timing()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var metrics = fix.Services.GetRequiredService<IServiceMetricsMonitor>();
        var env = fix.DefaultEnv;
        var sw = Stopwatch.StartNew();
        var ct = TestContext.Current.CancellationToken;

        // Check if cache is already warm (page shows immediately in this case).
        var cached = heartbeat.GetServices(env);
        if (cached is not null)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Cache already warm: {cached.Count} services — page shows immediately.");
        }
        else
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Cache cold — subscribing (triggers immediate poll)…");
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ((HeartbeatMonitor)heartbeat).OnServicesUpdated += e => { if (e == env) tcs.TrySetResult(true); };

            using var sub = heartbeat.Subscribe(env);
            await Task.WhenAny(tcs.Task, Task.Delay(30_000, ct));
            out_.WriteLine($"[{sw.Elapsed:c}] First heartbeat poll done: {heartbeat.GetServices(env)?.Count ?? 0} services");
        }

        // Log the service list (same as the page's LoadFromCache does).
        var services = heartbeat.GetServices(env) ?? [];
        out_.WriteLine($"[{sw.Elapsed:c}] Total services: {services.Count}");

        var online = services.Count(s => s.IsOnline);
        var offline = services.Count - online;
        out_.WriteLine($"  Online: {online}, Offline: {offline}");

        // Group by host (useful for diagnosing per-server heartbeat delays).
        foreach (var g in services.GroupBy(s => s.HostName).OrderBy(g => g.Key))
            out_.WriteLine($"  Host {g.Key}: {g.Count()} services");

        // Now check metrics availability.
        out_.WriteLine($"[{sw.Elapsed:c}] Checking metrics (RAM data) availability…");
        using var metSub = metrics.Subscribe(env);
        var withRam = services.Count(s => metrics.GetLatest(env, s) is not null);
        out_.WriteLine($"[{sw.Elapsed:c}] Immediate RAM data: {withRam}/{services.Count} services");

        // Wait a moment to see if more metrics arrive quickly.
        if (withRam < services.Count && services.Count > 0)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Waiting up to 10s for metrics to fill in…");
            var metricsFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            metrics.OnMetricsUpdated += e => { if (e == env) metricsFired.TrySetResult(true); };
            await Task.WhenAny(metricsFired.Task, Task.Delay(10_000, ct));
            withRam = services.Count(s => metrics.GetLatest(env, s) is not null);
            out_.WriteLine($"[{sw.Elapsed:c}] RAM data after wait: {withRam}/{services.Count} services");
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task HeartbeatService_DirectCall_Timing()
    {
        // Bypass HeartbeatMonitor and call IHeartbeatService directly.
        // This isolates the raw MongoDB / network cost of the heartbeat query.
        var heartbeatSvc = fix.Services.GetRequiredService<IHeartbeatService>();
        var env = fix.DefaultEnv;
        var sw = Stopwatch.StartNew();
        var ct = TestContext.Current.CancellationToken;

        out_.WriteLine($"[{sw.Elapsed:c}] Calling IHeartbeatService.GetServiceStatusesAsync('{env}')…");
        var services = await heartbeatSvc.GetServiceStatusesAsync(env, ct: ct);
        out_.WriteLine($"[{sw.Elapsed:c}] Result: {services.Count} services");

        foreach (var g in services.GroupBy(s => s.HostName).OrderBy(g => g.Key))
        {
            var online = g.Count(s => s.IsOnline);
            out_.WriteLine($"  {g.Key}: {g.Count()} total, {online} online");
        }
    }
}
