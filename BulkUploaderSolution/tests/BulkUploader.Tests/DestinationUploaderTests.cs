using BulkUploader.Core;
using BulkUploader.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BulkUploader.Tests;

public sealed class DestinationUploaderTests : IAsyncLifetime
{
    private InMemoryUploader<int> _uploader = null!;

    public Task InitializeAsync()
    {
        _uploader = new InMemoryUploader<int>(
            parameters: new UploaderParameters(
                jobChannelCapacity:    64,
                recordChannelCapacity: 10_000,
                batchChannelCapacity:  4,
                maxRetries:            0,       // no retries by default — tests control this
                retryBaseDelayMs:      10,
                idleTimeoutMs:         500,
                flushAfterIdleMs:      100));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _uploader.DisposeAsync();

    // ── Basic correctness ──────────────────────────────────────────────────────

    [Fact]
    public async Task SingleJob_AllRecordsUploaded()
    {
        var records = Enumerable.Range(0, 50).ToList();

        await _uploader.EnqueueJobAsync(() => records.ToAsync());

        _uploader.AllUploadedItems.Should().BeEquivalentTo(records);
    }

    [Fact]
    public async Task SingleJob_AwaitUnblocksOnlyAfterUpload()
    {
        var original = _uploader;

        // Wrap with a spy — but InMemoryUploader.OnBatchSucceeded sets flag after upload.
        // Instead just check ordering via the awaited task.
        var records = Enumerable.Range(0, 10).ToList();
        var t = _uploader.EnqueueJobAsync(() => records.ToAsync());
        await t;

        _uploader.AllUploadedItems.Count.Should().Be(10);
    }

    [Fact]
    public async Task MultipleJobs_AllRecordsUploaded()
    {
        var jobs = Enumerable.Range(0, 5)
            .Select(i => Enumerable.Range(i * 100, 100).ToList())
            .ToList();

        var tasks = jobs.Select(j => _uploader.EnqueueJobAsync(() => j.ToAsync()));
        await Task.WhenAll(tasks);

        _uploader.AllUploadedItems.Should().HaveCount(500);
        _uploader.AllUploadedItems.Should().BeEquivalentTo(Enumerable.Range(0, 500));
    }

    [Fact]
    public async Task ConcurrentJobs_AllRecordsUploaded()
    {
        const int jobCount    = 20;
        const int recordsEach = 200;

        var allRecords = Enumerable.Range(0, jobCount * recordsEach).ToList();

        var tasks = Enumerable.Range(0, jobCount).Select(i =>
        {
            var slice = allRecords.Skip(i * recordsEach).Take(recordsEach).ToList();
            return Task.Run(() => _uploader.EnqueueJobAsync(() => slice.ToAsync()));
        });

        await Task.WhenAll(tasks);

        _uploader.AllUploadedItems.Should().HaveCount(jobCount * recordsEach);
        _uploader.AllUploadedItems.Should().BeEquivalentTo(allRecords);
    }

    // ── Idle flush ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PartialBatch_FlushedByIdleTimer()
    {
        // Tuner's initial batch size is 10, but we only send 3.
        var records = new[] { 1, 2, 3 };
        await _uploader.EnqueueJobAsync(() => records.ToAsync());

        _uploader.AllUploadedItems.Should().BeEquivalentTo(records);
        _uploader.UploadedBatches.Should().HaveCount(1);
        _uploader.UploadedBatches[0].Should().HaveCount(3);
    }

    // ── Retry ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetrySucceeds_AfterTransientFailure()
    {
        var uploaderWithRetry = new InMemoryUploader<int>(
            parameters: new UploaderParameters(
                maxRetries:       2,
                retryBaseDelayMs: 10,
                flushAfterIdleMs: 100));

        await using var _ = uploaderWithRetry;

        uploaderWithRetry.FailNextBatches(2); // fail first 2 attempts, succeed on 3rd

        var records = Enumerable.Range(0, 5).ToList();
        await uploaderWithRetry.EnqueueJobAsync(() => records.ToAsync());

        uploaderWithRetry.AllUploadedItems.Should().BeEquivalentTo(records);
    }

    [Fact]
    public async Task RetryExhausted_JobFaultedWithUploadException()
    {
        var uploaderWithRetry = new InMemoryUploader<int>(
            parameters: new UploaderParameters(
                maxRetries:       1,
                retryBaseDelayMs: 10,
                flushAfterIdleMs: 100));

        await using var _ = uploaderWithRetry;
        uploaderWithRetry.FailNextBatches(999); // always fail

        var act = () => uploaderWithRetry.EnqueueJobAsync(() => new[] { 1, 2, 3 }.ToAsync());
        await act.Should().ThrowAsync<UploadException>();
    }

    // ── Producer fault ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProducerFault_JobFaultedImmediately()
    {
        var act = () => _uploader.EnqueueJobAsync(
            () => Enumerable.Range(0, 100).WithFaultAfter(10));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated producer fault.");
    }

    // ── Batching correctness ──────────────────────────────────────────────────

    [Fact]
    public async Task LargeJob_SplitIntoMultipleBatches()
    {
        var uploader = new InMemoryUploader<int>(
            tuner: new BatchSizeTuner(initial: 10, min: 10, max: 10)); // fixed size 10

        await using var _ = uploader;

        var records = Enumerable.Range(0, 100).ToList();
        await uploader.EnqueueJobAsync(() => records.ToAsync());

        uploader.UploadedBatches.Should().HaveCount(10);
        uploader.UploadedBatches.Should().AllSatisfy(b => b.Count.Should().Be(10));
        uploader.AllUploadedItems.Should().BeEquivalentTo(records);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeDuringEnqueue_ThrowsObjectDisposedException()
    {
        await _uploader.DisposeAsync();

        var act = () => _uploader.EnqueueJobAsync(() => new[] { 1 }.ToAsync());
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnBatchSucceeded_CalledPerBatch()
    {
        var uploader = new InMemoryUploader<int>(
            tuner: new BatchSizeTuner(initial: 10, min: 10, max: 10),
            parameters: new UploaderParameters(flushAfterIdleMs: 100));
        await using var _ = uploader;

        await uploader.EnqueueJobAsync(() => Enumerable.Range(0, 30).ToAsync());

        uploader.GetBatchSuccessCount().Should().Be(3);
    }

    [Fact]
    public async Task OnBatchFailed_CalledOnPermanentFailure()
    {
        var uploader = new InMemoryUploader<int>(
            parameters: new UploaderParameters(
                maxRetries: 0, retryBaseDelayMs: 0, flushAfterIdleMs: 100));
        await using var _ = uploader;

        uploader.FailNextBatches(999);

        try { await uploader.EnqueueJobAsync(() => new[] { 1 }.ToAsync()); }
        catch (UploadException) { }

        uploader.GetBatchFailureCount().Should().Be(1);
    }

    // ── Connect / disconnect ──────────────────────────────────────────────────

    [Fact]
    public async Task ConnectFailure_JobFaultedWithUploadException()
    {
        _uploader.SimulateConnectFailure(true);

        var act = () => _uploader.EnqueueJobAsync(() => new[] { 1 }.ToAsync());
        await act.Should().ThrowAsync<UploadException>()
            .WithMessage("*Connection failed*");
    }

    [Fact]
    public async Task IdleTimeout_TriggersDisconnect()
    {
        // Send a job, wait for it, then wait longer than idleTimeoutMs (500ms).
        await _uploader.EnqueueJobAsync(() => new[] { 1 }.ToAsync());
        await Task.Delay(700);

        _uploader.GetDisconnectCount().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Reconnect_AfterIdleDisconnect()
    {
        await _uploader.EnqueueJobAsync(() => new[] { 1 }.ToAsync());
        await Task.Delay(700); // wait for idle disconnect

        _uploader.AllUploadedItems.Should().HaveCount(1);
        var connectsBefore = _uploader.GetConnectCount();

        // Send another job — should reconnect.
        await _uploader.EnqueueJobAsync(() => new[] { 2 }.ToAsync());
        _uploader.GetConnectCount().Should().BeGreaterThan(connectsBefore);
        _uploader.AllUploadedItems.Should().HaveCount(2);
    }
}
