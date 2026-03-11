using BulkUploader.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BulkUploader.Tests.Helpers;

/// <summary>
/// In-memory <see cref="DestinationUploader{T}"/> test double.
/// All uploads are stored in <see cref="UploadedBatches"/> for assertion.
/// </summary>
public sealed class InMemoryUploader<T> : DestinationUploader<T>
{
    private readonly object _batchLock = new();
    private          bool   _simulateConnectFailure;
    private          int    _failNextNBatches;
    private          bool   _connected;

    private int _connectCount;
    private int _disconnectCount;
    private int _batchSuccessCount;
    private int _batchFailureCount;

    public List<IReadOnlyList<T>> UploadedBatches  { get; } = [];
    public List<T> AllUploadedItems => UploadedBatches.SelectMany(b => b).ToList();

    public int GetConnectCount()      => Volatile.Read(ref _connectCount);
    public int GetDisconnectCount()   => Volatile.Read(ref _disconnectCount);
    public int GetBatchSuccessCount() => Volatile.Read(ref _batchSuccessCount);
    public int GetBatchFailureCount() => Volatile.Read(ref _batchFailureCount);

    public InMemoryUploader(
        UploaderParameters? parameters = null,
        BatchSizeTuner?     tuner      = null,
        ILogger?            logger     = null)
        : base(
            "InMemory",
            parameters ?? new UploaderParameters(flushAfterIdleMs: 100),
            tuner      ?? new BatchSizeTuner(initial: 10, min: 2, max: 1000),
            logger     ?? NullLogger.Instance)
    { }

    // ── Test controls ─────────────────────────────────────────────────────────

    public void SimulateConnectFailure(bool fail) =>
        Volatile.Write(ref _simulateConnectFailure, fail);

    /// <summary>Next N batches will throw. Uses CAS so it's safe from concurrent access.</summary>
    public void FailNextBatches(int count) =>
        Interlocked.Exchange(ref _failNextNBatches, count);

    // ── Overrides ─────────────────────────────────────────────────────────────

    protected override bool IsConnected => Volatile.Read(ref _connected);

    protected override Task ConnectAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _simulateConnectFailure))
            throw new InvalidOperationException("Simulated connect failure.");

        Interlocked.Increment(ref _connectCount);
        Volatile.Write(ref _connected, true);
        return Task.CompletedTask;
    }

    protected override Task DisconnectAsync()
    {
        Interlocked.Increment(ref _disconnectCount);
        Volatile.Write(ref _connected, false);
        return Task.CompletedTask;
    }

    protected override Task UploadBatchAsync(IReadOnlyList<T> batch, CancellationToken ct)
    {
        // Atomically consume one failure slot via CAS.
        int slot;
        do
        {
            slot = Volatile.Read(ref _failNextNBatches);
            if (slot <= 0) break;
        }
        while (Interlocked.CompareExchange(ref _failNextNBatches, slot - 1, slot) != slot);

        if (slot > 0)
            throw new InvalidOperationException($"Simulated upload failure (slots remaining: {slot - 1}).");

        lock (_batchLock)
            UploadedBatches.Add(batch.ToList());

        return Task.CompletedTask;
    }

    protected override Task OnBatchSucceededAsync(int recordCount, TimeSpan elapsed, CancellationToken ct)
    {
        Interlocked.Increment(ref _batchSuccessCount);
        return Task.CompletedTask;
    }

    protected override Task OnBatchFailedAsync(int recordCount, UploadException exception, CancellationToken ct)
    {
        Interlocked.Increment(ref _batchFailureCount);
        return Task.CompletedTask;
    }
}
