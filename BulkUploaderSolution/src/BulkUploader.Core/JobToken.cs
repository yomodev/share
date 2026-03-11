namespace BulkUploader.Core;

/// <summary>
/// Tracks the lifecycle of one enqueued job across the three-stage pipeline.
///
/// The chunk processor awaits <see cref="Completion"/>.
/// The producer increments <see cref="IncrementEnqueued"/> before writing each
/// record, then calls <see cref="ProducerDone"/> when its iterator is exhausted.
/// The uploader calls <see cref="NotifyUploaded"/> after each successful batch,
/// passing how many records from this job were in that batch.
///
/// The TCS fires when <c>ProducerDone</c> has been called AND
/// <c>_uploaded >= _enqueued</c> — meaning every produced record has been committed.
///
/// Thread-safety: counters use <see cref="Interlocked"/>; TCS uses
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>.
/// </summary>
internal sealed class JobToken
{
    private long _enqueued;
    private long _uploaded;
    private int  _producerDone;  // 0 = still running, 1 = done

    public TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── Producer side ────────────────────────────────────────────────────────

    /// <summary>
    /// Called BEFORE writing each record to the record channel.
    /// Incrementing before write ensures the count is always >= records in-flight,
    /// preventing a premature TCS resolution race.
    /// </summary>
    public void IncrementEnqueued() => Interlocked.Increment(ref _enqueued);

    /// <summary>
    /// Called after the job's IAsyncEnumerable is fully iterated (or on error).
    /// If <paramref name="fault"/> is non-null, the job is faulted immediately.
    /// If zero records were produced this also resolves the job immediately.
    /// </summary>
    public void ProducerDone(Exception? fault = null)
    {
        if (fault is not null) { Completion.TrySetException(fault); return; }
        Interlocked.Exchange(ref _producerDone, 1);
        TryResolve();
    }

    // ── Uploader side ────────────────────────────────────────────────────────

    /// <summary>
    /// Called after successfully uploading <paramref name="count"/> records
    /// that belonged to this job.
    /// </summary>
    public void NotifyUploaded(int count)
    {
        Interlocked.Add(ref _uploaded, count);
        TryResolve();
    }

    /// <summary>Permanently faults the job (e.g. batch failed after all retries).</summary>
    public void Fault(Exception ex) => Completion.TrySetException(ex);

    // ── Internal ─────────────────────────────────────────────────────────────

    private void TryResolve()
    {
        if (Volatile.Read(ref _producerDone) == 1 &&
            Volatile.Read(ref _uploaded) >= Volatile.Read(ref _enqueued))
        {
            Completion.TrySetResult();
        }
    }
}
