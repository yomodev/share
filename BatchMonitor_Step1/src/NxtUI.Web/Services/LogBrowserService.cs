using NxtUI.Core.Services;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Filtering;
using NxtUI.Services;

namespace NxtUI.Web.Services;

public class LogBrowserService : ILogBrowserService
{
    private readonly LogPathSettings _settings;

    // Detects the start of a new log entry: line begins with a timestamp.
    private static readonly Regex TimestampPrefix =
        new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

    // Internal record for content filtering — property names match FilterParser aliases.
    private sealed record LogLine(
        DateTime Timestamp,
        string   Level,
        string   Machine,
        int      Pid,
        int      ThreadId,
        string   Message,
        string?  Caller);

    public LogBrowserService(IOptions<LogPathSettings> settings)
    {
        _settings = settings.Value;
    }

    // ── ILogBrowserService ────────────────────────────────────────────────

    public string? ResolveRoot(string server)
    {
        if (string.IsNullOrWhiteSpace(_settings.LogsFolder)) return null;
        return _settings.LogsFolder.Replace("{server}", server, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<LogFolderNode>> GetSubfoldersAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default)
    {
        var tasks   = servers.Select(s => GetSubfoldersForServerAsync(s, relativePath, ct));
        var results = await Task.WhenAll(tasks);

        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        var tasks   = servers.Select(s => GetFilesForServerAsync(s, relativePath, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(x => x)
                      .OrderBy(f => f.Server,  StringComparer.OrdinalIgnoreCase)
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

    public async IAsyncEnumerable<LogFileEntry> SearchAsync(
        IEnumerable<string> servers,
        string rootRelPath,
        string fileGlob,
        FilterNode? contentFilter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var serverList = servers.ToList();
        var channel    = Channel.CreateUnbounded<LogFileEntry>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

        var producers = serverList.Select(server => Task.Run(async () =>
        {
            await foreach (var entry in SearchServerAsync(server, rootRelPath, fileGlob, contentFilter, ct))
                await channel.Writer.WriteAsync(entry, ct);
        }, ct)).ToArray();

        _ = Task.WhenAll(producers).ContinueWith(
            _ => channel.Writer.TryComplete(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        await foreach (var entry in channel.Reader.ReadAllAsync(ct))
            yield return entry;
    }

    // ── private: folder / file helpers ───────────────────────────────────

    private Task<List<LogFolderNode>> GetSubfoldersForServerAsync(
        string server, string relativePath, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var root = ResolveRoot(server);
            if (root is null) return new List<LogFolderNode>();

            var dir = string.IsNullOrEmpty(relativePath) ? root : Path.Combine(root, relativePath);
            if (!Directory.Exists(dir)) return new List<LogFolderNode>();

            try
            {
                return Directory.GetDirectories(dir)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(d =>
                    {
                        var name        = Path.GetFileName(d);
                        var rel         = string.IsNullOrEmpty(relativePath) ? name : relativePath + "\\" + name;
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

            var dir = string.IsNullOrEmpty(relativePath) ? root : Path.Combine(root, relativePath);
            if (!Directory.Exists(dir)) return new List<LogFileEntry>();

            try
            {
                return Directory.GetFiles(dir)
                    .Select(f =>
                    {
                        var info = new FileInfo(f);
                        return new LogFileEntry(server, info.Name, info.Length,
                            info.CreationTimeUtc, info.LastWriteTimeUtc, f);
                    })
                    .ToList();
            }
            catch { return new List<LogFileEntry>(); }
        }, ct);
    }

    // ── private: recursive search ─────────────────────────────────────────

    private async IAsyncEnumerable<LogFileEntry> SearchServerAsync(
        string server, string rootRelPath, string fileGlob, FilterNode? contentFilter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var root = ResolveRoot(server);
        if (root is null) yield break;

        var searchRoot = string.IsNullOrEmpty(rootRelPath)
            ? root
            : Path.Combine(root, rootRelPath);

        if (!Directory.Exists(searchRoot)) yield break;

        await foreach (var entry in WalkDirectoryAsync(server, searchRoot, fileGlob, contentFilter, ct))
            yield return entry;
    }

    private async IAsyncEnumerable<LogFileEntry> WalkDirectoryAsync(
        string server, string dir, string fileGlob, FilterNode? contentFilter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string[] files;
        try   { files = Directory.GetFiles(dir, fileGlob); }
        catch { files = []; }

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            var passes = contentFilter is null
                || await Task.Run(() => FileMatchesContent(f, contentFilter, ct), ct);

            if (passes)
            {
                var info = new FileInfo(f);
                yield return new LogFileEntry(
                    server, info.Name, info.Length,
                    info.CreationTimeUtc, info.LastWriteTimeUtc, f);
            }
        }

        string[] subdirs;
        try   { subdirs = Directory.GetDirectories(dir); }
        catch { subdirs = []; }

        foreach (var sub in subdirs)
        {
            await foreach (var entry in WalkDirectoryAsync(server, sub, fileGlob, contentFilter, ct))
                yield return entry;
        }
    }

    // ── private: content filtering ────────────────────────────────────────

    private static bool FileMatchesContent(string filePath, FilterNode filter, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 65536);

            string? firstLine    = null;
            var     continuation = new StringBuilder();

            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();

                if (TimestampPrefix.IsMatch(raw))
                {
                    if (firstLine is not null)
                    {
                        var entry = ParseEntry(firstLine, continuation.ToString());
                        if (entry is not null && FilterEvaluator.Evaluate(filter, entry))
                            return true;
                    }
                    firstLine = raw;
                    continuation.Clear();
                }
                else if (firstLine is not null)
                {
                    if (continuation.Length > 0) continuation.Append('\n');
                    continuation.Append(raw);
                }
            }

            // Evaluate last buffered entry.
            if (firstLine is not null)
            {
                var entry = ParseEntry(firstLine, continuation.ToString());
                if (entry is not null && FilterEvaluator.Evaluate(filter, entry))
                    return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* unreadable file — skip */ }

        return false;
    }

    private static LogLine? ParseEntry(string firstLine, string continuation)
    {
        var parts = firstLine.Split('|');
        if (parts.Length < 6) return null;

        DateTime.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var ts);

        var level   = parts[1].Trim();
        var machine = parts[2].Trim();
        int.TryParse(parts[3].Trim(), out var pid);
        int.TryParse(parts[4].Trim(), out var tid);

        var message = parts[5].Trim();
        if (continuation.Length > 0)
            message = message + "\n" + continuation;

        var caller = parts.Length >= 7 ? parts[6].Trim() : null;

        return new LogLine(ts, level, machine, pid, tid, message, caller);
    }
}
