using System.Text;

namespace NxtUI.Logging;

/// <summary>
/// Reads only the bytes appended since a previous read, without locking the file
/// (opens with FileShare.ReadWrite | FileShare.Delete so the writer is never blocked).
/// Only complete lines — up to the last newline — are returned; a partial trailing
/// line is left for the next call. Handles truncation/rotation by resetting offset.
/// </summary>
public static class IncrementalFileReader
{
    public readonly record struct Result(IReadOnlyList<string> Lines, long NewOffset);

    public static Result ReadNew(string path, long offset, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;

        var fi = new FileInfo(path);
        if (!fi.Exists) return new Result(Array.Empty<string>(), offset);

        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = fs.Length;
        if (length < offset) offset = 0;          // file rotated / truncated
        if (length == offset) return new Result(Array.Empty<string>(), offset);

        fs.Seek(offset, SeekOrigin.Begin);

        var count  = (int)(length - offset);
        var buffer = new byte[count];
        var read   = 0;
        while (read < count)
        {
            var n = fs.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }

        // Only consume through the last newline — keep any partial line for next time.
        var lastNl = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNl < 0) return new Result(Array.Empty<string>(), offset);

        var text  = encoding.GetString(buffer, 0, lastNl + 1);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd('\r');

        return new Result(lines, offset + lastNl + 1);
    }
}
