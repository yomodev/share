using NxtUI.Core.Logging;
using NxtUI.Core.Services;
using System.Text;

namespace NxtUI.Web.Services;

public class LogViewerService : ILogViewerService
{
    public async Task<(string Text, long Offset)> ReadAllAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await sr.ReadToEndAsync(ct);
        return (text, fs.Position);
    }

    public (string Text, long NewOffset) ReadDelta(string path, long fromOffset)
    {
        var result = IncrementalFileReader.ReadNew(path, fromOffset);
        if (result.Lines.Count == 0) return ("", result.NewOffset);
        return (string.Join("\n", result.Lines), result.NewOffset);
    }
}
