namespace StringPooling.Analysis;

public delegate ulong HashFunc(ReadOnlySpan<byte> data);

public sealed record NamedHasher(string Name, HashFunc Fn);

public static class HasherCatalog
{
    public static readonly NamedHasher[] All =
    {
        new("Custom", new HashFunc(CustomHasher.Hash)),
        new("XxHash3", new HashFunc(XxHash3Hasher.Hash)),
        new("XxHash64", new HashFunc(XxHash64Hasher.Hash)),
        new("Fnv1a", new HashFunc(Fnv1aHasher.Hash)),
    };

    /// <summary>Hashers expected to pass the statistical tests (FNV-1a is reported but not asserted).</summary>
    public static IEnumerable<NamedHasher> Strong => All.Where(h => h.Name != "Fnv1a");

    public static NamedHasher Get(string name) => All.First(h => h.Name == name);
}
