namespace StringPooling;

public enum PoolKind
{
    NoPool,
    Intern,
    Dictionary,
    ConcurrentDictionary,
    HashKeyed_XxHash3,
    Locked_HashKeyed_XxHash3,
    Sharded1_XxHash3,       // one shard: shows the cost/benefit of sharding itself
    Sharded_Custom,
    Sharded_XxHash3,
    Sharded_XxHash64,
    Sharded_Fnv1a,
}

public static class PoolCatalog
{
    public static IEnumerable<PoolKind> AllKinds => Enum.GetValues<PoolKind>();

    /// <summary>Everything that actually shares string instances (i.e. all but NoPool).</summary>
    public static IEnumerable<PoolKind> PoolingKinds => AllKinds.Where(k => k != PoolKind.NoPool);

    public static IEnumerable<PoolKind> ThreadSafeKinds =>
        AllKinds.Where(k => Create(k, 16).IsThreadSafe);

    public static IStringPool Create(PoolKind kind, int expectedCount) => kind switch
    {
        PoolKind.NoPool => new NoPool(),
        PoolKind.Intern => new InternPool(),
        PoolKind.Dictionary => new DictionaryPool(expectedCount),
        PoolKind.ConcurrentDictionary => new ConcurrentDictionaryPool(expectedCount),
        PoolKind.HashKeyed_XxHash3 => new HashKeyedDictionaryPool<XxHash3Hasher>(expectedCount),
        PoolKind.Locked_HashKeyed_XxHash3 => new LockedPool(new HashKeyedDictionaryPool<XxHash3Hasher>(expectedCount)),
        PoolKind.Sharded1_XxHash3 => new ShardedStringPool<XxHash3Hasher>(expectedCount, shardCount: 1),
        PoolKind.Sharded_Custom => new ShardedStringPool<CustomHasher>(expectedCount),
        PoolKind.Sharded_XxHash3 => new ShardedStringPool<XxHash3Hasher>(expectedCount),
        PoolKind.Sharded_XxHash64 => new ShardedStringPool<XxHash64Hasher>(expectedCount),
        PoolKind.Sharded_Fnv1a => new ShardedStringPool<Fnv1aHasher>(expectedCount),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
