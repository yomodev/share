using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NxtUI.Core.Services;
using NxtUI.Web.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Reproduces the home treemap data flow:
///   1. HeartbeatMonitor.Subscribe(env) → triggers immediate poll of IHeartbeatService
///   2. Wait for cache to fill (OnServicesUpdated fires)
///   3. ServiceMetricsMonitor data (RAM per service) read from log files
///   4. Assemble the same payload the /api/treemap/{env} endpoint returns
///
/// "Taking ages" is most commonly caused by:
///   - IHeartbeatService (MongoDB) taking long on cold start
///   - Log file UNC paths unreachable → connection timeouts in ServiceMetricsMonitor
///   - 10-second JS poll interval after the first empty response
/// </summary>
public sealed class HomeTreemapPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task HeartbeatCacheFill_Timing()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var env       = fix.DefaultEnv;
        var sw        = Stopwatch.StartNew();

        out_.WriteLine($"[{sw.Elapsed:c}] Subscribing to env '{env}'…");

        // The subscription triggers an immediate poll (same as HomeMemoryTreemap.razor
        // calling MetricsMonitor.Subscribe → which subscribes HeartbeatMonitor).
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((HeartbeatMonitor)heartbeat).OnServicesUpdated += e =>
        {
            if (e == env) tcs.TrySetResult(e);
        };

        using var sub = heartbeat.Subscribe(env);
        out_.WriteLine($"[{sw.Elapsed:c}] Subscribe() returned — waiting for first OnServicesUpdated…");

        var notified = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        out_.WriteLine($"[{sw.Elapsed:c}] OnServicesUpdated fired: {notified == tcs.Task}");

        // Read the cache exactly as the API endpoint does.
        var services = heartbeat.GetServices(env);
        out_.WriteLine($"[{sw.Elapsed:c}] GetServices → {services?.Count ?? 0} services");

        // Assemble the treemap payload (mirrors Program.cs MapGet logic).
        if (services is not null)
        {
            var metrics = fix.Services.GetRequiredService<IServiceMetricsMonitor>();
            var cutoff  = DateTime.UtcNow.AddMinutes(-10);
            var hosts   = services
                .Where(s => s.IsOnline && s.UpdatedDateTime >= cutoff)
                .GroupBy(s => s.HostName)
                .Select(g => new
                {
                    host     = g.Key,
                    services = g.Select(s =>
                    {
                        var m = metrics.GetLatest(env, s);
                        return new { s.ServiceName, ramMb = m is null ? (double?)null : (m.CurrentUsageBytes + m.ChildUsageBytes) / 1_048_576.0 };
                    }).ToArray()
                }).ToArray();

            out_.WriteLine($"[{sw.Elapsed:c}] Treemap payload: {hosts.Length} hosts");
            foreach (var h in hosts)
            {
                var withRam = h.services.Count(s => s.ramMb.HasValue);
                out_.WriteLine($"  {h.host}: {h.services.Length} services, {withRam} with RAM data");
            }
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task MetricsAvailability_AfterHeartbeatFills()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var metrics   = fix.Services.GetRequiredService<IServiceMetricsMonitor>();
        var env       = fix.DefaultEnv;
        var sw        = Stopwatch.StartNew();

        // Ensure heartbeat is filled.
        var hbDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((HeartbeatMonitor)heartbeat).OnServicesUpdated += e => { if (e == env) hbDone.TrySetResult(true); };
        using var hbSub = heartbeat.Subscribe(env);
        await Task.WhenAny(hbDone.Task, Task.Delay(30_000));
        out_.WriteLine($"[{sw.Elapsed:c}] Heartbeat ready — {heartbeat.GetServices(env)?.Count ?? 0} services");

        // Subscribe to metrics (same as the ServicesPage + HomeMemoryTreemap do).
        // MetricsMonitor will start reading log files in background.
        var metricsFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        metrics.OnMetricsUpdated += e => { if (e == env) metricsFired.TrySetResult(true); };
        using var mSub = metrics.Subscribe(env);
        out_.WriteLine($"[{sw.Elapsed:c}] MetricsMonitor subscribed — waiting for first OnMetricsUpdated…");

        var got = await Task.WhenAny(metricsFired.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        out_.WriteLine($"[{sw.Elapsed:c}] Metrics fired: {got == metricsFired.Task}");

        // Sample how many services have RAM data.
        var services = heartbeat.GetServices(env) ?? [];
        var withRam  = services.Count(s => metrics.GetLatest(env, s) is not null);
        out_.WriteLine($"[{sw.Elapsed:c}] {withRam}/{services.Count} services have RAM data.");
    }
}
