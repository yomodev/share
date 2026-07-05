using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Logging;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Test-only background service that:
///   1. Writes Metrics.log files under ServiceTemplates paths (for the monitoring pipeline).
///   2. Writes realistic App.log files under LogsFolder paths (for the log browser).
///
/// Both sets use today's date so the log browser always shows a folder for the current day.
/// Gated by TestLogGenerator:Enabled — keep it off in production.
/// </summary>
public sealed class TestLogGenerator : BackgroundService
{
    private readonly TestLogGeneratorSettings _gen;
    private readonly LogPathSettings _paths;
    private readonly AppSettings _app;
    private readonly IHeartbeatService _heartbeat;
    private readonly ILogger<TestLogGenerator> _log;
    private readonly Random _rng = new();
    private readonly List<MetricsTarget> _metricsTargets = new();
    private readonly List<AppLogTarget> _appLogTargets = new();

    // ── realistic app-log vocabulary ─────────────────────────────────────────
    private static readonly string[] InfoMessages =
    [
        "Request processed successfully in {ms}ms",
        "Batch {b} completed: {n} items processed",
        "Connection established to {host}:{port}",
        "Cache hit for key={k} (ttl={n}s remaining)",
        "Scheduled job started: interval={n}s",
        "Health check passed for component={c}",
        "Configuration reloaded from {src}",
        "Queue consumer started on partition={p}",
        "Snapshot committed: revision={n}",
        "Session {id} authenticated successfully",
    ];
    private static readonly string[] WarnMessages =
    [
        "Slow query detected: {ms}ms for table={t}",
        "Retry attempt {n}/3 for operation={op}",
        "Queue depth high: {n} pending messages on partition={p}",
        "Rate limit approaching: {n}/100 requests used",
        "Memory usage at {n}% of configured limit",
        "Circuit breaker half-open — allowing probe request",
        "Dependency {dep} response time degraded: {ms}ms",
        "Dead-letter queue growing: {n} messages",
    ];
    private static readonly string[] DebugMessages =
    [
        "Processing item id={n} batch={b}",
        "Deserializing payload size={n} bytes",
        "Acquiring lock on resource={r}",
        "Evaluating rule {n} of {m}",
        "Cache miss for key={k} — fetching from DB",
        "Correlation id={id} propagated to downstream",
    ];
    private static readonly string[] ErrorMessages =
    [
        "Unhandled exception in {caller}",
        "Failed to connect to {host}:{port} after {n} retries",
        "Timeout waiting for lock on resource={r}",
        "Deserialization error in message id={id}",
    ];
    private static readonly string[] ExceptionTypes =
    [
        "System.InvalidOperationException",
        "System.TimeoutException",
        "System.IO.IOException",
        "System.NullReferenceException",
        "System.Net.Http.HttpRequestException",
    ];
    private static readonly string[] Callers =
    [
        "Service.ExecuteAsync",
        "Worker.ProcessAsync",
        "Handler.HandleAsync",
        "Repository.SaveAsync",
        "Pipeline.RunAsync",
        "Scheduler.RunJobAsync",
        "Consumer.ConsumeAsync",
        "Gateway.ForwardAsync",
    ];
    private static readonly string[] Components =
    [
        "kafka", "redis", "mongodb", "rabbitmq", "elasticsearch",
    ];
    private static readonly string[] Resources =
    [
        "resource-0", "resource-1", "resource-2", "resource-3",
    ];

    public TestLogGenerator(
        IOptions<TestLogGeneratorSettings> gen,
        IOptions<LogPathSettings> paths,
        IOptions<AppSettings> app,
        IHeartbeatService heartbeat,
        ILogger<TestLogGenerator> log)
    {
        _gen = gen.Value;
        _paths = paths.Value;
        _app = app.Value;
        _heartbeat = heartbeat;
        _log = log;
    }

    private sealed class MetricsTarget
    {
        public required string FilePath { get; init; }
        public required string Server { get; init; }
        public required int Pid { get; init; }
        public required string StreamName { get; init; }
        public required string MsgId { get; init; }
        public required DateTime ProcStart { get; init; }
        public required int Thread { get; init; }
        public long Current;
        public long Peak;
        public long Child;
        public long ChildPeak;
    }

    private sealed class AppLogTarget
    {
        public required string FilePath { get; init; }
        public required string Server { get; init; }
        public required int Pid { get; init; }
        public required string Service { get; init; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_paths.ServiceTemplates.Count == 0)
        {
            _log.LogWarning("TestLogGenerator: no Logs:ServiceTemplates configured — nothing to generate.");
            return;
        }

        await Task.Yield(); // release startup thread immediately

        try { await Task.Run(() => SeedAsync(stoppingToken), stoppingToken); }
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
        var metricsFile = string.IsNullOrWhiteSpace(_paths.MetricsFileName) ? "Metrics.log" : _paths.MetricsFileName;

