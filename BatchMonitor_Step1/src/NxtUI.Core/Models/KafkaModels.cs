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
    public long      Offset    { get; init; }
    public int       Partition { get; init; }
    public string?   Key       { get; init; }
    public string    Value     { get; init; } = string.Empty;
    public DateTime  Timestamp { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
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
}
