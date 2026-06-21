using BatchMonitor.Models;
using Microsoft.AspNetCore.SignalR;

namespace BatchMonitor.Hubs;

/// <summary>
/// SignalR hub for real-time batch event streaming (Step 9).
///
/// Clients call <see cref="SubscribeToBatch"/> to join a group named
/// "{env}:{runId}". The server (MockBatchService / MongoBatchService)
/// pushes <see cref="PerformanceEvent"/> objects to that group via
/// <see cref="IHubContext{BatchEventsHub}"/>.
///
/// Client-side method pushed to clients: "BatchEvent" (single event upsert).
/// </summary>
public class BatchEventsHub : Hub
{
    public async Task SubscribeToBatch(string env, string runId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(env, runId));

    public async Task UnsubscribeFromBatch(string env, string runId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(env, runId));

    public static string GroupName(string env, string runId) => $"{env}:{runId}";
}
