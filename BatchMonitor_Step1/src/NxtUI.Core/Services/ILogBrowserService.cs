namespace NxtUI.Services;

public record LogFolderNode(string Name, string RelativePath, bool HasChildren);

public record LogFileEntry(
    string   Server,
    string   FileName,
    long     SizeBytes,
    DateTime CreatedUtc,
    DateTime LastModifiedUtc,
    string   FullPath);

public interface ILogBrowserService
{
    /// <summary>
    /// Returns the resolved base path for a given server (LogsFolder with {server} substituted).
    /// Returns null if LogsFolder is not configured.
    /// </summary>
    string? ResolveRoot(string server);

    /// <summary>
    /// Returns the list of configured servers. If none configured, falls back to the
    /// provided <paramref name="heartbeatHosts"/>.
    /// </summary>
    IReadOnlyList<string> GetServers(IEnumerable<string> heartbeatHosts);

    /// <summary>
    /// Returns immediate child folders at <paramref name="relativePath"/> across all
    /// configured servers, merged into a distinct union by folder name.
    /// </summary>
    Task<IReadOnlyList<LogFolderNode>> GetSubfoldersAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Returns all files found at <paramref name="relativePath"/> across all configured
    /// servers. Each entry carries the server it came from.
    /// </summary>
    Task<IReadOnlyList<LogFileEntry>> GetFilesAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Extracts the server name and relative path from a full path previously
    /// resolved via ServiceTemplates. Returns null if the path cannot be matched
    /// against any server's LogsFolder root.
    /// </summary>
    (string Server, string RelativePath)? ParseHintPath(string fullPath, IEnumerable<string> servers);
}
