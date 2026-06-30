using System.Collections.Concurrent;

namespace NxtUI.Web.Services;

/// <summary>
/// Singleton registry of in-flight operations. Services call Track() when starting a
/// meaningful operation so the diagnostics monitor can report what was executing during lag.
/// </summary>
public sealed class OperationTracker
{
    private readonly ConcurrentDictionary<long, string> _ops = new();
    private long _seq;

    /// <summary>
    /// Register an in-flight operation. Dispose the returned handle when the operation completes.
    /// </summary>
    public IDisposable Track(string description)
    {
        var id = Interlocked.Increment(ref _seq);
        _ops[id] = description;
        return new Handle(this, id);
    }

    /// <summary>Snapshot of all currently registered operations.</summary>
    public IReadOnlyList<string> ActiveOperations => _ops.Values.ToList();

    private void Remove(long id) => _ops.TryRemove(id, out _);

    private sealed class Handle(OperationTracker owner, long id) : IDisposable
    {
        public void Dispose() => owner.Remove(id);
    }
}
