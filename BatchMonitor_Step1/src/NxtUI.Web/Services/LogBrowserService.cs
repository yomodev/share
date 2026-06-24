using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Services;

namespace NxtUI.Web.Services;

public class LogBrowserService : ILogBrowserService
{
    private readonly LogPathSettings _settings;

    public LogBrowserService(IOptions<LogPathSettings> settings)
    {
        _settings = settings.Value;
    }

    public string? ResolveRoot(string server)
    {
        if (string.IsNullOrWhiteSpace(_settings.LogsFolder)) return null;
        return _settings.LogsFolder.Replace("{server}", server, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetServers(IEnumerable<string> heartbeatHosts)
    {
        if (_settings.Servers.Count > 0) return _settings.Servers;
        return heartbeatHosts.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
    }

    public async Task<IReadOnlyList<LogFolderNode>> GetSubfoldersAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default)
    {
        var tasks = servers.Select(s => GetSubfoldersForServerAsync(s, relativePath, ct));
        var results = await Task.WhenAll(tasks);

        // union by name — keep first occurrence (any server having the folder is enough)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<LogFolderNode>();
        foreach (var list in results)
            foreach (var node in list)
                if (seen.Add(node.Name))
                    merged.Add(node);

        merged.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return merged;
    }

    public async Task<IReadOnlyList<LogFileEntry>> GetFilesAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default)
    {
        var tasks = servers.Select(s => GetFilesForServerAsync(s, relativePath, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(x => x)
                      .OrderBy(f => f.Server, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    public (string Server, string RelativePath)? ParseHintPath(string fullPath, IEnumerable<string> servers)
    {
        foreach (var server in servers)
        {
            var root = ResolveRoot(server);
            if (root is null) continue;
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = fullPath[root.Length..].TrimStart('\\', '/');
                return (server, rel);
            }
        }
        return null;
    }

    // ── private helpers ───────────────────────────────────────────────────

    private Task<List<LogFolderNode>> GetSubfoldersForServerAsync(
        string server, string relativePath, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var root = ResolveRoot(server);
            if (root is null) return new List<LogFolderNode>();

            var dir = string.IsNullOrEmpty(relativePath)
                ? root
                : Path.Combine(root, relativePath);

            if (!Directory.Exists(dir)) return new List<LogFolderNode>();

            try
            {
                return Directory.GetDirectories(dir)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(d =>
                    {
                        var name = Path.GetFileName(d);
                        var rel  = string.IsNullOrEmpty(relativePath) ? name : relativePath + "\\" + name;
                        var hasChildren = Directory.EnumerateDirectories(d).Any();
                        return new LogFolderNode(name, rel, hasChildren);
                    })
                    .ToList();
            }
            catch { return new List<LogFolderNode>(); }
        }, ct);
    }

    private Task<List<LogFileEntry>> GetFilesForServerAsync(
        string server, string relativePath, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var root = ResolveRoot(server);
            if (root is null) return new List<LogFileEntry>();

            var dir = string.IsNullOrEmpty(relativePath)
                ? root
                : Path.Combine(root, relativePath);

            if (!Directory.Exists(dir)) return new List<LogFileEntry>();

            try
            {
                return Directory.GetFiles(dir)
                    .Select(f =>
                    {
                        var info = new FileInfo(f);
                        return new LogFileEntry(
                            server,
                            info.Name,
                            info.Length,
                            info.CreationTimeUtc,
                            info.LastWriteTimeUtc,
                            f);
                    })
                    .ToList();
            }
            catch { return new List<LogFileEntry>(); }
        }, ct);
    }
}
