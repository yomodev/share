using BenchmarkDotNet.Attributes;

namespace StringPooling.Benchmarks;

/// <summary>
/// One thread, fresh (pre-sized) pool per invocation, so misses (insertions) and hits are both measured,
/// like processing a file once. Compare Mean and Allocated across PoolKind.
/// </summary>
[MemoryDiagnoser]
public class SingleThreadBenchmarks
{
    private const int FieldCount = 4_000_000;
    private FieldData _data = null!;

    [Params(1_000, 1_000_000)]
    public int Distinct { get; set; }

    [ParamsAllValues]
    public bool NonAscii { get; set; }

    [ParamsAllValues]
    public PoolKind Kind { get; set; }

    [GlobalSetup]
    public void Setup() => _data = FieldData.Generate(FieldCount, Distinct, NonAscii, seed: 42);

    [Benchmark]
    public long Run()
    {
        var pool = PoolCatalog.Create(Kind, Distinct);
        var buf = _data.Buffer;
        var off = _data.Offsets;
        var len = _data.Lengths;

        long total = 0;
        for (int i = 0; i < off.Length; i++)
            total += pool.Get(buf.AsSpan(off[i], len[i])).Length;
        return total;
    }
}

/// <summary>Several threads share one pool; only thread-safe implementations take part.</summary>
[MemoryDiagnoser]
public class ParallelBenchmarks
{
    private const int FieldCount = 8_000_000;
    private FieldData _data = null!;

    [Params(1_000, 1_000_000)]
    public int Distinct { get; set; }

    [Params(1, 2, 4, 8)]
    public int Threads { get; set; }

    [ParamsSource(nameof(ThreadSafeKinds))]
    public PoolKind Kind { get; set; }

    public IEnumerable<PoolKind> ThreadSafeKinds => PoolCatalog.ThreadSafeKinds.Where(k => k != PoolKind.NoPool);

    [GlobalSetup]
    public void Setup() => _data = FieldData.Generate(FieldCount, Distinct, nonAscii: false, seed: 42);

    [Benchmark]
    public long Run()
    {
        var pool = PoolCatalog.Create(Kind, Distinct);
        var buf = _data.Buffer;
        var off = _data.Offsets;
        var len = _data.Lengths;
        int n = off.Length;
        int threads = Threads;
        long total = 0;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            int start = (int)((long)n * t / threads);
            int end = (int)((long)n * (t + 1) / threads);
            long local = 0;
            for (int i = start; i < end; i++)
                local += pool.Get(buf.AsSpan(off[i], len[i])).Length;
            Interlocked.Add(ref total, local);
        });
        return total;
    }
}
