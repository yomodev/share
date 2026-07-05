using NxtUI.Core.Filtering;

namespace NxtUI.Core.Services;

public record LogFolderNode(string Name, string RelativePath, bool HasChildren);

public record LogFileEntry(
    string Server,
    string FileName,
    long SizeBytes,
    DateTime CreatedUtc,
    DateTime LastModifiedUtc,
    string FullPath);

public interface ILogBrowserService
{
    string? ResolveRoot(string server);

    Task<IReadOnlyList<LogFolderNode>> GetSubfoldersAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default);

    Task<IReadOnlyList<LogFileEntry>> GetFilesAsync(
        IEnumerable<string> servers, string relativePath, CancellationToken ct = default);

    (string Server, string RelativePath)? ParseHintPath(string fullPath, IEnumerable<string> servers);

    /// <summary>
    /// Recursively walks all subfolders under <paramref name="rootRelPath"/> across all servers,
    /// yields each file whose name matches <paramref name="fileGlob"/>, and — if
    /// <paramref name="contentFilter"/> is non-null — only yields files where at least one
    /// parsed log line satisfies the filter.  Results stream as they are found.
    /// </summary>
    IAsyncEnumerable<LogFileEntry> SearchAsync(
        IEnumerable<string> servers,
        string rootRelPath,
        string fileGlob,
        FilterNode? contentFilter,
        CancellationToken ct = default);
}