        var envs = _gen.Environments.Count > 0
            ? _app.Environments.Where(e => _gen.Environments.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : _app.Environments;

        int metricsGenerated = 0, appGenerated = 0, skipped = 0;

        foreach (var env in envs)
        {
            List<ServiceStatus> services;
            try { services = await _heartbeat.GetServiceStatusesAsync(env.Id, ct: ct); }
            catch (Exception ex)
            {
                _log.LogWarning("TestLogGenerator: skipping env {Env} — {Error}", env.Id, ex.Message);
                continue;
            }

            foreach (var svc in services)
            {
                if (svc.ProcessId % 3 == 0) { skipped++; continue; }

                // ── 1. Metrics.log under ServiceTemplates path ────────────────
                var metricsFolder = LogPathTemplate.Expand(template, svc, env.Id, DateTime.Today)
                                                   .Replace("*", "Run" + RandomHex(8));
                Directory.CreateDirectory(metricsFolder);

                var mt = new MetricsTarget
                {
                    FilePath = Path.Combine(metricsFolder, metricsFile),
                    Server = svc.HostName,
                    Pid = svc.ProcessId,
                    StreamName = $"{svc.ServiceName}Stream-{env.Id}",
                    MsgId = RandomHex(24),
                    ProcStart = DateTime.UtcNow.AddHours(-_rng.Next(1, 8)),
                    Thread = 200 + _rng.Next(0, 50),
                    Current = Mb(200 + _rng.Next(0, 120)),
                    Child = 0,
                    ChildPeak = Mb(500 + _rng.Next(0, 200)),
                };
                mt.Peak = mt.Current;

                var now = DateTime.Now;
                var totalSec = (int)(now - now.Date).TotalSeconds;
                var steps = Math.Max(_gen.InitialLineCount, totalSec / Math.Max(1, _gen.WriteIntervalSeconds));
                for (var k = steps; k >= 1; k--)
                {
                    AdvanceMetrics(mt);
                    WriteMetricsLine(mt, now.AddSeconds(-(long)k * _gen.WriteIntervalSeconds));
                }

                _metricsTargets.Add(mt);
                metricsGenerated++;

                // ── 2. App logs under LogsFolder path (today + past days) ───
                var logsRoot = _paths.LogsFolder;
                if (!string.IsNullOrWhiteSpace(logsRoot))
                {
                    var serverRoot = logsRoot.Replace("{server}", svc.HostName, StringComparison.OrdinalIgnoreCase);
                    const int appIntervalSec = 30;

                    // Today — full backfill from midnight to now, keep as live target
                    {
                        var appFolder = Path.Combine(serverRoot, DateTime.Today.ToString("yyyy-MM-dd"), svc.ServiceName);
                        Directory.CreateDirectory(appFolder);
                        // Errors subfolder (empty on some services, populated on others)
                        if (svc.ProcessId % 2 == 0)
                            Directory.CreateDirectory(Path.Combine(appFolder, "Errors"));

                        var at = new AppLogTarget { FilePath = Path.Combine(appFolder, "App.log"), Server = svc.HostName, Pid = svc.ProcessId, Service = svc.ServiceName };
                        var appSteps = totalSec / appIntervalSec;
                        for (var k = appSteps; k >= 1; k--)
                            WriteAppLine(at, now.AddSeconds(-(long)k * appIntervalSec));
                        _appLogTargets.Add(at);
                        appGenerated++;
                    }

                    // Past days — seed 1–5 days back with full-day logs (86400s / 30s = 2880 lines/day)
                    var pastDays = new[] { 1, 2, 3, 5 };
                    foreach (var daysBack in pastDays)
                    {
                        // Some services randomly skipped on past days to simulate services not running every day
                        if (_rng.Next(3) == 0) continue;

                        var date = DateTime.Today.AddDays(-daysBack);
                        var appFolder = Path.Combine(serverRoot, date.ToString("yyyy-MM-dd"), svc.ServiceName);
                        Directory.CreateDirectory(appFolder);
                        if (svc.ProcessId % 2 == 0)
                            Directory.CreateDirectory(Path.Combine(appFolder, "Errors"));

                        var at = new AppLogTarget { FilePath = Path.Combine(appFolder, "App.log"), Server = svc.HostName, Pid = svc.ProcessId, Service = svc.ServiceName };
                        var start = date; // midnight
                        for (var k = 86400 / appIntervalSec; k >= 1; k--)
                            WriteAppLine(at, start.AddSeconds((long)(86400 / appIntervalSec - k) * appIntervalSec));

                        // Some days also have a Debug.log with verbose output
                        if (daysBack <= 2 && _rng.Next(2) == 0)
                        {
                            var dbg = new AppLogTarget { FilePath = Path.Combine(appFolder, "Debug.log"), Server = svc.HostName, Pid = svc.ProcessId, Service = svc.ServiceName };
                            for (var k = 86400 / 10; k >= 1; k--)
                                WriteAppLine(dbg, start.AddSeconds((long)(86400 / 10 - k) * 10));
                        }

                        appGenerated++;
                    }

                    // Archive subfolder — empty, just to test browsing empty dirs
                    Directory.CreateDirectory(Path.Combine(serverRoot, "Archive", svc.ServiceName));
                }
            }
        }

        _log.LogInformation(
            "TestLogGenerator: seeded {M} metrics files, {A} app-log files, skipped {S} services.",
            metricsGenerated, appGenerated, skipped);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var t in _metricsTargets)
        {
            AdvanceMetrics(t);
            WriteMetricsLine(t, now);
        }
        foreach (var t in _appLogTargets)
        {
            // Write 1-4 app log lines per tick to simulate burst activity.
            var count = 1 + _rng.Next(0, 4);
            for (var i = 0; i < count; i++)
                WriteAppLine(t, now.AddSeconds(-_rng.NextDouble() * _gen.WriteIntervalSeconds));
        }
    }

