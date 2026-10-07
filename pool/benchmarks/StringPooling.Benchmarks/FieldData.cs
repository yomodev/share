using System.Text;

namespace StringPooling.Benchmarks;

/// <summary>
/// Synthetic "parsed CSV" input: one big UTF-8 buffer plus (offset, length) per field,
/// which is exactly what a span-based CSV parser hands to the pool.
/// Values are drawn from a skewed distribution (few very common values, a long tail).
/// </summary>
public sealed class FieldData
{
    public byte[] Buffer = Array.Empty<byte>();
    public int[] Offsets = Array.Empty<int>();
    public int[] Lengths = Array.Empty<int>();

    public static FieldData Generate(int fieldCount, int distinct, bool nonAscii, int seed)
    {
        var rng = new Random(seed);
        string[] prefixes = { "ACTIVE", "pending", "CZ", "Praha", "order", "customer", "SKU", "north", "item", "region" };

        var words = new byte[distinct][];
        var sb = new StringBuilder();
        for (int i = 0; i < distinct; i++)
        {
            sb.Clear();
            sb.Append(prefixes[i % prefixes.Length]).Append('-').Append(i.ToString("x"));   // guarantees uniqueness
            int pad = rng.Next(0, 24);
            for (int k = 0; k < pad; k++) sb.Append((char)('a' + rng.Next(26)));
            if (nonAscii && i % 5 == 0) sb.Append("é日本");
            words[i] = Encoding.UTF8.GetBytes(sb.ToString());
        }

        var picks = new int[fieldCount];
        long total = 0;
        for (int f = 0; f < fieldCount; f++)
        {
            int idx = (int)(distinct * Math.Pow(rng.NextDouble(), 3.0));   // skew toward low indices
            if (idx >= distinct) idx = distinct - 1;
            picks[f] = idx;
            total += words[idx].Length;
        }

        var data = new FieldData
        {
            Buffer = new byte[total],
            Offsets = new int[fieldCount],
            Lengths = new int[fieldCount],
        };
        int pos = 0;
        for (int f = 0; f < fieldCount; f++)
        {
            var w = words[picks[f]];
            w.CopyTo(data.Buffer, pos);
            data.Offsets[f] = pos;
            data.Lengths[f] = w.Length;
            pos += w.Length;
        }
        return data;
    }
}
