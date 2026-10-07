namespace StringPooling;

/// <summary>A pool that turns UTF-8 field bytes into (shared) string instances.</summary>
public interface IStringPool
{
    string Get(ReadOnlySpan<byte> utf8);
    bool IsThreadSafe { get; }
}

/// <summary>
/// Hash strategy over UTF-8 bytes. Implemented as a struct so the pool's generic code is
/// specialized per hasher and the call can be inlined (no delegate / interface dispatch).
/// </summary>
public interface IUtf8Hasher
{
    static abstract ulong Hash(ReadOnlySpan<byte> data);
}
