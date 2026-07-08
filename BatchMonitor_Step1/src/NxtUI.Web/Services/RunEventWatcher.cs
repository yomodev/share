using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Shared, run-scoped poller that gives real (non-self-pushing)
/// <see cref="IRunService"/> backends — SqlRunService, MongoRunService — live
/// push without every open circuit polling the database on its own.
///
/// Wires into <see cref="RunEventBroker"/>'s subscriber-count transitions:
/// starts polling a run's events the moment the first circuit subscribes via
/// <see cref="RunEventBroker.SubscribeToRunAsync"/>, and stops the moment the
/// last one unsubscribes — so N circuits watching the same run share one poll
/// loop instead of each running their own (today, each circuit's
/// PerformanceEventService polls independently).
///
/// Backends that already push directly (<see cref="IPushesOwnRunEvents"/>,
/// e.g. <see cref="MockRunService"/>) are skipped entirely — nothing to add here.
/// </summary>
public class RunEventWatcher(
    IRunService runService,
    RunEventBroker broker,
    ILogger<RunEventWatcher> logger) : IHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private sealed class Watch
    {
        public required CancellationTokenSource Cts { get; init; }
        public int RefCount;
        public DateTime Since;
    }

    private readonly Dictionary<string, Watch> _watches = new();
    private readonly object _lock = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (runService is IPushesOwnRunEvents) return Task.CompletedTask;

        broker.RunWatchStarted += OnRunWatchStarted;
        broker.RunWatchStopped += OnRunWatchStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        broker.RunWatchStarted -= OnRunWatchStarted;
        broker.RunWatchStopped -= OnRunWatchStopped;

        lock (_lock)
        {
            foreach (var watch in _watches.Values) watch.Cts.Cancel();
            _watches.Clear();
        }
        return Task.CompletedTask;
    }

    private void OnRunWatchStarted(string env, string runId)
    {
        var key = Key(env, runId);
        lock (_lock)
        {
            if (_watches.TryGetValue(key, out var existing) && !existing.Cts.IsCancellationRequested)
            {
                existing.RefCount++;
                return;
            }

            // Start a few seconds before "now" rather than at the run's start — each
            // subscribing circuit already loaded full history itself before subscribing
            // (see PerformanceEventService.LoadHistoryAsync); this loop only needs to
            // deliver events created from here forward.
            var watch = new Watch { Cts = new CancellationTokenSource(), RefCount = 1, Since = DateTime.UtcNow.AddSeconds(-5) };
            _watches[key] = watch;
            _ = PollLoopAsync(env, runId, key, watch, watch.Cts.Token);
        }
    }

    private void OnRunWatchStopped(string env, string runId)
    {
        var key = Key(env, runId);
        lock (_lock)
        {
            if (!_watches.TryGetValue(key, out var watch)) return;
            watch.RefCount--;
            if (watch.RefCount <= 0) watch.Cts.Cancel();
        }
    }

    private async Task PollLoopAsync(string env, string runId, string key, Watch watch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
                if (ct.IsCancellationRequested) break;

                var events = await runService.GetRunEventsAsync(env, runId, watch.Since, ct);
                if (events?.Count > 0)
                {
                    foreach (var evt in events)
                        broker.Publish(env, runId, evt);
                    watch.Since = events.Max(e => e.LastUpdate);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RunEventWatcher poll error for {Env}/{RunId}", env, runId);
            }
        }

        lock (_lock)
        {
            if (_watches.TryGetValue(key, out var current) && ReferenceEquals(current, watch))
                _watches.Remove(key);
        }
    }

    private static string Key(string env, string runId) => $"{env}:{runId}";
}
