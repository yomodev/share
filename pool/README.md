# StringPoolLab

A UTF-8 string pool for huge CSV files (.NET 8), with a pluggable hash function,
tests, and benchmarks comparing several implementations.

## Layout

```
src/StringPooling/          library
  ShardedStringPool<THasher>   the main implementation (lock-free hits, sharded locked inserts)
  Hashers.cs                   CustomHasher, XxHash3Hasher, XxHash64Hasher, Fnv1aHasher (baseline)
  Baselines.cs                 NoPool, InternPool, DictionaryPool, ConcurrentDictionaryPool,
                               HashKeyedDictionaryPool, LockedPool
  PoolCatalog.cs               PoolKind enum + factory used by tests and benchmarks
  Analysis/                    hash-quality checks (collisions, chi-square, avalanche)
tests/StringPooling.Tests/  xunit
benchmarks/StringPooling.Benchmarks/  BenchmarkDotNet + quality report
```

## Run

```
dotnet test -c Release

# hash quality numbers for all hashers (incl. weak FNV-1a)
dotnet run -c Release --project benchmarks/StringPooling.Benchmarks -- quality

# benchmarks (full run is long; add --job short for a quicker pass)
dotnet run -c Release --project benchmarks/StringPooling.Benchmarks -- --filter "*HashBenchmarks*"
dotnet run -c Release --project benchmarks/StringPooling.Benchmarks -- --filter "*SingleThreadBenchmarks*" --job short
dotnet run -c Release --project benchmarks/StringPooling.Benchmarks -- --filter "*ParallelBenchmarks*" --job short
```

## Reading the benchmarks

* `SingleThreadBenchmarks`: a fresh pre-sized pool per run, 4M fields, skewed value distribution,
  `Distinct` = 1k (very repetitive) or 1M (long tail), ASCII or mixed non-ASCII.
  Look at Mean and Allocated. `NoPool` is the floor for allocations, `Sharded1_*` vs `Sharded_*`
  shows the cost of sharding itself.
* `ParallelBenchmarks`: same idea with 1/2/4/8 threads sharing one pool, thread-safe pools only.
* `HashBenchmarks`: raw hash throughput per key length.
* All pools are called through `IStringPool`, so interface dispatch is a constant added to every row.
* Synthetic data is only a proxy. Replace `FieldData.Generate` with slices of your real file before deciding.
* The GC runs in the default (workstation, concurrent) mode on purpose, since you can't switch to server GC.

## Notes

* Equality on a hit compares UTF-8 bytes against the stored string without allocating
  (`Utf8Compare.Equal`). Equal lengths do not imply ASCII: an invalid byte such as 0xFF decodes
  to one U+FFFD char, so the slow path decodes before declaring a mismatch.
* Entries are never removed. That is what makes the lock-free read path safe.
* On .NET 9+ you could also use `Dictionary<string,string>.GetAlternateLookup<ReadOnlySpan<char>>()`.
