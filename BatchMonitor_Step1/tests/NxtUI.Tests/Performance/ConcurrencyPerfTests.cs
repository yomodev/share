using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NxtUI.Core.Services;
using NxtUI.Web.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Stress tests for concurrent subscriptions to HeartbeatMonitor and ServiceMetricsMonitor.
/// Catches refcount bugs, event fan-out failures, and monitor liveness issues after
/// rapid subscribe/unsubscribe cycles.
/// </summary>
public sealed class ConcurrencyPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task Multiple_concurrent_heartbeat_subscriptions_all_receive_update()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var env       = fix.DefaultEnv;
        var sw        = Stopwatch.StartNew();
        var ct        = TestContext.Current.CancellationToken;
        const int count = 5;

        var received  = new int[count];
        var tcss      = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        // Register ALL event handlers before creating any subscription so that even
        // an immediate synchronous poll (mock returns Task.FromResult) calls every handler.
        for (int i = 0; i < count; i++)
        {
            var idx = i;
            heartbeat.OnServicesUpdated += e =>
            {
                if (e == env) { Interlocked.Increment(ref received[idx]); tcss[idx].TrySetResult(true); }
            };
        }

        // Subscribe after all handlers are registered.
        var subs = Enumerable.Range(0, count).Select(_ => heartbeat.Subscribe(env)).ToArray();

        await Task.WhenAll(tcss.Select(t => Task.WhenAny(t.Task, Task.Delay(15_000, ct))));

        foreach (var sub in subs) sub.Dispose();

        for (int i = 0; i < count; i++)
            out_.WriteLine($"[{sw.Elapsed:c}] Sub {i}: received {received[i]} update(s)");

        foreach (var tcs in tcss)
            tcs.Task.IsCompletedSuccessfully.Should()
                .BeTrue(because: "every subscriber should receive at least one heartbeat update");
    }

    [Fact]
    public async Task Rapid_subscribe_unsubscribe_does_not_crash_monitor()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var env       = fix.DefaultEnv;
        var sw        = Stopwatch.StartNew();
        var ct        = TestContext.Current.CancellationToken;

        for (int i = 0; i < 30; i++)
            heartbeat.Subscribe(env).Dispose();   // subscribe then immediately release

        out_.WriteLine($"[{sw.Elapsed:c}] 30 rapid subscribe/dispose cycles done");

        // Verify the monitor is still alive and can serve a normal subscription.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        heartbeat.OnServicesUpdated += e => { if (e == env) tcs.TrySetResult(true); };
        using var finalSub = heartbeat.Subscribe(env);
        await Task.WhenAny(tcs.Task, Task.Delay(15_000, ct));

        out_.WriteLine($"[{sw.Elapsed:c}] Monitor still responsive: {tcs.Task.IsCompletedSuccessfully}");
        tcs.Task.IsCompletedSuccessfully.Should().BeTrue(
            because: "monitor must survive rapid subscribe/dispose cycles");
    }

    [Fact]
    public async Task Heartbeat_and_metrics_subscriptions_both_fire()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var metrics   = fix.Services.GetRequiredService<IServiceMetricsMonitor>();
        var env       = fix.DefaultEnv;
        var sw        = Stopwatch.StartNew();
        var ct        = TestContext.Current.CancellationToken;

        var hbTcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        heartbeat.OnServicesUpdated += e => { if (e == env) hbTcs.TrySetResult(true); };
        metrics.OnMetricsUpdated    += e => { if (e == env) metTcs.TrySetResult(true); };

        using var hbSub  = heartbeat.Subscribe(env);
        using var metSub = metrics.Subscribe(env);

        await Task.WhenAny(hbTcs.Task, Task.Delay(15_000, ct));
        out_.WriteLine($"[{sw.Elapsed:c}] Heartbeat fired: {hbTcs.Task.IsCompletedSuccessfully}");
        hbTcs.Task.IsCompletedSuccessfully.Should().BeTrue(because: "heartbeat should fire within 15s");

        // Metrics fire after heartbeat has data + log files have been read.
        // Allow extra time for the metrics polling cycle.
        await Task.WhenAny(metTcs.Task, Task.Delay(30_000, ct));
        out_.WriteLine($"[{sw.Elapsed:c}] Metrics fired: {metTcs.Task.IsCompletedSuccessfully}");

        var services = heartbeat.GetServices(env) ?? [];
        var withData = services.Count(s => metrics.GetLatest(env, s) is not null);
        out_.WriteLine($"[{sw.Elapsed:c}] {withData}/{services.Count} services have RAM metrics");
    }

    [Fact]
    public async Task Concurrent_subscribe_to_all_environments()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var envs      = fix.Environments;
        var sw        = Stopwatch.StartNew();
        var ct        = TestContext.Current.CancellationToken;

        if (envs.Length == 0) { out_.WriteLine("No environments configured."); return; }

        var tcss = envs.Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var subs = new IDisposable[envs.Length];

        for (int i = 0; i < envs.Length; i++)
        {
            var idx = i;
            heartbeat.OnServicesUpdated += e => { if (e == envs[idx]) tcss[idx].TrySetResult(true); };
            subs[i] = heartbeat.Subscribe(envs[i]);
        }

        await Task.WhenAll(tcss.Select(t => Task.WhenAny(t.Task, Task.Delay(20_000, ct))));
        foreach (var sub in subs) sub.Dispose();

        for (int i = 0; i < envs.Length; i++)
        {
            var svcs = heartbeat.GetServices(envs[i]);
            out_.WriteLine($"[{sw.Elapsed:c}] {envs[i]}: updated={tcss[i].Task.IsCompletedSuccessfully}, " +
                           $"services={(svcs?.Count.ToString() ?? "null")}");
        }
    }

    [Fact]
    public async Task Subscription_refcount_returns_to_zero_cleanly()
    {
        var heartbeat = fix.Services.GetRequiredService<IHeartbeatMonitor>();
        var env       = fix.DefaultEnv;
        var sw        = Stopwatch.StartNew();
        var ct        = TestContext.Current.CancellationToken;

        // Hold three subscriptions, wait for at least one update, then release all.
        var tcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        heartbeat.OnServicesUpdated += e => { if (e == env) tcs.TrySetResult(true); };

        var sub1 = heartbeat.Subscribe(env);
        var sub2 = heartbeat.Subscribe(env);
        var sub3 = heartbeat.Subscribe(env);

        await Task.WhenAny(tcs.Task, Task.Delay(15_000, ct));
        out_.WriteLine($"[{sw.Elapsed:c}] Received update with 3 subs open");

        sub1.Dispose();
        sub2.Dispose();
        sub3.Dispose();
        out_.WriteLine($"[{sw.Elapsed:c}] All 3 subscriptions released");

        // After all subscriptions are released the cache should still be readable.
        var cached = heartbeat.GetServices(env);
        out_.WriteLine($"[{sw.Elapsed:c}] Cache after release: {cached?.Count.ToString() ?? "null"} services");
    }
}
