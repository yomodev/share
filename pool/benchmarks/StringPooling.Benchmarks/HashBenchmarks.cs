using BenchmarkDotNet.Attributes;

namespace StringPooling.Benchmarks;

/// <summary>Raw hash speed per key length (direct static calls, no delegates).</summary>
public class HashBenchmarks
{
    private const int KeyCount = 1024;
    private byte[][] _keys = null!;

    [Params(4, 8, 16, 32, 64, 256)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(7);
        _keys = new byte[KeyCount][];
        for (int i = 0; i < KeyCount; i++)
        {
            _keys[i] = new byte[Length];
            rng.NextBytes(_keys[i]);
        }
    }

    [Benchmark(OperationsPerInvoke = KeyCount, Baseline = true)]
    public ulong Hash_XxHash3()
    {
        ulong x = 0;
        foreach (var k in _keys) x ^= XxHash3Hasher.Hash(k);
        return x;
    }

    [Benchmark(OperationsPerInvoke = KeyCount)]
    public ulong Hash_Custom()
    {
        ulong x = 0;
        foreach (var k in _keys) x ^= CustomHasher.Hash(k);
        return x;
    }

    [Benchmark(OperationsPerInvoke = KeyCount)]
    public ulong Hash_XxHash64()
    {
        ulong x = 0;
        foreach (var k in _keys) x ^= XxHash64Hasher.Hash(k);
        return x;
    }

    [Benchmark(OperationsPerInvoke = KeyCount)]
    public ulong Hash_Fnv1a()
    {
        ulong x = 0;
        foreach (var k in _keys) x ^= Fnv1aHasher.Hash(k);
        return x;
    }
}
