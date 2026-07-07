using Microsoft.Extensions.Options;
using NxtUI.Core.Configuration;
using NxtUI.Core.Logging;
using NxtUI.Core.Models;
using NxtUI.Core.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NxtUI.Web.Services;

/// <summary>
/// Background metrics monitor. Per environment, memory metrics come from two sources
/// merged together:
///
///   1. Kafka (primary, live): each environment's own Kafka cluster has a single-partition,
///      low-retention topic (KafkaSettings.MetricsTopicName, default "metrics") that every
///      service instance publishes protobuf-encoded MetricsTracker samples to. This is
///      tailed continuously from the moment an env gets its first subscriber.
///
///   2. Disk-based log parsing (backfill only): the first time a service is seen on Kafka,
///      if there's a gap between the process's start time and the earliest timestamp Kafka's
///      retention still has, that gap is filled once from the service's on-disk metrics log
///      file. After that one-time backfill, the service is no longer touched by the file
///      poller — Kafka is the sole ongoing source for it.
///
/// If an environment's Kafka topic has no data at all (not yet wired up there), this falls
/// back entirely to the original continuous file-polling behavior for that environment.
///
/// Polling is paused when an environment has no subscribers, but cached data (file offsets
/// and sample history) is kept for <c>IdleReleaseMinutes</c> so that re-subscribing resumes
/// from where it left off — no redundant file re-reads or Kafka replays.
///
/// Service lists come from <see cref="IHeartbeatMonitor"/> (already cached) so this service
/// never hits MongoDB directly.
/// </summary>
public sealed class ServiceMetricsMonitor : BackgroundService, IServiceMetricsMonitor
{
    private const int MaxHistory = 1000;
    private const int MaxParallel = 48;   // max concurrent file reads across all envs

    private readonly IHeartbeatMonitor _heartbeatMonitor;
    private readonly ILogPathDiscoveryService _discovery;
    private readonly LogPathSettings _paths;
    private readonly IKafkaMonitor _kafka;
    private readonly KafkaSettings _kafkaSettings;
    private readonly ILogger<ServiceMetricsMonitor> _log;

    // Limits simultaneous network file reads to avoid overwhelming the servers.
    private readonly SemaphoreSlim _ioGate = new(MaxParallel, MaxParallel);

    private readonly object _subLock = new();
    private readonly Dictionary<string, int> _envRefs = new();   // env -> subscriber count
    private readonly Dictionary<string, DateTime> _envIdle = new();   // env -> idle-since (no subscribers)
    private readonly Dictionary<string, IDisposable> _hbSubs = new();   // env -> HeartbeatMonitor subscription
    private readonly Dictionary<string, CancellationTokenSource> _kafkaCts = new();   // env -> Kafka consumer lifetime
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _envGates = new();
    // env -> topic has yielded at least one MetricsTracker message. Unset/false means
    // "no Kafka data for this env" — the file poller keeps running unconditionally for it.
    private readonly ConcurrentDictionary<string, bool> _kafkaHasData = new();
    private CancellationToken _ct = CancellationToken.None;

    public event Action<string>? OnMetricsUpdated;

    public ServiceMetricsMonitor(
        IHeartbeatMonitor heartbeatMonitor,
        ILogPathDiscoveryService discovery,
        IOptions<LogPathSettings> paths,
        IKafkaMonitor kafka,
        IOptions<KafkaSettings> kafkaSettings,
        ILogger<ServiceMetricsMonitor> log)
    {
        _heartbeatMonitor = heartbeatMonitor;
        _discovery = discovery;
        _paths = paths.Value;
        _kafka = kafka;
        _kafkaSettings = kafkaSettings.Value;
        _log = log;

        // A freshly resolved path means there may be a file to read right away —
        // poll that env immediately instead of waiting for the next interval.
        _discovery.OnPathResolved += OnPathResolved;
    }

    private void OnPathResolved(string cacheKey)
    {
        var env = cacheKey.Split('|', 2)[0]; // CacheKey is "env|host|service|pid"
        if (HasSubscribers(env))
            _ = PollEnvAsync(env, _ct);
    }

    public override void Dispose()
    {
        _discovery.OnPathResolved -= OnPathResolved;
        base.Dispose();
    }

