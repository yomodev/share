using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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

    private static string FormatRetention(long ms) =>
        ms < 0 ? "∞" : ms >= 86_400_000 ? $"{ms / 86_400_000}d" : $"{ms / 3_600_000}h";
}
