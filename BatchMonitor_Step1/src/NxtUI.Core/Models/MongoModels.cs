namespace NxtUI.Core.Models;

public record MongoDatabaseInfo
{
    public string Name { get; init; } = string.Empty;
    public long CollectionCount { get; init; }
    public long SizeBytes { get; init; }
}

public record MongoCollectionSummary
{
    public string Name { get; init; } = string.Empty;
    public long DocumentCount { get; init; }
    public long AvgDocSizeBytes { get; init; }
    public long StorageSizeBytes { get; init; }
    public int IndexCount { get; init; }
    /// <summary>False while background enrichment is still in flight.</summary>
    public bool StatsLoaded { get; init; }
}

public record MongoDocument
{
    public string Id { get; init; } = string.Empty;
    public string Json { get; init; } = string.Empty;
    public DateTime? Timestamp { get; init; }
}

public record MongoIndexInfo
{
    public string Name { get; init; } = string.Empty;
    public string Keys { get; init; } = string.Empty;
    public bool Unique { get; init; }
    public bool Sparse { get; init; }
}

public record MongoCollectionDetails
{
    public MongoCollectionSummary Summary { get; init; } = new();
    public IReadOnlyList<MongoIndexInfo> Indexes { get; init; } = [];
}
