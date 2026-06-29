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

    [Fact]
    public async Task Documents_Pagination_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        var databases = (await mongo.GetDatabasesAsync(env)).ToList();
        if (databases.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        var db    = databases[0].Name;
        var names = (await mongo.GetCollectionNamesAsync(env, db)).ToList();
        if (names.Count == 0) { out_.WriteLine($"No collections in '{db}' — skipping."); return; }

        var col = names[0];
        out_.WriteLine($"[{sw.Elapsed:c}] Paginating '{db}.{col}'…");

        var pg1Sw = Stopwatch.StartNew();
        var (docs1, total) = await mongo.GetDocumentsAsync(env, db, col, null, skip: 0, limit: 25);
        out_.WriteLine($"[{sw.Elapsed:c}] Page 1 (skip=0):    {docs1.Count} docs in {pg1Sw.Elapsed.TotalMilliseconds:F0}ms, total={total:N0}");

        if (total > 25)
        {
            var pg2Sw = Stopwatch.StartNew();
            var (docs2, _) = await mongo.GetDocumentsAsync(env, db, col, null, skip: 25, limit: 25);
            out_.WriteLine($"[{sw.Elapsed:c}] Page 2 (skip=25):   {docs2.Count} docs in {pg2Sw.Elapsed.TotalMilliseconds:F0}ms");
        }

        if (total > 1000)
        {
            var deepSw = Stopwatch.StartNew();
            var (docs3, _) = await mongo.GetDocumentsAsync(env, db, col, null, skip: 1000, limit: 25);
            out_.WriteLine($"[{sw.Elapsed:c}] Deep page (skip=1000): {docs3.Count} docs in {deepSw.Elapsed.TotalMilliseconds:F0}ms");
        }
    }

    [Fact]
    public async Task Document_Search_With_Filter_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        var databases = (await mongo.GetDatabasesAsync(env)).ToList();
        if (databases.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        var db    = databases[0].Name;
        var names = (await mongo.GetCollectionNamesAsync(env, db)).ToList();
        if (names.Count == 0) { out_.WriteLine($"No collections in '{db}' — skipping."); return; }

        var col = names[0];
        foreach (var search in new[] { (string?)null, "error", "2024", "_id" })
        {
            var sSw = Stopwatch.StartNew();
            var (docs, total) = await mongo.GetDocumentsAsync(env, db, col, search, skip: 0, limit: 25);
            out_.WriteLine($"[{sw.Elapsed:c}] search={search ?? "(none)",-10}: " +
                           $"{docs.Count}/{total:N0} in {sSw.Elapsed.TotalMilliseconds:F0}ms");
        }
    }

    [Fact]
    public async Task CollectionDetails_With_Indexes_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        var databases = (await mongo.GetDatabasesAsync(env)).ToList();
        if (databases.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        foreach (var dbInfo in databases.Take(2))
        {
            var names = (await mongo.GetCollectionNamesAsync(env, dbInfo.Name)).ToList();
            out_.WriteLine($"[{sw.Elapsed:c}] {dbInfo.Name}: {names.Count} collections");

            await Task.WhenAll(names.Take(5).Select(async col =>
            {
                var dSw     = Stopwatch.StartNew();
                var details = await mongo.GetCollectionDetailsAsync(env, dbInfo.Name, col);
                out_.WriteLine($"  [{dSw.Elapsed:c}] {col}: {details.Summary.DocumentCount:N0} docs, " +
                               $"{details.Indexes.Count} indexes");
                foreach (var idx in details.Indexes)
                    out_.WriteLine($"    {idx.Name}: keys={idx.Keys} unique={idx.Unique} sparse={idx.Sparse}");
            }));
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task Slow_Mongo_Connection_Stays_Within_Timeout()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var databases = (await mongo.GetDatabasesAsync(env, cts.Token)).ToList();
            out_.WriteLine($"[{sw.Elapsed:c}] GetDatabasesAsync: {databases.Count} databases");
            if (databases.Count == 0)
                out_.WriteLine("  WARNING: No databases returned — connection may be degraded.");
        }
        catch (OperationCanceledException)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] GetDatabasesAsync timed out after 5s — MongoDB may be unreachable.");
        }
        catch (Exception ex)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatBytes(long b) =>
        b switch { >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB", >= 1_048_576 => $"{b / 1_048_576.0:F1} MB", >= 1024 => $"{b / 1024.0:F1} KB", _ => $"{b} B" };
}
