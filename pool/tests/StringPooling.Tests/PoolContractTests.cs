using System.Text;
using StringPooling;

namespace StringPooling.Tests;

/// <summary>Behaviour every pooling implementation must share.</summary>
public class PoolContractTests
{
    public static IEnumerable<object[]> Kinds() => PoolCatalog.PoolingKinds.Select(k => new object[] { k });

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static readonly string[] Samples =
    {
        "a",
        "hello",
        "Praha-Žižkov",
        "héllo wörld",
        "日本語",
        "😀 emoji + 𝄞 surrogates",
        new string('x', 1000),
        new string('é', 5000),          // long, non-ASCII: forces the rented-buffer path
        "mixed " + new string('日', 300),
    };

    [Theory, MemberData(nameof(Kinds))]
    public void Empty_input_returns_string_empty(PoolKind kind)
    {
        var pool = PoolCatalog.Create(kind, 16);
        Assert.Same(string.Empty, pool.Get(ReadOnlySpan<byte>.Empty));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Same_content_returns_same_reference(PoolKind kind)
    {
        var pool = PoolCatalog.Create(kind, 16);
        foreach (var s in Samples)
        {
            var a = pool.Get(B(s));
            var b = pool.Get(B(s));
            Assert.Equal(s, a);
            Assert.Same(a, b);
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Many_distinct_values_are_all_correct_and_stable(PoolKind kind)
    {
        const int n = 50_000;
        var pool = PoolCatalog.Create(kind, 16);   // small on purpose: forces growth

        var first = new string[n];
        for (int i = 0; i < n; i++) first[i] = pool.Get(B($"key-{i}-é"));

        for (int i = 0; i < n; i++)
        {
            Assert.Equal($"key-{i}-é", first[i]);
            Assert.Same(first[i], pool.Get(B($"key-{i}-é")));
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Invalid_utf8_is_pooled_like_anything_else(PoolKind kind)
    {
        var pool = PoolCatalog.Create(kind, 16);
        byte[][] inputs =
        {
            new byte[] { 0xFF },                     // 1 byte -> 1 char (U+FFFD): equal lengths, not ASCII
            new byte[] { 0x61, 0xFF, 0x62 },
            new byte[] { 0xC3 },                     // truncated 2-byte sequence
            new byte[] { 0xE6, 0x97 },               // truncated 3-byte sequence
        };
        foreach (var bytes in inputs)
        {
            var a = pool.Get(bytes);
            var b = pool.Get(bytes);
            Assert.Equal(Encoding.UTF8.GetString(bytes), a);
            Assert.Same(a, b);
        }
    }
}
