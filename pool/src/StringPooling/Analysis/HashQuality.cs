using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace StringPooling.Analysis;

public sealed record KeyFamily(string Name, Func<int, byte[]> Make);

public readonly record struct AvalancheResult(double MeanFlipProbability, double WorstCellBias);

/// <summary>Statistical checks for a 64-bit hash over byte strings.</summary>
public static class HashQuality
{
    public static readonly KeyFamily[] KeyFamilies =
    {
        new("SeqId",         i => Encoding.ASCII.GetBytes($"ID{i:D8}")),
        new("Decimal",       i => Encoding.ASCII.GetBytes(i.ToString())),
        new("Email",         i => Encoding.ASCII.GetBytes($"user{i}@example.com")),
        new("Counter16Head", i => { var b = new byte[16]; BinaryPrimitives.WriteInt32LittleEndian(b, i); return b; }),
        new("Counter16Tail", i => { var b = new byte[16]; BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), i); return b; }),
        new("Counter3",      i => new[] { (byte)i, (byte)(i >> 8), (byte)(i >> 16) }),
    };

    /// <summary>Number of distinct keys (0..n-1) whose full 64-bit hashes collide. Expect 0 for a good hash.</summary>
    public static int CountCollisions(HashFunc hash, Func<int, byte[]> make, int n)
    {
        var hashes = new ulong[n];
        for (int i = 0; i < n; i++) hashes[i] = hash(make(i));
        Array.Sort(hashes);
        int collisions = 0;
        for (int i = 1; i < n; i++)
            if (hashes[i] == hashes[i - 1]) collisions++;
        return collisions;
    }

    /// <summary>
    /// Chi-square uniformity of bucket = (hash >> shift) &amp; (2^bits - 1), reported as a z-score
    /// (chi2 - df) / sqrt(2 df). Around 0 is ideal; |z| above ~5 is suspicious in either direction.
    /// shift=0 mimics the table slot index; shift=32 mimics the shard index.
    /// </summary>
    public static double ChiSquareZ(HashFunc hash, Func<int, byte[]> make, int n, int shift, int bits)
    {
        int buckets = 1 << bits;
        ulong mask = (ulong)buckets - 1;
        var counts = new int[buckets];
        for (int i = 0; i < n; i++)
            counts[(int)((hash(make(i)) >> shift) & mask)]++;

        double expected = (double)n / buckets;
        double chi = 0;
        foreach (int c in counts)
        {
            double d = c - expected;
            chi += d * d / expected;
        }
        int df = buckets - 1;
        return (chi - df) / Math.Sqrt(2.0 * df);
    }

    /// <summary>
    /// Strict avalanche test: flipping any single input bit should flip each output bit with probability 0.5.
    /// Returns the mean flip probability and the worst |p - 0.5| over all (input bit, output bit) cells.
    /// </summary>
    public static AvalancheResult Avalanche(HashFunc hash, int length, int samples, int seed)
    {
        var rng = new Random(seed);
        int inBits = length * 8;
        var counts = new int[inBits * 64];
        var data = new byte[length];

        for (int s = 0; s < samples; s++)
        {
            rng.NextBytes(data);
            ulong h0 = hash(data);
            for (int b = 0; b < inBits; b++)
            {
                data[b >> 3] ^= (byte)(1 << (b & 7));
                ulong diff = h0 ^ hash(data);
                data[b >> 3] ^= (byte)(1 << (b & 7));

                int row = b * 64;
                while (diff != 0)
                {
                    counts[row + BitOperations.TrailingZeroCount(diff)]++;
                    diff &= diff - 1;
                }
            }
        }

        long total = 0;
        double worst = 0;
        foreach (int c in counts)
        {
            total += c;
            worst = Math.Max(worst, Math.Abs((double)c / samples - 0.5));
        }
        return new AvalancheResult((double)total / ((double)counts.Length * samples), worst);
    }
}
