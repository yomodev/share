using System.Collections.Concurrent;
using NxtUI.Core.Events;

namespace NxtUI.Tests.Events;

/// <summary>
/// Reusable test double for <see cref="IRunEventSink"/> — captures every written event
/// in order instead of persisting anywhere, so a test (e.g. of <see cref="RunEventReporter"/>
/// or a future producer) can assert exactly what was emitted without a real SQL table.
/// </summary>
public sealed class FakeEventSink : IRunEventSink
{
    private readonly ConcurrentQueue<RunEvent> _written = new();

    /// <summary>All events written so far, in the order WriteAsync received them.</summary>
    public IReadOnlyList<RunEvent> Written => _written.ToArray();

    /// <summary>How many times WriteAsync has been called (as opposed to how many events total).</summary>
    public int WriteCallCount { get; private set; }

    /// <summary>Set to make the next WriteAsync call throw, to test a producer's error handling.</summary>
    public Exception? ThrowOnNextWrite { get; set; }

    public Task WriteAsync(IReadOnlyList<RunEvent> events, CancellationToken ct = default)
    {
        WriteCallCount++;

        if (ThrowOnNextWrite is { } ex)
        {
            ThrowOnNextWrite = null;
            throw ex;
        }

        foreach (var e in events)
            _written.Enqueue(e);

        return Task.CompletedTask;
    }
}
