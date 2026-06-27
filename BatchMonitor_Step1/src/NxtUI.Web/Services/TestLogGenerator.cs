using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Logging;
using NxtUI.Models;

namespace NxtUI.Services;

/// <summary>
/// Test-only background service. Materialises metrics log files in folders that
/// match the LogPaths templates (same placeholder expansion the discovery service
/// uses), so the Services page can find them and the monitoring/parsing can be
/// exercised end-to-end without real services.
///
/// - Seeds each file with a few backfilled lines on startup.
/// - Appends a new metrics line to every file each WriteIntervalSeconds.
/// - Deterministically skips ~1/3 of services (by PID) so some have no logs.
///
/// Gated by TestLogGenerator:Enabled — keep it off in production. In Development
/// appsettings, Logs:ServiceTemplates points at a local writable folder so we don't
/// hit (and hang on) the real UNC shares.
/// </summary>
public sealed class TestLogGenerator : BackgroundService
{
    private readonly TestLogGeneratorSettings _gen;
    private readonly LogPathSettings           _paths;
    private readonly AppSettings               _app;
    private readonly IHeartbeatService         _heartbeat;
    private readonly ILogger<TestLogGenerator> _log;
    private readonly Random                    _rng = new();
    private readonly List<Target>              _targets = new();

    public TestLogGenerator(
        IOptions<TestLogGeneratorSettings> gen,
        IOptions<LogPathSettings>          paths,
        IOptions<AppSettings>              app,
        IHeartbeatService                  heartbeat,
        ILogger<TestLogGenerator>          log)
    {
        _gen       = gen.Value;
        _paths     = paths.Value;
        _app       = app.Value;
        _heartbeat = heartbeat;
        _log       = log;
    }

    private sealed class Target
    {
        public required string   FilePath   { get; init; }
        public required string   Server     { get; init; }
        public required int      Pid        { get; init; }
        public required string   StreamName { get; init; }
        public required string   MsgId      { get; init; }
        public required DateTime ProcStart  { get; init; }
        public required int      Thread     { get; init; }
        public long Current;
        public long Peak;
        public long Child;
        public long ChildPeak;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_gen.Enabled)
        {
            _log.LogInformation("TestLogGenerator disabled.");
            return;
        }
        if (_paths.ServiceTemplates.Count == 0)
        {
            _log.LogWarning("TestLogGenerator: no Logs:ServiceTemplates configured — nothing to generate.");
            return;
        }

        try { await SeedAsync(stoppingToken); }
        catch (Exception ex) { _log.LogError(ex, "TestLogGenerator: seeding failed."); }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _gen.WriteIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Tick();
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        var template = _paths.ServiceTemplates[Math.Clamp(_gen.TemplateIndex, 0, _paths.ServiceTemplates.Count - 1)];
        var fileName = string.IsNullOrWhiteSpace(_paths.MetricsFileName) ? "Metrics.log" : _paths.MetricsFileName;

        // Honour the Environments allowlist — default (empty) means all.
        var envs = _gen.Environments.Count > 0
            ? _app.Environments.Where(e => _gen.Environments.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : _app.Environments;

        int generated = 0, skipped = 0;
        foreach (var env in envs)
        {
            List<ServiceStatus> services;
            try { services = await _heartbeat.GetServiceStatusesAsync(env.Id, ct); }
            catch (Exception ex)
            {
                _log.LogWarning("TestLogGenerator: skipping env {Env} — {Error}", env.Id, ex.Message);
                continue;
            }
            foreach (var svc in services)
            {
                // Skip ~1/3 of services (deterministic per PID) so some have no logs.
                if (svc.ProcessId % 3 == 0) { skipped++; continue; }

                // Always use today so the folder matches what the log browser shows.
                var folder = LogPathTemplate.Expand(template, svc, env.Id, DateTime.Today)
                                            .Replace("*", "Run" + RandomHex(8));
                Directory.CreateDirectory(folder);

                var target = new Target
                {
                    FilePath   = Path.Combine(folder, fileName),
                    Server     = svc.HostName,
                    Pid        = svc.ProcessId,
                    StreamName = $"{svc.ServiceName}Stream-{env.Id}",
                    MsgId      = RandomHex(24),
                    ProcStart  = DateTime.UtcNow.AddHours(-_rng.Next(1, 8)),
                    Thread     = 200 + _rng.Next(0, 50),
                    Current    = Mb(200 + _rng.Next(0, 120)),
                    Child      = 0,
                    ChildPeak  = Mb(500 + _rng.Next(0, 200)),
                };
                target.Peak = target.Current;

                // Backfill lines from midnight (or process start) up to now at the write interval.
                var now      = DateTime.Now;
                var dayStart = now.Date;
                var totalSec = (int)(now - dayStart).TotalSeconds;
                var steps    = Math.Max(_gen.InitialLineCount, totalSec / Math.Max(1, _gen.WriteIntervalSeconds));
                for (var k = steps; k >= 1; k--)
                {
                    Advance(target);
                    WriteLine(target, now.AddSeconds(-(long)k * _gen.WriteIntervalSeconds));
                }

                _targets.Add(target);
                generated++;
            }
        }

        _log.LogInformation(
            "TestLogGenerator: seeded {Generated} files, skipped {Skipped} services (template '{Template}', file '{File}').",
            generated, skipped, template, fileName);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var t in _targets)
        {
            Advance(t);
            WriteLine(t, now);
        }
    }

    private void Advance(Target t)
    {
        t.Current   = Math.Clamp(t.Current + Mb(_rng.Next(-20, 41)), Mb(50), Mb(2048));
        t.Peak      = Math.Max(t.Peak, t.Current);
        t.Child     = _rng.Next(0, 5) == 0 ? Mb(_rng.Next(0, 50)) : 0;
        t.ChildPeak = Math.Max(t.ChildPeak, t.Child);
    }

    private void WriteLine(Target t, DateTime ts)
    {
        var line =
            $"{ts:yyyy-MM-dd HH:mm:ss.ffff}|INFO|{t.Server}|{t.Pid}|{t.Thread}|" +
            $"Sending Metrics Tracker message {t.StreamName}, " +
            $"{t.MsgId}.Memory current usage: {t.Current} byte, " +
            $"peak usage: {t.Peak} byte, " +
            $"child usage: {t.Child}, " +
            $"child peak usage: {t.ChildPeak}, " +
            $"Process start time \"{t.ProcStart:yyyy-MM-ddTHH:mm:ss.fffffff}Z\"|" +
            $"Axis.Streams.Core.Infrastructure.MetricsTrackerService.Start";

        try
        {
            // FileShare.ReadWrite|Delete so the monitoring reader never blocks the writer.
            using var fs = new FileStream(t.FilePath, FileMode.Append, FileAccess.Write,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sw = new StreamWriter(fs);
            sw.WriteLine(line);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TestLogGenerator: failed writing {Path}", t.FilePath);
        }
    }

    private static long Mb(int mb) => (long)mb * 1024 * 1024;

    private string RandomHex(int len)
    {
        const string hex = "0123456789abcdef";
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = hex[_rng.Next(16)];
        return new string(chars);
    }
}
