namespace BulkUploader.Core;

// ── Channel 1 payload ─────────────────────────────────────────────────────────

/// <summary>
/// Written by chunk processors into Channel 1 (the job channel).
/// Read by the single producer task which iterates the lazy producer.
/// </summary>
internal sealed class EnqueuedJob<T>
{
    public Func<IAsyncEnumerable<T>> Producer { get; }
    public JobToken Token { get; } = new();

    public EnqueuedJob(Func<IAsyncEnumerable<T>> producer) => Producer = producer;
}

// ── Channel 2 payload ─────────────────────────────────────────────────────────

/// <summary>
/// Written by the producer task into Channel 2 (the record channel).
/// Each envelope carries the record and a reference to its job's token so the
/// batcher can track per-job counts even when records from different jobs interleave.
/// Struct to avoid per-record heap allocation.
/// </summary>
internal readonly struct RecordEnvelope<T>
{
    public T        Record { get; }
    public JobToken Token  { get; }

    public RecordEnvelope(T record, JobToken token) { Record = record; Token = token; }
}

// ── Channel 3 payload ─────────────────────────────────────────────────────────

/// <summary>
/// Written by the batcher task into Channel 3 (the batch channel).
/// <see cref="JobCounts"/> maps each <see cref="JobToken"/> present in this batch
/// to the number of its records included. Used by the uploader to call
/// <see cref="JobToken.NotifyUploaded"/> with the correct per-job count.
/// </summary>
internal sealed class ReadyBatch<T>
{
    public IReadOnlyList<T>                     Records   { get; }
    public IReadOnlyDictionary<JobToken, int>   JobCounts { get; }

    public ReadyBatch(List<T> records, Dictionary<JobToken, int> jobCounts)
    {
        Records   = records;
        JobCounts = jobCounts;
    }
}
