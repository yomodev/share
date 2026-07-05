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
}
