namespace BatchMonitor.Services;

/// <summary>
/// Scoped service that manages a single shared SignalR connection per Blazor circuit.
/// Tabs subscribe and unsubscribe to named groups via this service.
///
/// Step 1: stub only — real SignalR connection wired in Step 9.
/// </summary>
public class SignalRConnectionService : IAsyncDisposable
{
    private readonly ILogger<SignalRConnectionService> _logger;

    public SignalRConnectionService(ILogger<SignalRConnectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to real-time events for a given batch run in a given environment.
    /// No-op in Step 1.
    /// </summary>
    public Task SubscribeToBatchAsync(string env, string runId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SignalR subscription requested for {Env}/{RunId} (stub)", env, runId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unsubscribe from real-time events for a given batch run.
    /// No-op in Step 1.
    /// </summary>
    public Task UnsubscribeFromBatchAsync(string env, string runId)
    {
        _logger.LogDebug("SignalR unsubscription requested for {Env}/{RunId} (stub)", env, runId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
