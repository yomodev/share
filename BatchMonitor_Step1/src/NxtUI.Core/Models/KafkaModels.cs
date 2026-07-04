namespace NxtUI.Core.Models;

public record KafkaTopicConfig
{
    public long   RetentionMs       { get; init; }
    public string CleanupPolicy     { get; init; } = "delete";
    public long   MaxMessageBytes   { get; init; }
    public int    MinInSyncReplicas { get; init; }
    public string CompressionType   { get; init; } = "producer";
    public int    PartitionCount    { get; init; }
    public int    ReplicationFactor { get; init; }
}

public record KafkaTopicConsumerGroup
{
    public string GroupId  { get; init; } = string.Empty;
    public string State    { get; init; } = string.Empty;
    public long   TotalLag { get; init; }
}


public record KafkaBroker
{
    public int    Id       { get; init; }
    public string Host     { get; init; } = string.Empty;
    public int    Port     { get; init; }
    public bool   IsOnline { get; init; }
}

public record KafkaClusterInfo
{
    public string            ClusterId    { get; init; } = string.Empty;
    public int               ControllerId { get; init; }
    public List<KafkaBroker> Brokers      { get; init; } = new();
}

public record KafkaConsumerGroupOverview
{
    public string       GroupId    { get; init; } = string.Empty;
    public string       State      { get; init; } = string.Empty;
    public int          TopicCount { get; init; }
    public long         TotalLag   { get; init; }
    public List<string> Topics     { get; init; } = new();
}

public record KafkaGroupTopicLag
{
    public string TopicName  { get; init; } = string.Empty;
    public int    Partitions { get; init; }
    public long   Lag        { get; init; }
}

public record KafkaMessage
{
    public long     Offset      { get; init; }
    public int      Partition   { get; init; }
    public string?  Key         { get; init; }
    public DateTime Timestamp   { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();

    // ── Deserialized payload ─────────────────────────────────────────────────

    /// <summary>JSON representation of the message payload after proto deserialization.</summary>
    public string? JsonPayload { get; init; }

    /// <summary>Resolved proto type name, "ProtoMsg", or "unknown".</summary>
    public string PayloadType { get; init; } = "unknown";

    /// <summary>Original byte size of the raw payload before deserialization.</summary>
    public int RawSizeBytes { get; init; }
}

public record KafkaPartitionStats
{
    public int      Partition              { get; init; }
    public long     LowWatermark           { get; init; }
    public long     HighWatermark          { get; init; }
    public long     MessageCount           => Math.Max(0, HighWatermark - LowWatermark);
    public DateTime? FirstMessageTimestamp { get; init; }
    public DateTime? LastMessageTimestamp  { get; init; }
}

/// <summary>
/// Consumer seek directives extracted from the filter string by
/// <see cref="NxtUI.Filtering.KafkaFilterExtractor"/>.
/// </summary>
public record KafkaSeekDirective
{
    public static readonly KafkaSeekDirective Default = new();

    /// <summary>Explicit partition set. Null = all partitions.</summary>
    public IReadOnlySet<int>? Partitions  { get; init; }

    public long?     OffsetFrom     { get; init; }
    public long?     OffsetTo       { get; init; }
    public DateTime? TimestampFrom  { get; init; }
    public DateTime? TimestampTo    { get; init; }

    /// <summary>Fetch the last N messages per partition (overrides Offset/Timestamp).</summary>
    public int? Latest { get; init; }
}

public record KafkaTopicSummary
{
    public string Name              { get; init; } = string.Empty;
    public int    PartitionCount    { get; init; }
    public int    ReplicationFactor { get; init; }
    public long   MessageCount      { get; init; }

    /// <summary>Kafka cleanup.policy value: "delete", "compact", or "compact,delete".</summary>
    public string CleanupPolicy { get; init; } = "delete";

    /// <summary>retention.ms value. -1 means infinite.</summary>
    public long RetentionMs { get; init; }

    /// <summary>False until per-topic config has been loaded from the broker.</summary>
    public bool ConfigLoaded { get; init; }
}

/// <summary>Cheap, batched per-topic enrichment — see IKafkaMonitor.GetTopicEnrichmentAsync.</summary>
public record KafkaTopicEnrichment(string CleanupPolicy, long MessageCount);
