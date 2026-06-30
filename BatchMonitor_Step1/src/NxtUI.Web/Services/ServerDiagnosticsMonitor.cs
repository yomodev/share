using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NxtUI.Web.Services;

/// <summary>
/// Debugging aid: periodically samples server-side health indicators and logs them.
/// Disable via appsettings: "Diagnostics": { "Enabled": false }
/// </summary>
public sealed class ServerDiagnosticsMonitor(ILogger<ServerDiagnosticsMonitor> logger) : BackgroundService
{
    // How often to sample. Larger interval = less log noise.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // Warn when timer fires later than this much past its due time (thread pool saturation).
    private const double LagWarnMs  = 500;
    private const double LagInfoMs  = 100;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("ServerDiagnosticsMonitor started (interval={Interval}s)", Interval.TotalSeconds);

        var process    = Process.GetCurrentProcess();
        var prevCpuTime = process.TotalProcessorTime;
        var prevWall    = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException) { break; }
            sw.Stop();

            // How much did the timer overshoot? Indicates thread pool / event loop pressure.
            var lag = sw.Elapsed - Interval;

            try
            {
                ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
                ThreadPool.GetMaxThreads(out int workerMax,   out int ioMax);
                int workerUsed = workerMax - workerAvail;
                int ioUsed     = ioMax     - ioAvail;

                process.Refresh();
                var nowCpu   = process.TotalProcessorTime;
                var nowWall  = Stopwatch.GetTimestamp();
                var cpuDelta = (nowCpu - prevCpuTime).TotalMilliseconds;
                var wallMs   = (nowWall - prevWall) * 1000.0 / Stopwatch.Frequency;
                var cpuPct   = wallMs > 0 ? cpuDelta / wallMs * 100.0 : 0;
                prevCpuTime  = nowCpu;
                prevWall     = nowWall;

                var wsMb     = process.WorkingSet64 / 1_048_576.0;
                var threads  = process.Threads.Count;

                var level = lag.TotalMilliseconds > LagWarnMs ? LogLevel.Warning
                          : lag.TotalMilliseconds > LagInfoMs ? LogLevel.Information
                          : LogLevel.Debug;

                logger.Log(level,
                    "diag | lag={Lag:F0}ms | cpu={Cpu:F1}% | ws={WS:F0}MB | threads={Threads} | pool worker={WorkerUsed}/{WorkerMax} io={IoUsed}/{IoMax}",
                    lag.TotalMilliseconds, cpuPct, wsMb, threads, workerUsed, workerMax, ioUsed, ioMax);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ServerDiagnosticsMonitor: sample failed");
            }
        }

        logger.LogInformation("ServerDiagnosticsMonitor stopped");
    }
}
