using System.Buffers;
using System.Numerics;
using System.Text;

namespace StringPooling;

/// <summary>
/// One logical pool made of independent shards. Each shard is an open-addressing table
/// (parallel int[] hashes / string[] values). Hits are lock-free, misses take the shard lock.
/// Entries are never removed, which is what makes the lock-free read path safe.
/// </summary>
public sealed class ShardedStringPool<THasher> : IStringPool where THasher : IUtf8Hasher
{
    private const int MaxSlotsPerShard = 1 << 30;

    private sealed class Table
    {
        public readonly int[] Hashes;      // 0 = empty slot
        public readonly string?[] Values;
        public readonly int Mask;
        public readonly int GrowAt;

        public Table(int slots)
        {
            Hashes = new int[slots];
            Values = new string?[slots];
            Mask = slots - 1;
            GrowAt = (int)((long)slots * 6 / 10);
        }
    }

    private sealed class Shard
    {
        public readonly object Lock = new();
        public Table Current;
        public int Count;
        public Shard(int slots) => Current = new Table(slots);
    }

    private readonly Shard[] _shards;
    private readonly int _shardMask;

    public ShardedStringPool(int expectedCount = 1 << 22, int shardCount = 64)
    {
        int shards = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, shardCount));
        _shardMask = shards - 1;
        long perShard = Math.Max(16, (long)(expectedCount / (double)shards / 0.6) + 1);
        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Min(perShard, MaxSlotsPerShard));
        _shards = new Shard[shards];
        for (int i = 0; i < shards; i++) _shards[i] = new Shard(slots);
    }

    public bool IsThreadSafe => true;

    public int Count
    {
        get
        {
            long n = 0;
            foreach (var s in _shards) n += Volatile.Read(ref s.Count);
            return (int)Math.Min(n, int.MaxValue);
        }
    }

    /// <summary>Primary path: UTF-8 bytes straight from the parser.</summary>
    public string Get(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return string.Empty;

        ulong h64 = THasher.Hash(utf8);
        int h = (int)h64;                          // slot index source: low 32 bits
        if (h == 0) h = 1;                         // 0 is reserved for "empty"
        var shard = _shards[(int)(h64 >> 32) & _shardMask];   // shard source: high bits

        // Lock-free probe.
        var found = Find(Volatile.Read(ref shard.Current), h, utf8);
        if (found is not null) return found;

        lock (shard.Lock)
        {
            var t = shard.Current;
            found = Find(t, h, utf8);              // someone may have inserted it meanwhile
            if (found is not null) return found;

            string s = Encoding.UTF8.GetString(utf8);
            Insert(t, h, s);
            if (++shard.Count > t.GrowAt) Grow(shard);
            return s;
        }
    }

    /// <summary>Convenience path: transcodes to UTF-8 so the hash matches the byte path. Keep off hot paths.</summary>
    public string Get(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return string.Empty;

        int max = Encoding.UTF8.GetMaxByteCount(chars.Length);
        byte[]? rented = null;
        Span<byte> buf = stackalloc byte[512];
        if (max > buf.Length)
        {
            rented = ArrayPool<byte>.Shared.Rent(max);
            buf = rented;
        }
        try
        {
            int n = Encoding.UTF8.GetBytes(chars, buf);
            return Get((ReadOnlySpan<byte>)buf[..n]);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string? Find(Table t, int h, ReadOnlySpan<byte> utf8)
    {
        var hashes = t.Hashes;
        var values = t.Values;
        int mask = t.Mask;
        int i = h & mask;
        while (true)
        {
            int hv = Volatile.Read(ref hashes[i]);   // acquire
            if (hv == 0) return null;
            if (hv == h)
            {
                var v = values[i]!;                  // written before the hash was published
                if (Utf8Compare.Equal(utf8, v)) return v;
            }
            i = (i + 1) & mask;
        }
    }

    private static void Insert(Table t, int h, string s)
    {
        var hashes = t.Hashes;
        int mask = t.Mask;
        int i = h & mask;
        while (hashes[i] != 0) i = (i + 1) & mask;
        t.Values[i] = s;
        Volatile.Write(ref hashes[i], h);            // release: publish after the value
    }

    private static void Grow(Shard shard)
    {
        var old = shard.Current;
        if (old.Hashes.Length >= MaxSlotsPerShard)
            throw new InvalidOperationException("StringPool shard is full.");

        var next = new Table(old.Hashes.Length * 2);
        for (int j = 0; j < old.Hashes.Length; j++)
        {
            int h = old.Hashes[j];
            if (h == 0) continue;
            int i = h & next.Mask;
            while (next.Hashes[i] != 0) i = (i + 1) & next.Mask;
            next.Values[i] = old.Values[j];
            next.Hashes[i] = h;                      // table not visible to readers yet
        }
        Volatile.Write(ref shard.Current, next);     // publish the whole table at once
    }
}
