namespace BulkUploader.Core;

/// <summary>
/// Runtime-tunable parameters shared by all pipeline stages.
/// All mutable properties use <see cref="Volatile"/> so they can be updated
/// safely from any thread and take effect after the current operation completes.
/// Channel capacities are init-only: they are fixed when the uploader is constructed.
/// </summary>
public sealed class UploaderParameters
{
    // ── Channel capacities (fixed at construction) ────────────────────────────

    private int _jobChannelCapacity;
    private int _recordChannelCapacity;
    private int _batchChannelCapacity;

    /// <summary>Max pending jobs before <see cref="DestinationUploader{T}.EnqueueJobAsync"/> blocks.</summary>
    public int JobChannelCapacity
    {
        get => Volatile.Read(ref _jobChannelCapacity);
        init => _jobChannelCapacity = Math.Max(1, value);
    }

    /// <summary>Max records buffered between the producer task and the batcher task.</summary>
    public int RecordChannelCapacity
    {
        get => Volatile.Read(ref _recordChannelCapacity);
        init => _recordChannelCapacity = Math.Max(1, value);
    }

    /// <summary>Max ready batches buffered between the batcher task and the uploader task.</summary>
    public int BatchChannelCapacity
    {
        get => Volatile.Read(ref _batchChannelCapacity);
        init => _batchChannelCapacity = Math.Max(1, value);
    }

    // ── Runtime-tunable ──────────────────────────────────────────────────────

    private int _maxRetries;
    private int _retryBaseDelayMs;
    private int _idleTimeoutMs;
    private int _flushAfterIdleMs;

    /// <summary>How many times to retry a failed batch before faulting the owning job(s).</summary>
    public int MaxRetries
    {
        get => Volatile.Read(ref _maxRetries);
        set => Volatile.Write(ref _maxRetries, Math.Max(0, value));
    }

    /// <summary>Base delay for exponential back-off between retries (ms). Doubles on each attempt.</summary>
    public int RetryBaseDelayMs
    {
        get => Volatile.Read(ref _retryBaseDelayMs);
        set => Volatile.Write(ref _retryBaseDelayMs, Math.Max(0, value));
    }

    /// <summary>
    /// How long the uploader task waits for a new batch before disconnecting (ms).
    /// The connection is re-established transparently when the next batch arrives.
    /// </summary>
    public int IdleTimeoutMs
    {
        get => Volatile.Read(ref _idleTimeoutMs);
        set => Volatile.Write(ref _idleTimeoutMs, Math.Max(100, value));
    }

    /// <summary>
    /// The batcher flushes a partial buffer when no new record arrives within
    /// this window (ms). Uses idle-gap semantics: the timer resets on every
    /// incoming record.
    /// </summary>
    public int FlushAfterIdleMs
    {
        get => Volatile.Read(ref _flushAfterIdleMs);
        set => Volatile.Write(ref _flushAfterIdleMs, Math.Max(1, value));
    }

    public UploaderParameters(
        int jobChannelCapacity    = 256,
        int recordChannelCapacity = 500_000,
        int batchChannelCapacity  = 8,
        int maxRetries            = 3,
        int retryBaseDelayMs      = 500,
        int idleTimeoutMs         = 10_000,
        int flushAfterIdleMs      = 2_000)
    {
        JobChannelCapacity    = jobChannelCapacity;
        RecordChannelCapacity = recordChannelCapacity;
        BatchChannelCapacity  = batchChannelCapacity;
        MaxRetries            = maxRetries;
        RetryBaseDelayMs      = retryBaseDelayMs;
        IdleTimeoutMs         = idleTimeoutMs;
        FlushAfterIdleMs      = flushAfterIdleMs;
    }
}
