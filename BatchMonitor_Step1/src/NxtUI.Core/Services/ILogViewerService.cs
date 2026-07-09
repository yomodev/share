namespace NxtUI.Core.Services;

public interface ILogViewerService
{
    /// <summary>
    /// Read the entire content of a log file (non-locking) and return the byte
    /// offset reached so subsequent delta reads start from the right position.
    /// </summary>
    Task<(string Text, long Offset)> ReadAllAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Read only the bytes appended since <paramref name="fromOffset"/>.
    /// Returns empty text (and the unchanged offset) when nothing is new.
    /// Runs the (blocking) file read on a background thread so a tail poll never
    /// touches the caller's (UI) thread.
    /// </summary>
    Task<(string Text, long NewOffset)> ReadDeltaAsync(string path, long fromOffset, CancellationToken ct = default);

    /// <summary>
    /// Cheap stat (no content read) — the file's current size in bytes. Used to know
    /// where a later delta/tail read should start from when the initial content is
    /// fetched separately (see the /api/logs/read HTTP endpoint) rather than via
    /// ReadAllAsync.
    /// </summary>
    Task<long> GetFileSizeAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Opens the file for a streaming read, same sharing mode as ReadAllAsync. Caller
    /// owns and must dispose the returned stream. Used by the /api/logs/read endpoint
    /// to serve content over plain HTTP without ever buffering the whole file into a
    /// C# string (which ReadAllAsync does, and which then has to cross SignalR as one
    /// interop payload).
    /// </summary>
    FileStream OpenRead(string path);
}
