using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NxtUI.Core.Services;

/// <summary>
/// Uniform start/duration/error logging for I/O-bound operations (Kafka, SQL, disk
/// search, parsing, ...) so long-running or failing operations are easy to find by
/// grepping logs for "failed after" or "completed in" across every domain, instead of
/// each service inventing its own ad-hoc timing.
/// </summary>
public static class OperationLog
{
    /// <summary>Completions at or above this take LogWarning instead of LogDebug, so
    /// slow operations stand out from routine ones without raising the whole category's
    /// log level.</summary>
    public static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(3);

    public static async Task<T> RunAsync<T>(ILogger log, string action, Func<Task<T>> operation)
    {
        log.LogDebug("{Action}: started", action);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            LogCompleted(log, action, sw.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("{Action}: cancelled after {Ms}ms", action, sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Action}: failed after {Ms}ms", action, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public static Task RunAsync(ILogger log, string action, Func<Task> operation) =>
        RunAsync(log, action, async () => { await operation(); return true; });

    private static void LogCompleted(ILogger log, string action, TimeSpan elapsed)
    {
        if (elapsed >= SlowThreshold)
            log.LogWarning("{Action}: completed in {Ms}ms (slow)", action, (long)elapsed.TotalMilliseconds);
        else
            log.LogDebug("{Action}: completed in {Ms}ms", action, (long)elapsed.TotalMilliseconds);
    }
}
