using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace StringPooling;

/// <summary>No pooling at all: one new string per field. The "do nothing" baseline.</summary>
public sealed class NoPool : IStringPool
{
    public bool IsThreadSafe => true;
    public string Get(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}

/// <summary>string.Intern on a freshly decoded string. Never frees, global lock-ish table.</summary>
public sealed class InternPool : IStringPool
{
    public bool IsThreadSafe => true;
    public string Get(ReadOnlySpan<byte> utf8) =>
        utf8.IsEmpty ? string.Empty : string.Intern(Encoding.UTF8.GetString(utf8));
}

/// <summary>Decode first, then Dictionary&lt;string,string&gt; lookup. Allocates a string per field.</summary>
public sealed class DictionaryPool : IStringPool
{
    private readonly Dictionary<string, string> _map;
    public DictionaryPool(int capacity = 0) => _map = new(capacity);
    public bool IsThreadSafe => false;

    public string Get(ReadOnlySpan<byte> utf8)
    {
        string s = Encoding.UTF8.GetString(utf8);
        if (_map.TryGetValue(s, out var existing)) return existing;
        _map.Add(s, s);
        return s;
    }
}

/// <summary>Decode first, then ConcurrentDictionary.GetOrAdd. Allocates a string per field.</summary>
public sealed class ConcurrentDictionaryPool : IStringPool
{
    private readonly ConcurrentDictionary<string, string> _map;

    public ConcurrentDictionaryPool(int capacity = 31) =>
        _map = new(Environment.ProcessorCount, Math.Max(capacity, 31));

    public bool IsThreadSafe => true;

    public string Get(ReadOnlySpan<byte> utf8)
    {
        string s = Encoding.UTF8.GetString(utf8);
        return _map.GetOrAdd(s, s);
    }
}

/// <summary>
/// Dictionary keyed by the 64-bit hash (no string allocation on hit), with verification and
/// a side table for real collisions. Single-threaded.
/// </summary>
public sealed class HashKeyedDictionaryPool<THasher> : IStringPool where THasher : IUtf8Hasher
{
    private readonly Dictionary<ulong, string> _map;
    private Dictionary<ulong, List<string>>? _collisions;

    public HashKeyedDictionaryPool(int capacity = 0) => _map = new(capacity);
    public bool IsThreadSafe => false;

    public string Get(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return string.Empty;

        ulong h = THasher.Hash(utf8);
        ref string? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_map, h, out bool exists);
        if (!exists)
        {
            string s = Encoding.UTF8.GetString(utf8);
            slot = s;
            return s;
        }
        if (Utf8Compare.Equal(utf8, slot!)) return slot!;
        return GetFromCollisions(h, utf8);
    }

    private string GetFromCollisions(ulong h, ReadOnlySpan<byte> utf8)
    {
        _collisions ??= new();
        if (!_collisions.TryGetValue(h, out var list))
            _collisions[h] = list = new List<string>(2);

        foreach (var item in list)
            if (Utf8Compare.Equal(utf8, item)) return item;

        string created = Encoding.UTF8.GetString(utf8);
        list.Add(created);
        return created;
    }
}

/// <summary>Coarse single lock around any pool: the naive way to make a pool thread-safe.</summary>
public sealed class LockedPool : IStringPool
{
    private readonly IStringPool _inner;
    private readonly object _lock = new();

    public LockedPool(IStringPool inner) => _inner = inner;
    public bool IsThreadSafe => true;

    public string Get(ReadOnlySpan<byte> utf8)
    {
        lock (_lock) return _inner.Get(utf8);
    }
}
