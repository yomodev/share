using NxtUI.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace NxtUI.Hubs;

/// <summary>
/// SignalR hub for real-time run event streaming.
///
/// Clients call <see cref="SubscribeToRun"/> to join a group named
/// "{env}:{runId}". The server (MockRunService / MongoRunService)
/// pushes <see cref="PerformanceEvent"/> objects to that group via
/// <see cref="IHubContext{RunEventsHub}"/>.
///
/// Client-side method pushed to clients: "RunEvent" (single event upsert).
/// </summary>
public class RunEventsHub : Hub
{
    public async Task SubscribeToRun(string env, string runId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(env, runId));

    public async Task UnsubscribeFromRun(string env, string runId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(env, runId));

    public static string GroupName(string env, string runId) => $"{env}:{runId}";
}
