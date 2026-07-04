using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using NxtUI.Web.Services;

namespace NxtUI.Web.Components;

/// <summary>
/// Wraps the main content area. When a component throws an unhandled exception:
///   1. Logs it with full stack trace.
///   2. Reports it via ErrorNotificationService (toast + Settings history).
///   3. Auto-recovers so the rest of the UI continues working.
///
/// Usage in Razor: <GlobalErrorBoundary>...content...</GlobalErrorBoundary>
/// </summary>
public sealed class GlobalErrorBoundary : ErrorBoundary
{
    [Inject] private ErrorNotificationService Notifications { get; set; } = default!;
    [Inject] private ILogger<GlobalErrorBoundary> Logger { get; set; } = default!;

    protected override async Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "Unhandled component exception — recovering");

        var msg = exception.Message.Length > 150
            ? exception.Message[..150] + "…"
            : exception.Message;

        Notifications.Report(
            $"An error occurred and was recovered automatically.\n{msg}",
            exception,
            "Render");

        // Yield so the current render frame completes, then reset the boundary.
        await Task.Yield();
        Recover();
    }
}
