namespace NxtUI.Services;

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
    /// </summary>
    (string Text, long NewOffset) ReadDelta(string path, long fromOffset);
}
