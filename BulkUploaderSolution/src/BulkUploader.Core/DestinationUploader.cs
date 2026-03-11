using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BulkUploader.Core;

/// <summary>
/// Abstract base class for a single-destination bulk uploader.
///
/// ────────────────────────────────────────────────────────────────────────────
/// PIPELINE OVERVIEW
/// ────────────────────────────────────────────────────────────────────────────
///
///   Chunk processors (N concurrent threads / tasks)
///       │
///       │  await EnqueueJobAsync(() => MyAsyncEnumerable())
///       │                              ↑ producer runs lazily inside stage 1
///       ▼
///   ┌─────────────────────────────────────────────────────────────────────┐
///   │  Channel 1 — job channel       (bounded, capacity: JobChannelCap.) │
///   │  Payload: EnqueuedJob { Producer, JobToken }                        │
///   │  Backpressure: EnqueueJobAsync blocks when full                     │
///   └──────────────────────────────┬──────────────────────────────────────┘
///                                  │  Stage 1 — Producer task (single)
///                                  │  Reads jobs, iterates IAsyncEnumerable lazily.
///                                  │  Calls IncrementEnqueued() before each write.
///                                  │  Calls ProducerDone() when iterator exhausted.
///                                  ▼
///   ┌─────────────────────────────────────────────────────────────────────┐
///   │  Channel 2 — record channel    (bounded, capacity: RecordChanCap.) │
///   │  Payload: RecordEnvelope { Record, JobToken }  (struct, no alloc)   │
///   │  Backpressure: producer blocks when full                            │
///   └──────────────────────────────┬──────────────────────────────────────┘
///                                  │  Stage 2 — Batcher task (single)
///                                  │  Accumulates records into a buffer.
///                                  │  Flush triggers:
///                                  │    (a) buffer.Count >= Tuner.BatchSize
///                                  │    (b) no new record for FlushAfterIdleMs
///                                  │  Tracks per-job counts per batch.
///                                  ▼
///   ┌─────────────────────────────────────────────────────────────────────┐
///   │  Channel 3 — batch channel     (bounded, capacity: BatchChanCap.)  │
///   │  Payload: ReadyBatch { Records, JobCounts }                         │
///   │  Backpressure: batcher blocks when uploader is slow                 │
///   └──────────────────────────────┬──────────────────────────────────────┘
///                                  │  Stage 3 — Uploader task (single)
///                                  │  Calls ConnectAsync / DisconnectAsync.
///                                  │  Calls UploadBatchAsync with retry + back-off.
///                                  │  Calls token.NotifyUploaded per job in batch.
///                                  │  Adjusts BatchSize via BatchSizeTuner.
///                                  ▼
///                           Destination (SQL / Mongo / custom)
///
/// ────────────────────────────────────────────────────────────────────────────
/// COMPLETION TRACKING
/// ────────────────────────────────────────────────────────────────────────────
/// Records from different jobs interleave freely. Each envelope carries its
/// JobToken. After upload the uploader calls token.NotifyUploaded(n) for every
/// job represented in that batch. The TCS resolves when ProducerDone AND
/// uploaded >= enqueued, unblocking the chunk processor's await.
///
/// ────────────────────────────────────────────────────────────────────────────
/// LIFECYCLE HOOKS FOR SUBCLASSES
/// ────────────────────────────────────────────────────────────────────────────
/// Override the following in the correct order:
///
///   1. OnInitializingAsync  — called once at construction before the pipeline
///                             starts; validate config, allocate shared resources.
///   2. ConnectAsync         — called before the first batch and after idle
///                             disconnect; open connection / acquire handle.
///   3. UploadBatchAsync     — perform the actual bulk insert; throw on failure.
///   4. OnBatchSucceededAsync — optional hook after a successful upload.
///   5. OnBatchFailedAsync   — optional hook after permanent failure (all retries
///                             exhausted); logging already done by base class.
///   6. DisconnectAsync      — close the connection; called on idle timeout and
///                             during shutdown.
///   7. OnShutdownAsync      — called once during DisposeAsync, after the pipeline
///                             has drained; release shared resources.
/// </summary>
public abstract class DestinationUploader<T> : IAsyncDisposable
{
    // ── Channels ──────────────────────────────────────────────────────────────
    private readonly Channel<EnqueuedJob<T>>     _jobChannel;
    private readonly Channel<RecordEnvelope<T>>  _recordChannel;
    private readonly Channel<ReadyBatch<T>>      _batchChannel;

