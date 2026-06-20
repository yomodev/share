namespace BatchMonitor.Core.Models;

public record MongoDatabaseInfo
{
    public string Name            { get; init; } = string.Empty;
    public int    CollectionCount { get; init; }
    public long   SizeBytes       { get; init; }
}

public record MongoCollectionSummary
{
    public string Name             { get; init; } = string.Empty;
    public long   DocumentCount    { get; init; }
    public long   AvgDocSizeBytes  { get; init; }
    public long   StorageSizeBytes { get; init; }
    public int    IndexCount       { get; init; }
}

public record MongoDocument
{
    public string    Id        { get; init; } = string.Empty;
    public string    Json      { get; init; } = string.Empty;
    public DateTime? Timestamp { get; init; }
}
