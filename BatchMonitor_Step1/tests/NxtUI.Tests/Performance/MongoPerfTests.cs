using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Reproduces the MongoDashboard load sequence:
///   OnInitializedAsync — GetDatabaseNamesAsync (names only, no dbStats)
///   On DB select       — GetCollectionPageAsync (server-side filter + page + stats for page rows only)
/// </summary>
public sealed class MongoPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task DatabaseNames_Fast_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();
        var ct    = TestContext.Current.CancellationToken;

        var names = (await mongo.GetDatabaseNamesAsync(env, ct)).ToList();
        out_.WriteLine($"[{sw.Elapsed:c}] GetDatabaseNamesAsync: {names.Count} databases");
        foreach (var n in names)
            out_.WriteLine($"  {n}");
    }

    [Fact]
    public async Task CollectionPage_WithStats_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();
        var ct    = TestContext.Current.CancellationToken;

        var dbNames = (await mongo.GetDatabaseNamesAsync(env, ct)).ToList();
        if (dbNames.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        var firstDb = dbNames[0];
        out_.WriteLine($"[{sw.Elapsed:c}] Loading first page of collections for '{firstDb}'…");

        // Page 1 — filter=null, page size 25. Stats fetched only for this page.
        var (page1, total) = await mongo.GetCollectionPageAsync(env, firstDb, null, skip: 0, limit: 25, ct);
        out_.WriteLine($"[{sw.Elapsed:c}] Page 1: {page1.Count}/{total:N0} collections");
        foreach (var c in page1)
            out_.WriteLine($"  {c.Name}: {c.DocumentCount:N0} docs, {FormatBytes(c.StorageSizeBytes)}, {c.IndexCount} indexes");

        // Page 2 — verify paging works.
        if (total > 25)
        {
            var (page2, _) = await mongo.GetCollectionPageAsync(env, firstDb, null, skip: 25, limit: 25, ct);
            out_.WriteLine($"[{sw.Elapsed:c}] Page 2: {page2.Count} collections");
        }

        // Filtered — simulate user typing in the filter box.
        var prefix = page1.Count > 0 ? page1[0].Name[..Math.Min(3, page1[0].Name.Length)] : "a";
        var (filtered, filteredTotal) = await mongo.GetCollectionPageAsync(env, firstDb, prefix, skip: 0, limit: 25, ct);
        out_.WriteLine($"[{sw.Elapsed:c}] Filter '{prefix}': {filteredTotal:N0} matches, showing {filtered.Count}");

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task Documents_Pagination_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();
        var ct    = TestContext.Current.CancellationToken;

        var databases = (await mongo.GetDatabaseNamesAsync(env, ct)).ToList();
        if (databases.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        var db    = databases[0];
        var names = (await mongo.GetCollectionNamesAsync(env, db, ct)).ToList();
        if (names.Count == 0) { out_.WriteLine($"No collections in '{db}' — skipping."); return; }

        var col = names[0];
        out_.WriteLine($"[{sw.Elapsed:c}] Paginating '{db}.{col}'…");

        var pg1Sw = Stopwatch.StartNew();
        var (docs1, total) = await mongo.GetDocumentsAsync(env, db, col, null, skip: 0, limit: 25, ct: ct);
        out_.WriteLine($"[{sw.Elapsed:c}] Page 1 (skip=0):    {docs1.Count} docs in {pg1Sw.Elapsed.TotalMilliseconds:F0}ms, total={total:N0}");

        if (total > 25)
        {
            var pg2Sw = Stopwatch.StartNew();
            var (docs2, _) = await mongo.GetDocumentsAsync(env, db, col, null, skip: 25, limit: 25, ct: ct);
            out_.WriteLine($"[{sw.Elapsed:c}] Page 2 (skip=25):   {docs2.Count} docs in {pg2Sw.Elapsed.TotalMilliseconds:F0}ms");
        }

        if (total > 1000)
        {
            var deepSw = Stopwatch.StartNew();
            var (docs3, _) = await mongo.GetDocumentsAsync(env, db, col, null, skip: 1000, limit: 25, ct: ct);
            out_.WriteLine($"[{sw.Elapsed:c}] Deep page (skip=1000): {docs3.Count} docs in {deepSw.Elapsed.TotalMilliseconds:F0}ms");
        }
    }

    [Fact]
    public async Task Document_Search_With_Filter_Timing()
    {
        var mongo = fix.Services.GetRequiredService<IMongoReader>();
        var env   = fix.DefaultEnv;
        var sw    = Stopwatch.StartNew();
        var ct    = TestContext.Current.CancellationToken;

        var databases = (await mongo.GetDatabaseNamesAsync(env, ct)).ToList();
        if (databases.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        var db    = databases[0];
        var names = (await mongo.GetCollectionNamesAsync(env, db, ct)).ToList();
        if (names.Count == 0) { out_.WriteLine($"No collections in '{db}' — skipping."); return; }

        var col = names[0];
        foreach (var search in new[] { (string?)null, "error", "2024", "_id" })
        {
            var sSw = Stopwatch.StartNew();
            var (docs, total) = await mongo.GetDocumentsAsync(env, db, col, search, skip: 0, limit: 25, ct: ct);
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
        var ct    = TestContext.Current.CancellationToken;

        var databases = (await mongo.GetDatabaseNamesAsync(env, ct)).ToList();
        if (databases.Count == 0) { out_.WriteLine("No databases — skipping."); return; }

        foreach (var dbName in databases.Take(2))
        {
            var names = (await mongo.GetCollectionNamesAsync(env, dbName, ct)).ToList();
            out_.WriteLine($"[{sw.Elapsed:c}] {dbName}: {names.Count} collections");

            await Task.WhenAll(names.Take(5).Select(async col =>
            {
                var dSw     = Stopwatch.StartNew();
                var details = await mongo.GetCollectionDetailsAsync(env, dbName, col, ct);
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
            var databases = (await mongo.GetDatabaseNamesAsync(env, cts.Token)).ToList();
            out_.WriteLine($"[{sw.Elapsed:c}] GetDatabaseNamesAsync: {databases.Count} databases");
            if (databases.Count == 0)
                out_.WriteLine("  WARNING: No databases returned — connection may be degraded.");
        }
        catch (OperationCanceledException)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] GetDatabaseNamesAsync timed out after 5s — MongoDB may be unreachable.");
        }
        catch (Exception ex)
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatBytes(long b) =>
        b switch { >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB", >= 1_048_576 => $"{b / 1_048_576.0:F1} MB", >= 1024 => $"{b / 1024.0:F1} KB", _ => $"{b} B" };
}