    // ── Metrics helpers ───────────────────────────────────────────────────────

    private void AdvanceMetrics(MetricsTarget t)
    {
        t.Current = Math.Clamp(t.Current + Mb(_rng.Next(-20, 41)), Mb(50), Mb(2048));
        t.Peak = Math.Max(t.Peak, t.Current);
        t.Child = _rng.Next(0, 5) == 0 ? Mb(_rng.Next(0, 50)) : 0;
        t.ChildPeak = Math.Max(t.ChildPeak, t.Child);
    }

    private void WriteMetricsLine(MetricsTarget t, DateTime ts)
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
        AppendLine(t.FilePath, line);
    }

    // ── App log helpers ───────────────────────────────────────────────────────

    private void WriteAppLine(AppLogTarget t, DateTime ts)
    {
        // Weighted level distribution: 60% INFO, 20% DEBUG, 12% WARN, 8% ERROR
        var roll = _rng.Next(100);
        string level, message, caller;
        IEnumerable<string>? extras = null;

        if (roll < 60)
        {
            level = "INFO";
            message = Fill(Pick(InfoMessages), t);
            caller = Pick(Callers);
        }
        else if (roll < 80)
        {
            level = "DEBUG";
            message = Fill(Pick(DebugMessages), t);
            caller = Pick(Callers);
        }
        else if (roll < 92)
        {
            level = "WARN";
            message = Fill(Pick(WarnMessages), t);
            caller = Pick(Callers);
        }
        else
        {
            level = "ERROR";
            var errMsg = Fill(Pick(ErrorMessages), t);
            var exType = Pick(ExceptionTypes);
            var lineNo = 40 + _rng.Next(200);
            message = errMsg;
            caller = Pick(Callers);
            extras =
            [
                $"   {exType}: {errMsg}",
                $"   at {t.Service}.{caller}() in {t.Service}.cs:line {lineNo}",
                $"   at System.Threading.Tasks.Task.Run(Func<Task> function)",
            ];
        }

        var tid = 10 + _rng.Next(90);
        var main = $"{ts:yyyy-MM-dd HH:mm:ss.ffff}|{level}|{t.Server}|{t.Pid}|{tid}|{message}|{caller}";
        var lines = extras is null
            ? main
            : main + "\n" + string.Join("\n", extras);

        AppendLine(t.FilePath, lines);
    }

    private string Fill(string template, AppLogTarget t) =>
        template
            .Replace("{ms}", (10 + _rng.Next(990)).ToString())
            .Replace("{b}", _rng.Next(50).ToString())
            .Replace("{n}", _rng.Next(1000).ToString())
            .Replace("{host}", t.Server)
            .Replace("{port}", "9092")
            .Replace("{k}", "key-" + _rng.Next(999))
            .Replace("{t}", "orders")
            .Replace("{op}", "db-write")
            .Replace("{p}", _rng.Next(4).ToString())
            .Replace("{r}", Pick(Resources))
            .Replace("{m}", "12")
            .Replace("{c}", Pick(Components))
            .Replace("{dep}", Pick(Components))
            .Replace("{src}", "appsettings.json")
            .Replace("{id}", RandomHex(8))
            .Replace("{caller}", Pick(Callers));

    private static void AppendLine(string path, string content)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sw = new StreamWriter(fs);
            sw.WriteLine(content);
        }
        catch { /* best-effort */ }
    }

    private T Pick<T>(T[] arr) => arr[_rng.Next(arr.Length)];

    private static long Mb(int mb) => (long)mb * 1024 * 1024;

    private string RandomHex(int len)
    {
        const string hex = "0123456789abcdef";
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = hex[_rng.Next(16)];
        return new string(chars);
    }
}
