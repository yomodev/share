using System.IO.Hashing;
using System.Text;
using StringPooling;

namespace StringPooling.Tests;

// Hashers that are deliberately terrible, to prove the pool stays correct under collisions.
public readonly struct ZeroHasher : IUtf8Hasher
{
    public static ulong Hash(ReadOnlySpan<byte> data) => 0;                   // also exercises the "0 = empty" remap
}

public readonly struct ConstantHasher : IUtf8Hasher
{
    public static ulong Hash(ReadOnlySpan<byte> data) => 0x1234_5678_9ABC_DEF0UL;
}

public readonly struct NarrowHasher : IUtf8Hasher
{
    public static ulong Hash(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data) & 0xFF;   // only 256 distinct hashes
}

public class ShardedStringPoolTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static void ExerciseSmallPool<THasher>(int n) where THasher : IUtf8Hasher
    {
        var pool = new ShardedStringPool<THasher>(expectedCount: 1, shardCount: 4);
        var first = new string[n];
        for (int i = 0; i < n; i++) first[i] = pool.Get(B($"value-{i}"));

        for (int i = 0; i < n; i++)
        {
            Assert.Equal($"value-{i}", first[i]);
            Assert.Same(first[i], pool.Get(B($"value-{i}")));
        }
        Assert.Equal(n, pool.Count);
    }

    [Fact] public void Hash_zero_for_everything() => ExerciseSmallPool<ZeroHasher>(3000);
    [Fact] public void Constant_hash_for_everything() => ExerciseSmallPool<ConstantHasher>(3000);
    [Fact] public void Only_256_distinct_hashes() => ExerciseSmallPool<NarrowHasher>(20_000);
    [Fact] public void Real_hash_with_growth_from_tiny_start() => ExerciseSmallPool<XxHash3Hasher>(200_000);

    [Fact]
    public void Single_shard_works()
    {
        var pool = new ShardedStringPool<XxHash3Hasher>(expectedCount: 1, shardCount: 1);
        for (int i = 0; i < 50_000; i++)
            Assert.Same(pool.Get(B($"k{i}")), pool.Get(B($"k{i}")));
        Assert.Equal(50_000, pool.Count);
    }

    [Fact]
    public void Count_counts_distinct_values_only()
    {
        var pool = new ShardedStringPool<XxHash3Hasher>(16, 8);
        for (int rep = 0; rep < 5; rep++)
            for (int i = 0; i < 1000; i++)
                pool.Get(B($"v{i}"));
        Assert.Equal(1000, pool.Count);
    }

    [Fact]
    public void Char_overload_shares_instances_with_byte_overload()
    {
        var pool = new ShardedStringPool<XxHash3Hasher>(16, 4);
        foreach (var s in new[] { "plain", "héllo", "日本語", "😀😀", new string('é', 1000) /* >512 bytes */ })
        {
            var fromBytes = pool.Get(B(s));
            var fromChars = pool.Get(s.AsSpan());
            Assert.Equal(s, fromChars);
            Assert.Same(fromBytes, fromChars);
        }
    }

    [Fact]
    public void Concurrent_gets_return_exactly_one_reference_per_distinct_value()
    {
        const int distinct = 20_000;
        const int perThread = 200_000;
        const int threads = 8;

        var words = Enumerable.Range(0, distinct).Select(i => B($"word-{i}-ü")).ToArray();
        // Tiny initial capacity so tables grow while other threads are reading.
        var pool = new ShardedStringPool<XxHash3Hasher>(expectedCount: 16, shardCount: 8);

        var results = new string?[threads][];
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            var local = new string?[distinct];
            var rng = new Random(t);
            gate.Wait();
            for (int k = 0; k < perThread; k++)
            {
                int i = rng.Next(distinct);
                var s = pool.Get(words[i]);
                if (local[i] is null) local[i] = s;
                else if (!ReferenceEquals(local[i], s))
                    throw new Exception($"thread {t} saw two different instances for word {i}");
            }
            results[t] = local;
        }, TaskCreationOptions.LongRunning)).ToArray();

        gate.Set();
        Task.WaitAll(tasks);

        int requested = 0;
        for (int i = 0; i < distinct; i++)
        {
            string? reference = null;
            for (int t = 0; t < threads; t++)
            {
                var s = results[t]![i];
                if (s is null) continue;
                if (reference is null) { reference = s; requested++; }
                Assert.Same(reference, s);
            }
            if (reference is not null) Assert.Equal($"word-{i}-ü", reference);
        }
        Assert.Equal(requested, pool.Count);
    }
}
