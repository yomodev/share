using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;

namespace NxtUI.Web.Services;

/// <summary>
/// Wraps every Blazor circuit hub message. Logs exceptions immediately and warns
/// when any message (render, event dispatch, JS interop) takes longer than the threshold.
/// Register via AddHubFilter&lt;BlazorCircuitFilter&gt;() in AddSignalR().
/// Enable via appsettings: "Diagnostics": { "Enabled": true }
/// </summary>
public sealed class BlazorCircuitFilter(ILogger<BlazorCircuitFilter> logger) : IHubFilter
{
    // Hub method names used by the Blazor protocol — friendlier to log than raw names.
    private static readonly Dictionary<string, string> MethodLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BeginInvokeDotNetFromJS"]  = "JS→.NET call",
        ["EndInvokeJSFromDotNet"]    = ".NET→JS result",
        ["DispatchBrowserEvent"]     = "browser event",
        ["OnRenderCompleted"]        = "render ack",
        ["OnErrorFromRenderer"]      = "renderer error",
    };

    private const int WarnMs  = 200;
    private const int ErrorMs = 1000;

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext ctx,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var sw = Stopwatch.StartNew();
        Exception? caught = null;
        try
        {
            return await next(ctx);
        }
        catch (Exception ex)
        {
            caught = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            var ms    = sw.ElapsedMilliseconds;
            var label = MethodLabels.GetValueOrDefault(ctx.HubMethodName, ctx.HubMethodName);

            if (caught is not null)
            {
                logger.LogError(caught,
                    "circuit exception | op={Op} duration={Ms}ms",
                    label, ms);
            }
            else if (ms >= ErrorMs)
            {
                logger.LogError(
                    "circuit very slow | op={Op} duration={Ms}ms — possible deadlock or thread starvation",
                    label, ms);
            }
            else if (ms >= WarnMs)
            {
                logger.LogWarning(
                    "circuit slow | op={Op} duration={Ms}ms",
                    label, ms);
            }
        }
    }

    // Log errors from hub connections/disconnections too.
    public async Task OnConnectedAsync(HubLifetimeContext ctx, Func<HubLifetimeContext, Task> next)
    {
        try { await next(ctx); }
        catch (Exception ex) { logger.LogError(ex, "circuit OnConnected failed"); throw; }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext ctx, Exception? ex, Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (ex is not null)
            logger.LogWarning(ex, "circuit disconnected with error");
        await next(ctx, ex);
    }
}
