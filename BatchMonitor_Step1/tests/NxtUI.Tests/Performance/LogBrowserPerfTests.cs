using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NxtUI.Core.Configuration;
using NxtUI.Core.Services;
using System.Diagnostics;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Reproduces the LogBrowser load sequence:
///   OnInitializedAsync — GetSubfoldersAsync(servers, "") — root-level folder enumeration
///   User expands node  — GetSubfoldersAsync(servers, path)
///   User searches      — SearchAsync (recursive, streaming)
///
/// Bottlenecks are typically UNC path access latency and the number of servers/folders
/// that must be enumerated in parallel.
/// </summary>
public sealed class LogBrowserPerfTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task RootFolderEnumeration_Timing()
    {
        var browser = fix.Services.GetRequiredService<ILogBrowserService>();
        var logPaths = fix.Services.GetRequiredService<IOptions<FileBrowserSettings>>().Value;
        var sw = Stopwatch.StartNew();

        var servers = logPaths.Servers;
        out_.WriteLine($"[{sw.Elapsed:c}] Servers configured: {servers.Count}");
        foreach (var s in servers)
            out_.WriteLine($"  {s} → root={browser.ResolveRoot(s) ?? "(no root)"}");

        if (servers.Count == 0)
        {
            out_.WriteLine("No servers configured in Logs:Servers — add server names to appsettings.json to test log browser.");
            return;
        }

        // Same call as LogBrowser OnInitializedAsync.
        out_.WriteLine($"[{sw.Elapsed:c}] GetSubfoldersAsync(\"\") — enumerating root folders across all servers…");
        var roots = await browser.GetSubfoldersAsync(servers, "", CancellationToken.None);
        out_.WriteLine($"[{sw.Elapsed:c}] Root folders: {roots.Count}");
        foreach (var r in roots.Take(20))
            out_.WriteLine($"  {r.RelativePath} [{r.Name}] (hasChildren={r.HasChildren})");

        // Expand the first few nodes (simulates user clicking expand in the tree).
        foreach (var node in roots.Where(r => r.HasChildren).Take(3))
        {
            out_.WriteLine($"[{sw.Elapsed:c}] Expanding '{node.RelativePath}'…");
            var children = await browser.GetSubfoldersAsync(servers, node.RelativePath, CancellationToken.None);
            out_.WriteLine($"[{sw.Elapsed:c}]   {children.Count} children");
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Done.");
    }

    [Fact]
    public async Task FileSearch_Timing()
    {
        var browser = fix.Services.GetRequiredService<ILogBrowserService>();
        var logPaths = fix.Services.GetRequiredService<IOptions<FileBrowserSettings>>().Value;
        var sw = Stopwatch.StartNew();

        var servers = logPaths.Servers;
        if (servers.Count == 0)
        {
            out_.WriteLine("No servers configured — skipping file search test.");
            return;
        }

        // Simulates the user searching for *.log files with no content filter.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var fileCount = 0;
        var totalBytes = 0L;

        out_.WriteLine($"[{sw.Elapsed:c}] SearchAsync *.log (no content filter, root path)…");

        await foreach (var entry in browser.SearchAsync(servers, "", "*.log", null, cts.Token))
        {
            fileCount++;
            totalBytes += entry.SizeBytes;

            // Print first 10 then every 100th.
            if (fileCount <= 10 || fileCount % 100 == 0)
                out_.WriteLine($"  [{sw.Elapsed:c}] #{fileCount} {entry.Server}/{entry.FileName} ({FormatBytes(entry.SizeBytes)})");
        }

        out_.WriteLine($"[{sw.Elapsed:c}] Search complete: {fileCount} files, {FormatBytes(totalBytes)} total.");
    }

    private static string FormatBytes(long b) =>
        b switch { >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB", >= 1_048_576 => $"{b / 1_048_576.0:F1} MB", >= 1024 => $"{b / 1024.0:F1} KB", _ => $"{b} B" };
}
