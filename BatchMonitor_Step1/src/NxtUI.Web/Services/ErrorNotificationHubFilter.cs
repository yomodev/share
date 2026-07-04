using Microsoft.AspNetCore.SignalR;

namespace NxtUI.Web.Services;

/// <summary>
/// Catches exceptions thrown while dispatching a UI event (button click, etc.)
/// to its C# handler. Blazor Server normally treats an unhandled exception here
/// as fatal to the circuit; this reports it via <see cref="ErrorNotificationService"/>
/// (toast + Settings history) and swallows it instead, so the rest of the app
/// keeps working. Always registered — independent of the Diagnostics:Enabled flag,
/// unlike BlazorCircuitFilter's slow-operation logging.
/// </summary>
public sealed class ErrorNotificationHubFilter(
    ErrorNotificationService notifications,
    ILogger<ErrorNotificationHubFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext ctx, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(ctx);
        }
        catch (Exception ex) when (ctx.HubMethodName.Equals("DispatchBrowserEvent", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(ex, "Unhandled exception in a UI event handler — recovered");
            notifications.Report("An error occurred while handling your action.", ex, "UI event");
            return null; // swallow — keep the circuit alive instead of letting it crash
        }
    }
}
