using NxtUI.Core.Logging;
using NxtUI.Core.Services;
using System.Text;

namespace NxtUI.Web.Services;

public class LogViewerService : ILogViewerService
{
    public Task<(string Text, long Offset)> ReadAllAsync(string path, CancellationToken ct = default)
    {
        // The whole read runs on a thread-pool thread. Opening a FileStream without
        // FileOptions.Asynchronous makes ReadToEndAsync fall back to synchronous, thread-
        // blocking I/O — and these are log files on network shares that can be large and
        // slow, so we must never let that happen on the Blazor circuit's UI thread.
        return Task.Run(() =>
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = sr.ReadToEnd();
            return (text, fs.Position);
        }, ct);
    }

    public Task<(string Text, long NewOffset)> ReadDeltaAsync(string path, long fromOffset, CancellationToken ct = default) =>
        // IncrementalFileReader.ReadNew is synchronous file I/O — offload it so the tail
        // poller never reads (a possibly slow network-share file) on the UI thread.
        Task.Run(() =>
        {
            var result = IncrementalFileReader.ReadNew(path, fromOffset);
            if (result.Lines.Count == 0) return ("", result.NewOffset);
            return (string.Join("\n", result.Lines), result.NewOffset);
        }, ct);
}