    // ── Pipeline tasks ────────────────────────────────────────────────────────
    private readonly Task _producerTask;
    private readonly Task _batcherTask;
    private readonly Task _uploaderTask;

    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;

    // ── Public surface ────────────────────────────────────────────────────────

    protected ILogger           Logger          { get; }
    public    UploaderParameters Parameters     { get; }
    public    BatchSizeTuner    Tuner           { get; }
    public    string            DestinationName { get; }

    // ─────────────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────────────

    protected DestinationUploader(
        string             destinationName,
        UploaderParameters parameters,
        BatchSizeTuner     tuner,
        ILogger            logger)
    {
        DestinationName = destinationName;
        Parameters      = parameters;
        Tuner           = tuner;
        Logger          = logger;

        _jobChannel = Channel.CreateBounded<EnqueuedJob<T>>(new BoundedChannelOptions(parameters.JobChannelCapacity)
        {
            SingleWriter                  = false,
            SingleReader                  = true,
            FullMode                      = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

        _recordChannel = Channel.CreateBounded<RecordEnvelope<T>>(new BoundedChannelOptions(parameters.RecordChannelCapacity)
        {
            SingleWriter                  = true,
            SingleReader                  = true,
            FullMode                      = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

        _batchChannel = Channel.CreateBounded<ReadyBatch<T>>(new BoundedChannelOptions(parameters.BatchChannelCapacity)
        {
            SingleWriter                  = true,
            SingleReader                  = true,
            FullMode                      = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

        // Pipeline tasks are started after the virtual OnInitializingAsync hook,
        // which must be called explicitly via InitializeAsync() by factory methods
        // or the registry. We start tasks here for simplicity — subclasses that
        // need async init should use a factory pattern and call InitializeAsync().
        var ct = _disposeCts.Token;
        _producerTask = Task.Run(() => RunProducerAsync(ct));
        _batcherTask  = Task.Run(() => RunBatcherAsync(ct));
        _uploaderTask = Task.Run(() => RunUploaderAsync(ct));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues a streaming job and asynchronously waits until every record
    /// yielded by the producer has been committed to the destination.
    ///
    /// Blocks if the job channel is at capacity (backpressure).
    /// Throws <see cref="UploadException"/> if all retries are exhausted.
    /// Throws <see cref="ObjectDisposedException"/> if the uploader is disposed.
    /// </summary>
    public async Task EnqueueJobAsync(Func<IAsyncEnumerable<T>> producer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var job = new EnqueuedJob<T>(producer);
        await _jobChannel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
        await job.Token.Completion.Task.ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stage 1 — Producer task
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunProducerAsync(CancellationToken ct)
    {
        Logger.LogInformation("[{Dest}] Producer task started.", DestinationName);
        try
        {
            await foreach (var job in _jobChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await IterateJobAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            _recordChannel.Writer.TryComplete();
            Logger.LogInformation("[{Dest}] Producer task stopped.", DestinationName);
        }
    }

    private async Task IterateJobAsync(EnqueuedJob<T> job, CancellationToken ct)
    {
        try
        {
            await foreach (var record in job.Producer().WithCancellation(ct).ConfigureAwait(false))
            {
                job.Token.IncrementEnqueued();
                await _recordChannel.Writer
                    .WriteAsync(new RecordEnvelope<T>(record, job.Token), ct)
                    .ConfigureAwait(false);
            }
            job.Token.ProducerDone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Token.ProducerDone(
                new OperationCanceledException("Uploader was disposed during job iteration."));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dest}] Producer failed iterating job.", DestinationName);
            job.Token.ProducerDone(ex);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stage 2 — Batcher task
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunBatcherAsync(CancellationToken ct)
    {
        Logger.LogInformation("[{Dest}] Batcher task started.", DestinationName);

        var buffer    = new List<T>();
        var jobCounts = new Dictionary<JobToken, int>(ReferenceEqualityComparer.Instance);
        var flushCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        flushCts.CancelAfter(Parameters.FlushAfterIdleMs);

        try
        {
            while (true)
            {
                bool gotRecord;
                RecordEnvelope<T> envelope = default;

                try
                {
                    gotRecord = await _recordChannel.Reader.WaitToReadAsync(flushCts.Token)
                                    .ConfigureAwait(false)
                                && _recordChannel.Reader.TryRead(out envelope);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Idle-gap timer fired.
                    if (buffer.Count > 0)
                    {
                        Logger.LogDebug("[{Dest}] Idle flush: {N} records.", DestinationName, buffer.Count);
                        await FlushAsync(buffer, jobCounts, ct).ConfigureAwait(false);
                    }

                    if (_recordChannel.Reader.Completion.IsCompleted) break;

                    flushCts.Dispose();
                    flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    flushCts.CancelAfter(Parameters.FlushAfterIdleMs);
                    continue;
                }

                if (!gotRecord) break; // record channel drained and completed

                // Reset idle timer — record just arrived.
                flushCts.Dispose();
                flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                flushCts.CancelAfter(Parameters.FlushAfterIdleMs);

                buffer.Add(envelope.Record);
                jobCounts[envelope.Token] = jobCounts.GetValueOrDefault(envelope.Token) + 1;

                if (buffer.Count >= Tuner.BatchSize)
                    await FlushAsync(buffer, jobCounts, ct).ConfigureAwait(false);
            }

            if (buffer.Count > 0)
            {
                Logger.LogDebug("[{Dest}] Final flush: {N} records.", DestinationName, buffer.Count);
                await FlushAsync(buffer, jobCounts, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            flushCts.Dispose();
            _batchChannel.Writer.TryComplete();
            Logger.LogInformation("[{Dest}] Batcher task stopped.", DestinationName);
        }
    }

    private async Task FlushAsync(List<T> buffer, Dictionary<JobToken, int> jobCounts, CancellationToken ct)
    {
        var batch = new ReadyBatch<T>(
            new List<T>(buffer),
            new Dictionary<JobToken, int>(jobCounts, ReferenceEqualityComparer.Instance));

        await _batchChannel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
        buffer.Clear();
        jobCounts.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stage 3 — Uploader task
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunUploaderAsync(CancellationToken ct)
    {
        Logger.LogInformation("[{Dest}] Uploader task started.", DestinationName);
        var sw = new Stopwatch();

        while (!ct.IsCancellationRequested)
        {
            ReadyBatch<T>? batch;
            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(Parameters.IdleTimeoutMs);

                if (!await _batchChannel.Reader.WaitToReadAsync(idleCts.Token).ConfigureAwait(false))
                    break;

                _batchChannel.Reader.TryRead(out batch);
                if (batch is null) continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Logger.LogDebug("[{Dest}] Idle timeout — disconnecting.", DestinationName);
                await SafeDisconnectAsync().ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) { break; }

            // ── Ensure connected ──────────────────────────────────────────
            if (!IsConnected)
            {
                try
                {
                    Logger.LogInformation("[{Dest}] Connecting...", DestinationName);
                    await ConnectAsync(ct).ConfigureAwait(false);
                    Logger.LogInformation("[{Dest}] Connected.", DestinationName);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[{Dest}] Connection failed — faulting batch.", DestinationName);
                    FaultBatch(batch, new UploadException(DestinationName, "Connection failed.", ex));
                    continue;
                }
            }

            // ── Upload with retry ─────────────────────────────────────────
            try
            {
                sw.Restart();
                await UploadWithRetryAsync(batch.Records, ct).ConfigureAwait(false);
                sw.Stop();

                Tuner.RecordBatch(batch.Records.Count, sw.Elapsed.TotalMilliseconds);
                LogBatch(batch.Records.Count, sw.Elapsed.TotalMilliseconds);

                foreach (var (token, count) in batch.JobCounts)
                    token.NotifyUploaded(count);

                await SafeInvokeHookAsync(
                    () => OnBatchSucceededAsync(batch.Records.Count, sw.Elapsed, ct),
                    nameof(OnBatchSucceededAsync)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Dest}] Batch permanently failed after retries.", DestinationName);
                var uploadEx = ex as UploadException ?? new UploadException(DestinationName, "Batch failed.", ex);
                FaultBatch(batch, uploadEx);

                await SafeInvokeHookAsync(
                    () => OnBatchFailedAsync(batch.Records.Count, uploadEx, ct),
                    nameof(OnBatchFailedAsync)).ConfigureAwait(false);
            }
        }

        await DrainBatchChannelAsync().ConfigureAwait(false);
        await SafeDisconnectAsync().ConfigureAwait(false);
        await SafeInvokeHookAsync(() => OnShutdownAsync(ct), nameof(OnShutdownAsync)).ConfigureAwait(false);
        Logger.LogInformation("[{Dest}] Uploader task stopped.", DestinationName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Retry
    // ─────────────────────────────────────────────────────────────────────────

    private async Task UploadWithRetryAsync(IReadOnlyList<T> records, CancellationToken ct)
    {
        var maxRetries = Parameters.MaxRetries;
        Exception? lastEx = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = Parameters.RetryBaseDelayMs * (1 << (attempt - 1));
                Logger.LogWarning("[{Dest}] Retry {A}/{M} in {D}ms.", DestinationName, attempt, maxRetries, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            try
            {
                await UploadBatchAsync(records, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                Logger.LogWarning(ex, "[{Dest}] Upload attempt {A} failed.", DestinationName, attempt + 1);
            }
        }

        throw new UploadException(DestinationName,
            $"Batch failed after {maxRetries} retries.", lastEx!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void FaultBatch(ReadyBatch<T> batch, Exception ex)
    {
        foreach (var (token, _) in batch.JobCounts)
            token.Fault(ex);
    }

    private async Task DrainBatchChannelAsync()
    {
        _batchChannel.Writer.TryComplete();
        var ex = new OperationCanceledException("Uploader disposed before batch was uploaded.");
        await foreach (var batch in _batchChannel.Reader.ReadAllAsync())
            FaultBatch(batch, ex);
    }

    private async Task SafeDisconnectAsync()
    {
        if (!IsConnected) return;
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
            Logger.LogInformation("[{Dest}] Disconnected.", DestinationName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Dest}] Error during disconnect (ignored).", DestinationName);
        }
    }

    private async Task SafeInvokeHookAsync(Func<Task> hook, string hookName)
    {
        try   { await hook().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Dest}] Hook {Hook} threw (ignored).", DestinationName, hookName);
        }
    }

    private void LogBatch(int count, double ms) =>
        Logger.LogDebug(
            "[{Dest}] ✓ {Count} records in {Ms:F1}ms ({Tput:F0} rec/s) | next batch: {Next} | dir: {Dir}",
            DestinationName, count, ms, count / (ms / 1000.0),
            Tuner.BatchSize, Tuner.Snapshot().Direction > 0 ? "▲" : "▼");

    // ─────────────────────────────────────────────────────────────────────────
    // Abstract surface — MUST override
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns true when the connection to the destination is open and usable.</summary>
    protected abstract bool IsConnected { get; }

    /// <summary>
    /// Open the connection to the destination. Called before the first batch and
    /// automatically after an idle disconnect. May throw; the base class will log
    /// and fault the pending batch.
    /// </summary>
    protected abstract Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Close the connection. Called on idle timeout and during shutdown.
    /// Should not throw — any exception is caught and logged by the base class.
    /// </summary>
    protected abstract Task DisconnectAsync();

    /// <summary>
    /// Perform the actual bulk insert for <paramref name="batch"/>.
    /// Called exclusively from the single uploader task — no concurrency concerns.
    /// Throw on failure; the base class handles retries with exponential back-off.
    /// </summary>
    protected abstract Task UploadBatchAsync(IReadOnlyList<T> batch, CancellationToken ct);

    // ─────────────────────────────────────────────────────────────────────────
    // Virtual hooks — OPTIONALLY override
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once per successful batch, after <see cref="JobToken.NotifyUploaded"/>
    /// has been called. Use for metrics, audit trails, etc.
    /// Exceptions are caught and logged — do not re-throw.
    /// </summary>
    protected virtual Task OnBatchSucceededAsync(int recordCount, TimeSpan elapsed, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Called after a batch permanently fails (all retries exhausted).
    /// The affected jobs have already been faulted. Use for alerting, dead-letter
    /// logging, etc. Exceptions are caught and logged — do not re-throw.
    /// </summary>
    protected virtual Task OnBatchFailedAsync(int recordCount, UploadException exception, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Called once during <see cref="DisposeAsync"/>, after the pipeline has fully
    /// drained and the connection has been closed. Use to release shared resources
    /// allocated in the constructor or a factory method.
    /// Exceptions are caught and logged — do not re-throw.
    /// </summary>
    protected virtual Task OnShutdownAsync(CancellationToken ct)
        => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────────────────────
    // Disposal
    // ─────────────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _jobChannel.Writer.TryComplete();
        await _disposeCts.CancelAsync().ConfigureAwait(false);

        await Task.WhenAll(_producerTask, _batcherTask, _uploaderTask).ConfigureAwait(false);
        _disposeCts.Dispose();
    }
}
