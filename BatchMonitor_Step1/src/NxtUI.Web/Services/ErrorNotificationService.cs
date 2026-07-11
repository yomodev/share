using Microsoft.Extensions.Logging.Abstractions;

namespace NxtUI.Web.Services;

/// <summary>One recorded server-side error, shown as a toast and kept in Settings' history list.</summary>
public sealed record ErrorNotificationEntry(DateTime TimestampUtc, string Message, string? Source, string? Detail);

/// <summary>
/// Scoped (one per Blazor circuit) record of every server-side error reported
/// during this client session — both unhandled render exceptions (via
/// GlobalErrorBoundary) and unhandled UI-event-handler exceptions (via
/// ErrorNotificationHubFilter). Raises <see cref="OnError"/> so a toast can be
/// shown reactively; callers don't need to know about Snackbar directly.
///
/// Every call to <see cref="Report"/> is also written to NLog (this class's own
/// per-class log file, via the logger-shortName file target) with the full exception
/// and stack trace — Report is the single funnel every page/component uses to surface an
/// error, so logging it here once covers the whole app instead of requiring each call site
/// to remember to log separately.
/// </summary>
public sealed class ErrorNotificationService(ILogger<ErrorNotificationService>? logger = null)
{
    private const int MaxHistory = 200;
    private readonly object _lock = new();
    private readonly List<ErrorNotificationEntry> _history = [];
    private readonly ILogger _log = logger ?? NullLogger<ErrorNotificationService>.Instance;

    public event Action<ErrorNotificationEntry>? OnError;

    public IReadOnlyList<ErrorNotificationEntry> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public void Report(string message, Exception? exception = null, string? source = null)
    {
        var entry = new ErrorNotificationEntry(DateTime.UtcNow, message, source, exception?.ToString());
        _log.LogError(exception, "[{Source}] {Message}", source ?? "—", message);
        lock (_lock)
        {
            _history.Add(entry);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
        // Invoke each subscriber individually and swallow exceptions — Report() is itself
        // often called from inside a catch block (e.g. ConfigPage.SaveAsync), so a throwing
        // subscriber must never propagate back out here: that would escape the caller's own
        // catch and crash the circuit exactly while it's trying to report a failure.
        foreach (var handler in OnError?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<ErrorNotificationEntry>)handler).Invoke(entry);
            }
            catch { }
        }
    }

    public void Clear()
    {
        lock (_lock) _history.Clear();
    }
}
