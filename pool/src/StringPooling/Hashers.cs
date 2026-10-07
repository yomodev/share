using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StringPooling;

/// <summary>The hand-written hash from the conversation: 8 bytes/step multiply-rotate + murmur3 fmix64.</summary>
public readonly struct CustomHasher : IUtf8Hasher
{
    private const ulong K1 = 0x9E3779B185EBCA87UL;
    private const ulong K2 = 0xC2B2AE3D27D4EB4FUL;

    public static ulong Hash(ReadOnlySpan<byte> d)
    {
        int len = d.Length;
        if (len == 0) return 0x2545F4914F6CDD1DUL;

        ref byte r = ref MemoryMarshal.GetReference(d);
        ulong h = (ulong)len * K1;

        int i = 0;
        for (; i + 8 <= len; i += 8)
            h = BitOperations.RotateLeft(
                    h ^ (Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, i)) * K2), 29) * K1;

        ulong tail;
        if (len >= 8)
            tail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, len - 8));
        else if (len >= 4)
            tail = ((ulong)Unsafe.ReadUnaligned<uint>(ref r) << 32)
                 | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref r, len - 4));
        else
            tail = ((ulong)r << 16)
                 | ((ulong)Unsafe.Add(ref r, len >> 1) << 8)
                 | Unsafe.Add(ref r, len - 1);

        h ^= tail * K2;
        return FMix64(h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FMix64(ulong h)
    {
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }
}

/// <summary>XXH3 64-bit from System.IO.Hashing (vectorized, well-vetted).</summary>
public readonly struct XxHash3Hasher : IUtf8Hasher
{
    public static ulong Hash(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);
}

/// <summary>XXH64 from System.IO.Hashing.</summary>
public readonly struct XxHash64Hasher : IUtf8Hasher
{
    public static ulong Hash(ReadOnlySpan<byte> data) => XxHash64.HashToUInt64(data);
}

/// <summary>FNV-1a 64-bit, byte at a time. Included as a deliberately simple/weak baseline.</summary>
public readonly struct Fnv1aHasher : IUtf8Hasher
{
    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}
