using StringPooling.Analysis;

namespace StringPooling.Tests;

/// <summary>
/// Statistical sanity checks. They run on the hashers expected to be good (Custom, XxHash3, XxHash64);
/// run `dotnet run -c Release --project benchmarks/StringPooling.Benchmarks -- quality`
/// to see the numbers for all hashers, including the weak FNV-1a baseline.
/// </summary>
public class HashQualityTests
{
    private const int N = 1 << 20;

    public static IEnumerable<object[]> Hashers() => HasherCatalog.Strong.Select(h => new object[] { h.Name });

    [Theory, MemberData(nameof(Hashers))]
    public void No_full_64bit_collisions_on_structured_keys(string name)
    {
        var hash = HasherCatalog.Get(name).Fn;
        foreach (var family in HashQuality.KeyFamilies)
        {
            int collisions = HashQuality.CountCollisions(hash, family.Make, N);
            Assert.True(collisions == 0, $"{name}/{family.Name}: {collisions} full 64-bit collisions in {N} keys");
        }
    }

    [Theory, MemberData(nameof(Hashers))]
    public void Slot_and_shard_bits_are_uniform(string name)
    {
        var hash = HasherCatalog.Get(name).Fn;
        foreach (var family in HashQuality.KeyFamilies)
        {
            double zSlot = HashQuality.ChiSquareZ(hash, family.Make, N, shift: 0, bits: 16);    // low bits -> table slot
            double zShard = HashQuality.ChiSquareZ(hash, family.Make, N, shift: 32, bits: 6);   // bits 32..37 -> shard
            Assert.True(Math.Abs(zSlot) < 7, $"{name}/{family.Name}: slot-bit chi-square z = {zSlot:F2}");
            Assert.True(Math.Abs(zShard) < 7, $"{name}/{family.Name}: shard-bit chi-square z = {zShard:F2}");
        }
    }

    [Theory, MemberData(nameof(Hashers))]
    public void Single_bit_flips_avalanche(string name)
    {
        var hash = HasherCatalog.Get(name).Fn;
        foreach (int length in new[] { 3, 4, 7, 8, 9, 16, 31, 33, 64, 100 })
        {
            var r = HashQuality.Avalanche(hash, length, samples: 3000, seed: 1234 + length);
            Assert.True(Math.Abs(r.MeanFlipProbability - 0.5) < 0.01,
                $"{name} len {length}: mean flip probability {r.MeanFlipProbability:F4}");
            Assert.True(r.WorstCellBias < 0.08,
                $"{name} len {length}: worst (input bit, output bit) bias {r.WorstCellBias:F4}");
        }
    }
}
