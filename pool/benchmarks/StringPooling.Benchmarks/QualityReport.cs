using StringPooling.Analysis;

namespace StringPooling.Benchmarks;

/// <summary>Prints hash-quality numbers for every hasher (including the weak FNV-1a baseline).</summary>
public static class QualityReport
{
    public static void Run()
    {
        const int n = 1 << 20;

        Console.WriteLine($"== Collisions of full 64-bit hash (want 0) and chi-square z-scores (want |z| < ~4), {n:N0} keys per family ==");
        foreach (var h in HasherCatalog.All)
        {
            Console.WriteLine();
            Console.WriteLine(h.Name);
            Console.WriteLine("  " + "family".PadRight(16) + "collisions".PadLeft(12) + "z(slot16)".PadLeft(12) + "z(shard6)".PadLeft(12));
            foreach (var f in HashQuality.KeyFamilies)
            {
                int c = HashQuality.CountCollisions(h.Fn, f.Make, n);
                double zs = HashQuality.ChiSquareZ(h.Fn, f.Make, n, shift: 0, bits: 16);
                double zh = HashQuality.ChiSquareZ(h.Fn, f.Make, n, shift: 32, bits: 6);
                Console.WriteLine("  " + f.Name.PadRight(16) + c.ToString().PadLeft(12) + zs.ToString("F2").PadLeft(12) + zh.ToString("F2").PadLeft(12));
            }
        }

        Console.WriteLine();
        Console.WriteLine("== Avalanche (ideal: mean 0.5000, worst cell bias near 0; 3000 samples/length) ==");
        foreach (var h in HasherCatalog.All)
        {
            Console.WriteLine();
            Console.WriteLine(h.Name);
            Console.WriteLine("  " + "length".PadRight(8) + "mean".PadLeft(10) + "worstBias".PadLeft(12));
            foreach (int len in new[] { 1, 2, 3, 4, 7, 8, 9, 16, 31, 33, 64, 100 })
            {
                var r = HashQuality.Avalanche(h.Fn, len, 3000, 1234 + len);
                Console.WriteLine("  " + len.ToString().PadRight(8) + r.MeanFlipProbability.ToString("F4").PadLeft(10) + r.WorstCellBias.ToString("F4").PadLeft(12));
            }
        }
        Console.WriteLine();
        Console.WriteLine("Note: for length 1-2 there are only 256 / 65536 distinct inputs, so worstBias is noisy there.");
    }
}
