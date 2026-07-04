using NxtUI.Core.Services;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Filtering;
using NxtUI.Web.Services;

namespace NxtUI.Web.Services;

public class LogBrowserService : ILogBrowserService
{
    private readonly LogPathSettings _settings;
    private readonly IReadOnlyList<CompiledLogFormat> _formats;

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

    public LogBrowserService(IOptions<LogPathSettings> settings, IConfiguration config)
    {
        _settings = settings.Value;

        // Same "Logs:Formats" templates the interactive LogViewer uses (see log-viewer-parser.js
        // compileFormat) — search must parse files the same way the viewer displays them, so a
        // filter like `caller:>` or `thread:5` matches consistently in both places.
        var formatStrings = config.GetSection("Logs:Formats").Get<string[]>() ?? [];
        _formats = CompileFormats(formatStrings);
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
                        var name = Path.GetFileName(d);
                        var rel  = string.IsNullOrEmpty(relativePath) ? name : relativePath + "\\" + name;
                        // Assume children exist; expand will reveal an empty node.
                        // Probing each child on a network share is prohibitively slow.
                        return new LogFolderNode(name, rel, HasChildren: true);
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

    private bool FileMatchesContent(string filePath, FilterNode filter, CancellationToken ct)
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

    // Tries each configured Logs:Formats template (same ones the interactive LogViewer
    // uses) against the entry's first line, falling back to the legacy fixed
    // pipe-delimited layout so existing deployments without a Formats config keep working.
    private LogLine? ParseEntry(string firstLine, string continuation)
    {
        foreach (var fmt in _formats)
        {
            var m = fmt.Regex.Match(firstLine);
            if (!m.Success) continue;

            DateTime.TryParse(Group(m, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var ts);

            var level   = Group(m, "level");
            var machine = Group(m, "host");
            int.TryParse(Group(m, "pid"),    out var pid);
            int.TryParse(Group(m, "thread"), out var tid);

            var message = Group(m, "message");
            if (continuation.Length > 0)
                message = message + "\n" + continuation;

            var caller = Group(m, "caller");
            return new LogLine(ts, level, machine, pid, tid, message,
                string.IsNullOrEmpty(caller) ? null : caller);
        }

        return ParseEntryLegacy(firstLine, continuation);
    }

    private static string Group(Match m, string name) =>
        m.Groups[name].Success ? m.Groups[name].Value.Trim() : "";

    private static LogLine? ParseEntryLegacy(string firstLine, string continuation)
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

    // ── private: format template compiler ─────────────────────────────────
    // Mirrors log-viewer-parser.js's compileFormat() so a search filter behaves
    // identically to the interactive viewer for the same Logs:Formats templates.

    // internal (not private) + [InternalsVisibleTo] so the format-grammar contract
    // test can compile a format string directly and compare it against the JS side.
    internal sealed record CompiledLogFormat(Regex Regex);

    private static readonly Regex TokenRegex = new(@"\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly HashSet<string> CaptureFields =
        new(StringComparer.Ordinal) { "timestamp", "level", "host", "pid", "thread", "message", "caller" };

    private static List<CompiledLogFormat> CompileFormats(IEnumerable<string> formatStrings)
    {
        var result = new List<CompiledLogFormat>();
        foreach (var fmt in formatStrings)
        {
            var compiled = CompileFormat(fmt);
            if (compiled is not null) result.Add(compiled);
        }
        return result;
    }

    internal static CompiledLogFormat? CompileFormat(string? formatStr)
    {
        if (string.IsNullOrEmpty(formatStr)) return null;

        var tokens = new List<(bool IsPlaceholder, string Value)>();
        var last   = 0;
        foreach (Match m in TokenRegex.Matches(formatStr))
        {
            if (m.Index > last) tokens.Add((false, formatStr[last..m.Index]));
            tokens.Add((true, m.Groups[1].Value));
            last = m.Index + m.Length;
        }
        if (last < formatStr.Length) tokens.Add((false, formatStr[last..]));

        var pattern    = new StringBuilder("^");
        var fieldCount = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var (isPlaceholder, val) = tokens[i];
            if (!isPlaceholder) { pattern.Append(Regex.Escape(val)); continue; }

            if (val == "newline") { pattern.Append(@"\n"); continue; }
            if (val == "$")       { pattern.Append(@"(?=$|\n)"); continue; }
            if (val == "*")       { pattern.Append(@"[^\n]*"); continue; }

            // Determine the stopper based on what follows this field, same as the JS compiler.
            string stopper;
            (bool IsPlaceholder, string Value)? next =
                i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (next is null)
            {
                stopper = "[^\\n]*"; // last token: greedy to EOL
            }
            else if (next.Value.IsPlaceholder)
            {
                stopper = next.Value.Value is "$" or "*" or "newline"
                    ? "[^\\n]*"   // before an EOL marker: greedy
                    : "[^\\n]*?"; // before another capture: non-greedy
            }
            else
            {
                var fc    = next.Value.Value.Length > 0 ? next.Value.Value[0] : '\0';
                var fcEsc = fc is ']' or '\\' or '^' or '-' ? "\\" + fc : fc.ToString();
                stopper   = fc == '\0' ? "[^\\n]*" : $"[^\\n{fcEsc}]*";
            }

            if (CaptureFields.Contains(val))
            {
                pattern.Append($"(?<{val}>{stopper})");
                fieldCount++;
            }
            else
            {
                pattern.Append($"(?:{stopper})");
            }
        }

        if (fieldCount < 2) return null; // too few fields to be useful

        return new CompiledLogFormat(new Regex(pattern.ToString(),
            RegexOptions.Compiled | RegexOptions.Multiline));
    }
}
