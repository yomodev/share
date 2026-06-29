using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Reproduces the MongoDashboard load sequence:
///   OnInitializedAsync — GetDatabasesAsync
///   Phase 1            — GetCollectionNamesAsync (fast — renders table immediately)
///   Phase 2            — GetCollectionStatsAsync per collection (enriches docs/size/indexes)
/// </summary>
public sealed class MongoPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task DatabasesAndCollections_TwoPhase_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        // OnInitializedAsync — list databases.
        var databases = (await mongo.GetDatabasesAsync(env)).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] GetDatabasesAsync: {databases.Count} databases");
        foreach (var db in databases)
            out_.WriteLine($"  {db.Name}: {db.CollectionCount} collections, {FormatBytes(db.SizeBytes)}");

        if (databases.Count == 0) { out_.WriteLine("No databases — skipping collection tests."); return; }

        var firstDb = databases[0].Name;
        out_.WriteLine($"[{sw.Elapsed:c}] Loading collections for '{firstDb}'…");

        // Phase 1 — collection names (fast, renders table rows immediately).
        var names = (await mongo.GetCollectionNamesAsync(env, firstDb)).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] GetCollectionNamesAsync: {names.Count} collections");

        // Phase 2 — per-collection stats enrichment (parallel, same pattern as the page).
        out_.WriteLine($"[{sw.Elapsed:c}] Enriching {names.Count} collections in parallel…");
        var enriched = 0;
        await Task.WhenAll(names.Select(async name =>
        {
            var tSw  = Stopwatch.StartNew();
            var stats = await mongo.GetCollectionStatsAsync(env, firstDb, name);
            Interlocked.Increment(ref enriched);
            if (stats is not null)
                out_.WriteLine($"  [{tSw.Elapsed:c}] {name}: {stats.DocumentCount:N0} docs, {FormatBytes(stats.StorageSizeBytes)}, {stats.IndexCount} indexes");
            else
                out_.WriteLine($"  [{tSw.Elapsed:c}] {name}: stats unavailable");
        }));

        out_.WriteLine($"[{sw.Elapsed:c}] All {enriched} collections enriched.");
    }

    [Fact]
    public async Task AllDatabases_CollectionStats_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        var databases = (await mongo.GetDatabasesAsync(env)).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] {databases.Count} databases");

        foreach (var db in databases)
        {
            var names = (await mongo.GetCollectionNamesAsync(env, db.Name)).ToList();
            out_.WriteLine($"[{sw.Elapsed:c}] {db.Name}: {names.Count} collections");

            var enrichTask = Task.WhenAll(names.Select(n => mongo.GetCollectionStatsAsync(env, db.Name, n)));
            await enrichTask;
            out_.WriteLine($"[{sw.Elapsed:c}] {db.Name} enrichment done");
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Total time for all databases.");
    }

    private static string FormatBytes(long b) =>
        b switch { >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB", >= 1_048_576 => $"{b / 1_048_576.0:F1} MB", >= 1024 => $"{b / 1024.0:F1} KB", _ => $"{b} B" };
}
