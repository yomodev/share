using Microsoft.AspNetCore.SignalR;

namespace NxtUI.Web.Hubs;

/// <summary>
/// SignalR hub for real-time run event streaming.
/// Clients subscribe to groups named "{env}:{runId}".
/// </summary>
public class RunHub : Hub
{
    public async Task SubscribeToRun(string env, string runId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, $"{env}:{runId}");

    public async Task UnsubscribeFromRun(string env, string runId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"{env}:{runId}");
}