    private sealed class Entry
    {
        public readonly object Sync = new();
        public string? FilePath;    // cached once discovered; never re-searched
        public long Offset;
        public DateTime LastWriteUtc = DateTime.MinValue;
        public MetricsSample? Latest;
        public readonly List<MetricsSample> History = new();
        // Set once the one-time disk backfill (for the pre-Kafka-retention gap) has run
        // for this service, whether or not it actually found a gap to fill. Once true and
        // Kafka has data for the env, the file poller stops touching this entry entirely.
        public bool DiskBackfilled;
    }

    // ── Subscription ───────────────────────────────────────────────────────────

    public IDisposable Subscribe(string env)
    {
        lock (_subLock)
        {
            _envRefs[env] = _envRefs.TryGetValue(env, out var n) ? n + 1 : 1;
            _envIdle.Remove(env); // no longer idle
            // Keep a HeartbeatMonitor subscription alive for this env so it keeps polling.
            if (!_hbSubs.ContainsKey(env))
                _hbSubs[env] = _heartbeatMonitor.Subscribe(env);

            // Start (or resume) the Kafka metrics consumer for this env.
            if (!_kafkaCts.ContainsKey(env))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                _kafkaCts[env] = cts;
                _ = RunKafkaConsumerAsync(env, cts.Token);
            }
        }

