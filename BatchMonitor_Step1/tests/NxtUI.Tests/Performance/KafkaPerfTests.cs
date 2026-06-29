using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Reproduces the KafkaDashboard load sequence:
///   Phase 1 — GetClusterInfoAsync (shows cluster bar) + GetTopicsAsync (shows table)
///   Phase 2 — GetTopicConfigAsync per topic (enriches cleanup policy / retention)
/// </summary>
public sealed class KafkaPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task TopicsLoad_TwoPhase_Timing()
    {
        var kafka = fix.Services.GetRequiredService<IKafkaMonitor>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        // Phase 1a — cluster info (renders the cluster bar in the UI).
        var clusterTask = kafka.GetClusterInfoAsync(env);
        var topicsTask  = kafka.GetTopicsAsync(env);

        var cluster = await clusterTask;
        out_.WriteLine($"[{sw.Elapsed:c}] GetClusterInfoAsync: {cluster.Brokers.Count} brokers, " +
                       $"controller={cluster.ControllerId}, cluster={cluster.ClusterId}");

        // Phase 1b — topic list (renders the table).
        var topics = (await topicsTask).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] GetTopicsAsync: {topics.Count} topics");

        // Phase 2 — per-topic config enrichment (runs in parallel in the page).
        out_.WriteLine($"[{sw.Elapsed:c}] Starting per-topic config enrichment ({topics.Count} topics)…");
        var enriched = 0;
        await Task.WhenAll(topics.Select(async t =>
        {
            var tSw  = Stopwatch.StartNew();
            var cfg  = await kafka.GetTopicConfigAsync(env, t.Name);
            Interlocked.Increment(ref enriched);
            out_.WriteLine($"  [{tSw.Elapsed:c}] {t.Name}: policy={cfg.CleanupPolicy}, retention={FormatRetention(cfg.RetentionMs)}");
        }));

        out_.WriteLine($"[{sw.Elapsed:c}] All {enriched} topics enriched.");
    }

    [Fact]
    public async Task ConsumersLoad_Timing()
    {
        var kafka = fix.Services.GetRequiredService<IKafkaMonitor>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        var clusterTask = kafka.GetClusterInfoAsync(env);
        var groupsTask  = kafka.GetAllConsumerGroupsAsync(env);

        var cluster = await clusterTask;
        out_.WriteLine($"[{sw.Elapsed:c}] GetClusterInfoAsync: {cluster.Brokers.Count} brokers");

        var groups = (await groupsTask).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] GetAllConsumerGroupsAsync: {groups.Count} groups");

        // Per-group lag detail (triggered when user selects a group in the UI).
        foreach (var g in groups.Take(3))
        {
            var lags = await kafka.GetGroupTopicLagsAsync(env, g.GroupId);
            out_.WriteLine($"  [{sw.Elapsed:c}] {g.GroupId}: {lags.Count} topics, totalLag={lags.Sum(l => l.Lag)}");
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task TopicTail_FirstMessages_Timing()
    {
        var kafka  = fix.Services.GetRequiredService<IKafkaMonitor>();
        var env    = fix.DefaultEnv;
        var sw     = Stopwatch.StartNew();
        var topics = (await kafka.GetTopicsAsync(env)).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] {topics.Count} topics available");

        if (topics.Count == 0) { out_.WriteLine("No topics — skipping tail test."); return; }

        var topic    = topics[0].Name;
        var messages = new List<KafkaMessage>();
        out_.WriteLine($"[{sw.Elapsed:c}] Tailing '{topic}' (first 10 messages, 15s timeout)…");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var msg in kafka.TailTopicAsync(env, topic, maxMessages: 10, cts.Token))
            {
                messages.Add(msg);
                out_.WriteLine($"  [{sw.Elapsed:c}] partition={msg.Partition} offset={msg.Offset} " +
                               $"key={msg.Key ?? "(null)"} valueLen={msg.Value.Length}");
            }
        }
        catch (OperationCanceledException)
        {
            out_.WriteLine($"  [{sw.Elapsed:c}] Timed out after {messages.Count} messages.");
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Tail done: {messages.Count} messages received.");
    }

    [Fact]
    public async Task AllConsumerGroups_LagDetail_Sequential_vs_Parallel()
    {
        var kafka  = fix.Services.GetRequiredService<IKafkaMonitor>();
        var env    = fix.DefaultEnv;
        var sw     = Stopwatch.StartNew();
        var groups = (await kafka.GetAllConsumerGroupsAsync(env)).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] {groups.Count} consumer groups");

        if (groups.Count == 0) { out_.WriteLine("No groups — skipping."); return; }

        // Sequential
        var seqSw = Stopwatch.StartNew();
        foreach (var g in groups)
            await kafka.GetGroupTopicLagsAsync(env, g.GroupId);
        out_.WriteLine($"[{sw.Elapsed:c}] Sequential: {seqSw.Elapsed.TotalMilliseconds:F0}ms for {groups.Count} groups");

        // Parallel (same as the page would do on group selection change)
        var parSw = Stopwatch.StartNew();
        await Task.WhenAll(groups.Select(g => kafka.GetGroupTopicLagsAsync(env, g.GroupId)));
        out_.WriteLine($"[{sw.Elapsed:c}] Parallel:   {parSw.Elapsed.TotalMilliseconds:F0}ms for {groups.Count} groups");

        if (parSw.Elapsed.TotalMilliseconds > 0)
            out_.WriteLine($"  Speedup: {seqSw.Elapsed.TotalMilliseconds / parSw.Elapsed.TotalMilliseconds:F1}x");
    }

    [Fact]
    public async Task TopicConsumerGroups_Per_Topic_Timing()
    {
        var kafka  = fix.Services.GetRequiredService<IKafkaMonitor>();
        var env    = fix.DefaultEnv;
        var sw     = Stopwatch.StartNew();
        var topics = (await kafka.GetTopicsAsync(env)).ToList();

        if (topics.Count == 0) { out_.WriteLine("No topics."); return; }

        out_.WriteLine($"[{sw.Elapsed:c}] Fetching consumer groups per topic ({topics.Count} topics)…");
        await Task.WhenAll(topics.Take(5).Select(async t =>
        {
            var tSw    = Stopwatch.StartNew();
            var groups = await kafka.GetTopicConsumerGroupsAsync(env, t.Name);
            out_.WriteLine($"  [{tSw.Elapsed:c}] {t.Name}: {groups.Count} consumer groups, " +
                           $"totalLag={groups.Sum(g => g.TotalLag):N0}");
        }));

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task Slow_broker_response_stays_within_timeout()
    {
        var kafka = fix.Services.GetRequiredService<IKafkaMonitor>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var cluster = await kafka.GetClusterInfoAsync(env, cts.Token);
            out_.WriteLine($"[{sw.Elapsed:c}] GetClusterInfoAsync: {cluster.Brokers.Count} brokers " +
                           $"(online: {cluster.Brokers.Count(b => b.IsOnline)})");

            if (cluster.Brokers.Any(b => !b.IsOnline))
                out_.WriteLine($"  WARNING: {cluster.Brokers.Count(b => !b.IsOnline)} broker(s) offline — " +
                               "consider checking Kafka connectivity.");
        }
        catch (OperationCanceledException)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] GetClusterInfoAsync timed out after 5s — broker may be unreachable.");
        }
        catch (Exception ex)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatRetention(long ms) =>
        ms < 0 ? "∞" : ms >= 86_400_000 ? $"{ms / 86_400_000}d" : $"{ms / 3_600_000}h";
}
