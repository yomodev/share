using System.Runtime.CompilerServices;

namespace BulkUploader.Tests.Helpers;

internal static class AsyncEnumerableHelpers
{
    /// <summary>Wraps an IEnumerable as IAsyncEnumerable (no actual async I/O — for tests only).</summary>
    public static async IAsyncEnumerable<T> ToAsync<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in source)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    /// <summary>Async enumerable that throws after yielding <paramref name="yieldCount"/> items.</summary>
    public static async IAsyncEnumerable<T> WithFaultAfter<T>(
        this IEnumerable<T> source,
        int yieldCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = 0;
        foreach (var item in source)
        {
            ct.ThrowIfCancellationRequested();
            if (count++ >= yieldCount) throw new InvalidOperationException("Simulated producer fault.");
            yield return item;
            await Task.Yield();
        }
    }
}