        // Poll immediately so a freshly opened tab doesn't wait a full interval.
        _ = PollEnvAsync(env, _ct);
        return new Subscription(this, env);
    }

    private void Release(string env)
    {
        lock (_subLock)
        {
            if (!_envRefs.TryGetValue(env, out var n)) return;
            if (n <= 1)
            {
                _envRefs.Remove(env);
                _envIdle[env] = DateTime.UtcNow;
                // Release the HeartbeatMonitor subscription; its own idle window keeps it cached.
                if (_hbSubs.TryGetValue(env, out var hbSub))
                {
                    hbSub.Dispose();
                    _hbSubs.Remove(env);
                }
            }
            else
                _envRefs[env] = n - 1;
        }
    }

    private bool HasSubscribers(string env)
    {
        lock (_subLock) return _envRefs.ContainsKey(env);
    }

    private string[] SubscribedEnvs()
    {
        lock (_subLock) return _envRefs.Keys.ToArray();
    }

    // ── Reads ──────────────────────────────────────────────────────────────────

    public MetricsSample? GetLatest(string env, ServiceStatus svc)
    {
        if (_entries.TryGetValue(LogPathDiscoveryService.CacheKey(svc, env), out var e))
            lock (e.Sync) return e.Latest;
        return null;
    }

    public IReadOnlyList<MetricsSample> GetHistory(string env, ServiceStatus svc)
    {
        if (_entries.TryGetValue(LogPathDiscoveryService.CacheKey(svc, env), out var e))
            lock (e.Sync) return e.History.ToList();
        return Array.Empty<MetricsSample>();
    }

    // ── Polling loop ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ct = ct;
        var interval = TimeSpan.FromSeconds(Math.Max(5, _paths.MetricsIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                CleanupIdleEnvs();
                await Task.WhenAll(SubscribedEnvs().Select(env => PollEnvAsync(env, ct)));
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private void CleanupIdleEnvs()
    {
        var idleTimeout = TimeSpan.FromMinutes(_paths.IdleReleaseMinutes);
        var now = DateTime.UtcNow;

        List<string> toRelease;
        lock (_subLock)
            toRelease = _envIdle.Where(kv => now - kv.Value > idleTimeout)
                                .Select(kv => kv.Key).ToList();

        foreach (var env in toRelease)
        {
            lock (_subLock)
            {
                _envIdle.Remove(env);
                if (_kafkaCts.TryGetValue(env, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    _kafkaCts.Remove(env);
                }
            }
            foreach (var key in _entries.Keys
                         .Where(k => k.StartsWith(env + "|", StringComparison.Ordinal)).ToList())
                _entries.TryRemove(key, out _);
            _kafkaHasData.TryRemove(env, out _);
            _discovery.ClearEnv(env);
            _log.LogInformation("metrics [{Env}]: idle cache released", env);
        }
    }

    // Coalesced rerun flag: set when a trigger (timer / subscribe / path-resolved)
    // arrives while a poll for that env is already running. Instead of dropping
    // the trigger outright, the in-flight poll checks this after it finishes and
    // runs once more immediately — so a burst of triggers during a busy poll
    // results in exactly one extra pass, not zero (silently dropped) and not N
    // (queued/piled up).
    private readonly ConcurrentDictionary<string, bool> _envRerunPending = new();

    private async Task PollEnvAsync(string env, CancellationToken ct)
    {
        if (!HasSubscribers(env)) return;
        if (string.IsNullOrWhiteSpace(_paths.MetricsFileName)) return;

        var gate = _envGates.GetOrAdd(env, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            _envRerunPending[env] = true;
            return;
        }

        try
        {
            do
            {
                _envRerunPending[env] = false;
                await PollEnvOnceAsync(env, ct);
            }
            while (_envRerunPending.TryGetValue(env, out var pending) && pending && HasSubscribers(env));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PollEnvOnceAsync(string env, CancellationToken ct)
    {
        var services = _heartbeatMonitor.GetServices(env);
        if (services is null)
        {
            _log.LogDebug("metrics [{Env}]: heartbeat cache empty, skipping", env);
            return;
        }

        _log.LogDebug("metrics [{Env}]: polling {Count} services", env, services.Count);

        var parsed = 0;

        var kafkaLive = _kafkaHasData.TryGetValue(env, out var hasData) && hasData;

        await Task.WhenAll(services.Select(async svc =>
        {
            var key = LogPathDiscoveryService.CacheKey(svc, env);
            var entry = _entries.GetOrAdd(key, _ => new Entry());

            // Kafka is confirmed live for this env and this service already got its
            // one-time disk backfill — Kafka is now the sole ongoing source for it.
            if (kafkaLive)
            {
                bool backfilled;
                lock (entry.Sync) backfilled = entry.DiskBackfilled;
                if (backfilled) return;
            }

            // Resolve and cache the file path once; never re-search after that.
            if (entry.FilePath is null)
            {
                var folder = _discovery.GetCachedPath(svc, env);
                if (folder is null) { _discovery.EnsureDiscovering(svc, env); return; }
                entry.FilePath = Path.Combine(folder, _paths.MetricsFileName);
            }

            var file = entry.FilePath;

            // Cheap stat — skip the read entirely if the file hasn't changed.
            // Runs unthrottled because a stat is far cheaper than a read.
            DateTime mtime;
            try
            {
                mtime = await Task.Run(() => new FileInfo(file).LastWriteTimeUtc, ct);
            }
            catch { return; }

            lock (entry.Sync)
            {
                if (mtime <= entry.LastWriteUtc) return;
            }

            // File changed — throttle concurrent reads to avoid saturating servers.
            long offset;
            lock (entry.Sync) offset = entry.Offset;

            await _ioGate.WaitAsync(ct);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IncrementalFileReader.Result result;
            try { result = await Task.Run(() => IncrementalFileReader.ReadNew(file, offset), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "metrics: read failed {File} after {Ms}ms", file, sw.ElapsedMilliseconds);
                return;
            }
            finally { _ioGate.Release(); }

            if (sw.Elapsed >= OperationLog.SlowThreshold)
                _log.LogWarning("metrics: read+parse {File} completed in {Ms}ms (slow), {Count} line(s)",
                    file, sw.ElapsedMilliseconds, result.Lines.Count);

            if (result.Lines.Count == 0)
            {
                lock (entry.Sync) entry.LastWriteUtc = mtime; // mark seen even with no new metrics lines
                return;
            }

            var parsedAny = false;
            lock (entry.Sync)
            {
                entry.Offset = result.NewOffset;
                entry.LastWriteUtc = mtime;
                foreach (var line in result.Lines)
                {
                    if (!MetricsLogParser.TryParse(line, out var sample) || sample is null) continue;
                    entry.Latest = sample;
                    entry.History.Add(sample);
                    if (entry.History.Count > MaxHistory)
                        entry.History.RemoveRange(0, entry.History.Count - MaxHistory);
                    parsedAny = true;
                    Interlocked.Increment(ref parsed);
                }
            }

            // Report this service's update the moment it's parsed, instead of
            // waiting for every other service in the env to finish reading too.
            if (parsedAny) OnMetricsUpdated?.Invoke(env);
        }));

        if (parsed > 0)
            _log.LogDebug("metrics [{Env}]: {Parsed} new samples parsed", env, parsed);

        // Prune entries for services no longer reported by heartbeat.
        var activeKeys = services.Select(svc => LogPathDiscoveryService.CacheKey(svc, env)).ToHashSet();
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(env + "|", StringComparison.Ordinal) && !activeKeys.Contains(k)).ToList())
        {
            _entries.TryRemove(key, out _);
            _log.LogDebug("metrics [{Env}]: dropped stale entry {Key}", env, key);
        }
        _discovery.PruneStaleEntries(env, activeKeys);
    }

    // ── Kafka metrics consumer ──────────────────────────────────────────────────

    private async Task RunKafkaConsumerAsync(string env, CancellationToken ct)
    {
        var topic = _kafkaSettings.MetricsTopicName;
        if (string.IsNullOrWhiteSpace(topic)) return;

        // Seek from the oldest currently-known service's start time instead of the topic's
        // true beginning. OffsetsForTimes resolves the correct starting offset directly on
        // the broker — it doesn't scan — so this is cheap regardless of retention length,
        // and stays correct (worst case: replays a bit more than strictly necessary) even
        // if retention later grows from a few hours to a week.
        //
        // The heartbeat cache is warmed by the SAME Subscribe() call that kicked off this
        // consumer (see Subscribe() above), via a fire-and-forget Mongo poll — so on an env's
        // very first subscribe, GetServices(env) can easily still be null here purely because
        // that poll hasn't returned yet, not because there's really no heartbeat data. Wait
        // briefly for it instead of immediately falling back to Default (effectively "from
        // latest"), which would silently miss messages published between subscribe and the
        // heartbeat poll actually completing.
        var services = _heartbeatMonitor.GetServices(env) ?? await WaitForHeartbeatAsync(env, ct);
        var oldestStart = services?.Count > 0 ? services.Min(s => s.CreatedDateTime) : (DateTime?)null;
        var directive = oldestStart.HasValue
            ? new KafkaSeekDirective { TimestampFrom = oldestStart.Value }
            : KafkaSeekDirective.Default;

        _log.LogInformation(
            "metrics [{Env}]: starting Kafka consumer for topic '{Topic}' (seek={@Directive}) — " +
            "until the first matching message arrives, this env is served from disk-log polling",
            env, topic, directive);

        var otherTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var first = true;
            var messageCount = 0;
            await foreach (var msg in _kafka.TailTopicAsync(env, topic, directive, ct, uncapped: true))
            {
                if (ct.IsCancellationRequested) break;
                messageCount++;

                if (!string.Equals(msg.PayloadType, "MetricsTracker", StringComparison.OrdinalIgnoreCase))
                {
                    if (otherTypes.Add(msg.PayloadType ?? "(null)"))
                        _log.LogDebug(
                            "metrics [{Env}]: topic '{Topic}' also carries payload type '{Type}' — ignored (not MetricsTracker)",
                            env, topic, msg.PayloadType);
                    continue;
                }

                _kafkaHasData[env] = true;
                if (first)
                {
                    _log.LogInformation("metrics [{Env}]: Kafka topic '{Topic}' has data, earliest={Ts:HH:mm:ss} — env is now served from Kafka", env, topic, msg.Timestamp);
                    first = false;
                }

                await HandleMetricsMessageAsync(env, msg, ct);
            }

            // The tail loop only returns without throwing when the topic run ends on its own
            // (e.g. broker closed the connection) rather than via cancellation — that's not
            // supposed to happen for a continuous "uncapped" tail, so it's worth flagging:
            // metrics for this env silently fall back to disk polling until the next subscribe.
            if (!ct.IsCancellationRequested)
                _log.LogWarning(
                    "metrics [{Env}]: Kafka consumer for topic '{Topic}' ended unexpectedly after {Count} message(s) " +
                    "without being cancelled — this env now falls back to disk-log polling until re-subscribed",
                    env, topic, messageCount);
        }
        catch (OperationCanceledException) { /* unsubscribed / shutting down */ }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "metrics [{Env}]: Kafka consumer for topic '{Topic}' failed — this env falls back to disk-log " +
                "polling until re-subscribed (check broker connectivity/credentials for this environment)",
                env, topic);
        }
    }

    // How long to wait for HeartbeatMonitor's warm-up poll before giving up and treating
    // this env as genuinely having no heartbeat data yet.
    private static readonly TimeSpan HeartbeatWaitTimeout = TimeSpan.FromSeconds(10);

    private async Task<IReadOnlyList<ServiceStatus>?> WaitForHeartbeatAsync(string env, CancellationToken ct)
    {
        var services = _heartbeatMonitor.GetServices(env);
        if (services is not null) return services;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(string updatedEnv)
        {
            if (string.Equals(updatedEnv, env, StringComparison.OrdinalIgnoreCase)) tcs.TrySetResult();
        }

        _heartbeatMonitor.OnServicesUpdated += OnUpdated;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(HeartbeatWaitTimeout);
            try { await tcs.Task.WaitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug(
                    "metrics [{Env}]: no heartbeat data after waiting {Timeout}s at Kafka consumer startup — " +
                    "seeking from Default instead of oldest-service-start", env, HeartbeatWaitTimeout.TotalSeconds);
            }
        }
        finally
        {
            _heartbeatMonitor.OnServicesUpdated -= OnUpdated;
        }

        return _heartbeatMonitor.GetServices(env);
    }

    private async Task HandleMetricsMessageAsync(string env, KafkaMessage msg, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(msg.JsonPayload ?? "{}"); }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "metrics [{Env}]: could not parse MetricsTracker JSON payload (offset={Offset}, partition={Partition})",
                env, msg.Offset, msg.Partition);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var hostName    = GetStringCI(root, "HostName");
            var serviceName = GetStringCI(root, "ServiceName");
            var instanceId  = GetStringCI(root, "ServiceInstanceId");
            if (hostName is null || serviceName is null || instanceId is null)
            {
                _log.LogWarning(
                    "metrics [{Env}]: MetricsTracker message missing HostName/ServiceName/ServiceInstanceId " +
                    "(offset={Offset}, partition={Partition}) — dropped",
                    env, msg.Offset, msg.Partition);
                return;
            }

            // Kafka carries no ProcessId — join against the heartbeat-reported instance by
            // (host, service, instance id) to find the same ServiceStatus/Entry the file
            // poller already uses. If heartbeat hasn't reported this instance yet, skip;
            // a later message will match once it does.
            var services = _heartbeatMonitor.GetServices(env);
            var svc = services?.FirstOrDefault(s =>
                string.Equals(s.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ServiceInstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            if (svc is null)
            {
                _log.LogDebug(
                    "metrics [{Env}]: no heartbeat match yet for {Service}@{Host} (instance={Instance}) — " +
                    "message skipped, will retry once heartbeat reports this instance",
                    env, serviceName, hostName, instanceId);
                return;
            }

            var startStr = GetStringCI(root, "ProcessStartTime");
            DateTime.TryParse(startStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var processStartUtc);

            var sample = new MetricsSample
            {
                Timestamp           = msg.Timestamp,
                CurrentUsageBytes   = GetInt64CI(root, "CurrentMemoryUsageByte"),
                PeakUsageBytes      = GetInt64CI(root, "PeakMemoryUsageByte"),
                ChildUsageBytes     = GetInt64CI(root, "ChildProcessesMemoryUsageByte"),
                ChildPeakUsageBytes = GetInt64CI(root, "ChildProcessesPeakMemoryUsageByte"),
            };

            var key   = LogPathDiscoveryService.CacheKey(svc, env);
            var entry = _entries.GetOrAdd(key, _ => new Entry());

            bool needsBackfill;
            lock (entry.Sync)
            {
                UpsertSample(entry, sample);
                needsBackfill = !entry.DiskBackfilled;
            }

            if (needsBackfill && processStartUtc != default)
                await BackfillFromDiskAsync(env, svc, entry, processStartUtc, sample.Timestamp, ct);

            OnMetricsUpdated?.Invoke(env);
        }
    }

    /// <summary>
    /// One-time fill of the gap between a process's start time and the earliest sample
    /// Kafka's (low-retention) topic still has, read from that service's on-disk metrics
    /// log file. Runs at most once per service — see Entry.DiskBackfilled.
    /// </summary>
    private async Task BackfillFromDiskAsync(
        string env, ServiceStatus svc, Entry entry, DateTime processStartUtc, DateTime kafkaEarliestUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_paths.MetricsFileName))
        {
            lock (entry.Sync) entry.DiskBackfilled = true;
            return;
        }

        var folder = _discovery.GetCachedPath(svc, env);
        if (folder is null)
        {
            _discovery.EnsureDiscovering(svc, env);
            return; // retry on the next Kafka message for this service, once the path resolves
        }

        var file = Path.Combine(folder, _paths.MetricsFileName);

        await _ioGate.WaitAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IncrementalFileReader.Result result;
        try { result = await Task.Run(() => IncrementalFileReader.ReadNew(file, 0), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "metrics [{Env}]: backfill read failed {File} after {Ms}ms", env, file, sw.ElapsedMilliseconds);
            lock (entry.Sync) entry.DiskBackfilled = true; // don't retry forever on a broken file
            return;
        }
        finally { _ioGate.Release(); }

        var filled = 0;
        lock (entry.Sync)
        {
            foreach (var line in result.Lines)
            {
                if (!MetricsLogParser.TryParse(line, out var sample) || sample is null) continue;
                if (sample.Timestamp < processStartUtc || sample.Timestamp >= kafkaEarliestUtc) continue;
                UpsertSample(entry, sample);
                filled++;
            }

            // Hand off our read position to the file poller — without this, PollEnvOnceAsync
            // doesn't know this file was already read to the end and re-parses it from byte 0
            // the next time it touches this entry, duplicating a potentially large (tens of
            // thousands of lines) synchronous parse. This was the actual bug: real-world
            // metrics files can be big, and doing that parse twice is exactly the kind of
            // CPU-bound work on the shared thread pool that starves out SignalR's keep-alive
            // processing and shows up to users as "attempting to reconnect".
            entry.FilePath     = file;
            entry.Offset       = result.NewOffset;
            entry.LastWriteUtc = DateTime.UtcNow;
            entry.DiskBackfilled = true;
        }

        _log.LogInformation(
            "metrics [{Env}]: backfilled {Filled} samples for {Service}@{Host} from disk in {Ms}ms, range [{Start:HH:mm:ss}, {End:HH:mm:ss})",
            env, filled, svc.ServiceName, svc.HostName, sw.ElapsedMilliseconds, processStartUtc, kafkaEarliestUtc);
    }

    /// <summary>Inserts/replaces by timestamp, keeps History sorted ascending, caps at MaxHistory.</summary>
    private static void UpsertSample(Entry entry, MetricsSample sample)
    {
        var idx = entry.History.FindIndex(s => s.Timestamp == sample.Timestamp);
        if (idx >= 0)
        {
            entry.History[idx] = sample;
        }
        else
        {
            var insertAt = entry.History.FindIndex(s => s.Timestamp > sample.Timestamp);
            if (insertAt < 0) entry.History.Add(sample);
            else entry.History.Insert(insertAt, sample);
        }

        if (entry.History.Count > MaxHistory)
            entry.History.RemoveRange(0, entry.History.Count - MaxHistory);

        if (entry.Latest is null || sample.Timestamp >= entry.Latest.Timestamp)
            entry.Latest = sample;
    }

    private static string? GetStringCI(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    private static long GetInt64CI(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch
                {
                    JsonValueKind.Number => p.Value.GetInt64(),
                    JsonValueKind.String when long.TryParse(p.Value.GetString(), out var v) => v,
                    _ => 0,
                };
        return 0;
    }

    private sealed class Subscription : IDisposable
    {
        private ServiceMetricsMonitor? _owner;
        private readonly string _env;

        public Subscription(ServiceMetricsMonitor owner, string env) { _owner = owner; _env = env; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_env);
    }
}
