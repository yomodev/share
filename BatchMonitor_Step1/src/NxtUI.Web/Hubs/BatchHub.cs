using Microsoft.AspNetCore.SignalR;

namespace BatchMonitor.Hubs;

/// <summary>
/// SignalR hub for real-time batch event streaming.
/// Step 1: stub only. Full implementation in Step 9.
/// Clients subscribe to groups named "{env}:{runId}".
/// </summary>
public class BatchHub : Hub
{
    /// <summary>
    /// Called by clients to subscribe to real-time updates for a specific batch.
    /// </summary>
    public async Task SubscribeToBatch(string env, string runId)
    {
        var groupName = $"{env}:{runId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Called by clients to unsubscribe from a specific batch's updates.
    /// </summary>
    public async Task UnsubscribeFromBatch(string env, string runId)
    {
        var groupName = $"{env}:{runId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}
