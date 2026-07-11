namespace NxtUI.Core.Events;

/// <summary>
/// Friendly producer-side helper a run uses to report what it's doing, without hand-building
/// <see cref="RunEvent"/>s. It stamps the actor identity (service/server/pid) once, generates
/// ids/timestamps, and forwards to an <see cref="IRunEventSink"/>.
///
/// <para><b>Template — a generic run reporting its flow:</b></para>
/// <code>
/// var reporter = new RunEventReporter(sink, runId: "RUN-42", service: "OrderLoader");
/// await reporter.ConnectAsync();                                  // service is up
///
/// // A unit of work as a span (start now, finish on dispose):
/// await using (var act = reporter.BeginActivity("ParseOrders", correlationId: "BATCH-7"))
/// {
///     await reporter.ConsumeAsync("orders.in", correlationId: "BATCH-7", recordCount: 5000);
///     // ... do work ...
///     await reporter.ProduceAsync("orders.parsed", correlationId: "BATCH-7", recordCount: 4980);
///     act.RecordCount = 4980;                                     // captured on the finish span
/// }                                                              // → Process span emitted here
///
/// try { /* risky step */ }
/// catch (Exception ex) { await reporter.ErrorAsync(ex.Message, correlationId: "BATCH-7", source: "DbWrite", target: "dbo.Orders"); }
///
/// await reporter.DisconnectAsync(EventStatus.Success);           // service done
/// </code>
/// </summary>
public sealed class RunEventReporter(
    IRunEventSink sink,
    string runId,
    string service,
    string? server = null,
    int? processId = null,
    string pipeline = "")
{
    private readonly string _server = server ?? Environment.MachineName;
    private readonly int _pid = processId ?? Environment.ProcessId;

    public string Pipeline { get; init; } = pipeline;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public Task ConnectAsync(string? info = null, CancellationToken ct = default) =>
        EmitAsync(EventKind.Connect, info: info, ct: ct);

    public Task DisconnectAsync(EventStatus status = EventStatus.Success, string? info = null, CancellationToken ct = default) =>
        EmitAsync(EventKind.Disconnect, status: status, info: info, ct: ct);

    // ── Messages ────────────────────────────────────────────────────────────────

    public Task ProduceAsync(string target, string? correlationId = null, long? recordCount = null, string? source = null, string? info = null, CancellationToken ct = default) =>
        EmitAsync(EventKind.Produce, correlationId: correlationId, target: target, recordCount: recordCount, source: source, info: info, ct: ct);

    public Task ConsumeAsync(string target, string? correlationId = null, long? recordCount = null, string? source = null, string? info = null, CancellationToken ct = default) =>
        EmitAsync(EventKind.Consume, correlationId: correlationId, target: target, recordCount: recordCount, source: source, info: info, ct: ct);

    // ── Notes & failures ─────────────────────────────────────────────────────────

    public Task InfoAsync(string info, string? correlationId = null, string? source = null, CancellationToken ct = default) =>
        EmitAsync(EventKind.Info, correlationId: correlationId, source: source, info: info, ct: ct);

    public Task ErrorAsync(string message, string? correlationId = null, string? source = null, string? target = null, CancellationToken ct = default) =>
        EmitAsync(EventKind.Error, correlationId: correlationId, source: source, target: target, status: EventStatus.Failed, error: message, ct: ct);

    // ── Work spans ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begins a <see cref="EventKind.Process"/> span. Emits nothing until disposed, then emits
    /// one span event covering start→finish. Set <see cref="Activity.RecordCount"/> /
    /// <see cref="Activity.Fail"/> before disposal to enrich the finish.
    /// </summary>
    public Activity BeginActivity(string source, string? correlationId = null, string? target = null) =>
        new(this, source, correlationId, target);

    // ── Core emit ──────────────────────────────────────────────────────────────

    private Task EmitAsync(
        EventKind kind,
        string? correlationId = null,
        string? source = null,
        string? target = null,
        EventStatus status = EventStatus.None,
        long? recordCount = null,
        string? error = null,
        string? info = null,
        DateTime? timestampUtc = null,
        DateTime? finishUtc = null,
        CancellationToken ct = default)
    {
        var evt = new RunEvent
        {
            RunId = runId,
            CorrelationId = correlationId,
            Service = service,
            Server = _server,
            ProcessId = _pid,
            Pipeline = Pipeline,
            Kind = kind,
            Source = source ?? string.Empty,
            Target = target,
            TimestampUtc = timestampUtc ?? DateTime.UtcNow,
            FinishUtc = finishUtc,
            Status = status,
            RecordCount = recordCount,
            Error = error,
            Info = info,
        };
        return sink.WriteAsync([evt], ct);
    }

    /// <summary>A pending work span. Emitted as a single Process event when disposed.</summary>
    public sealed class Activity : IAsyncDisposable
    {
        private readonly RunEventReporter _reporter;
        private readonly string _source;
        private readonly string? _correlationId;
        private readonly string? _target;
        private readonly DateTime _startUtc = DateTime.UtcNow;

        public long? RecordCount { get; set; }
        public string? Error { get; private set; }

        internal Activity(RunEventReporter reporter, string source, string? correlationId, string? target)
        {
            _reporter = reporter;
            _source = source;
            _correlationId = correlationId;
            _target = target;
        }

        /// <summary>Mark the span as failed; the emitted Process event carries the message and Failed status.</summary>
        public void Fail(string message) => Error = message;

        public ValueTask DisposeAsync() => new(_reporter.EmitAsync(
            EventKind.Process,
            correlationId: _correlationId,
            source: _source,
            target: _target,
            status: Error is not null ? EventStatus.Failed : EventStatus.Success,
            recordCount: RecordCount,
            error: Error,
            timestampUtc: _startUtc,
            finishUtc: DateTime.UtcNow));
    }
}
