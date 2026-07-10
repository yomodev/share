using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>Read-only Kafka monitoring operations. Safe to inject in any component.</summary>
public interface IKafkaMonitor
{
    Task<KafkaClusterInfo> GetClusterInfoAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicSummary>> GetTopicsAsync(string env, CancellationToken ct = default);
    Task<KafkaTopicConfig> GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default);

    /// <summary>
    /// Cheap, batched enrichment for a whole topic list — cleanup policy (for the "~"
    /// compacted-topic marker) and an estimated message count (sum of high-low watermark
    /// per partition; a live snapshot, not a total-ever-produced count). Uses one shared
    /// admin client and a small constant number of broker round trips regardless of how
    /// many topics are passed in — unlike calling GetTopicConfigAsync once per topic.
    /// </summary>
    Task<IReadOnlyDictionary<string, KafkaTopicEnrichment>> GetTopicEnrichmentAsync(
        string env, IReadOnlyList<KafkaTopicSummary> topics, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(string env, string topicName, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaGroupTopicLag>> GetGroupTopicLagsAsync(string env, string groupId, CancellationToken ct = default);

    /// <summary>
    /// Streams messages from a topic.
    /// The <paramref name="directive"/> controls which partitions and offsets to seek to;
    /// pass <see cref="KafkaSeekDirective.Default"/> to stream from the beginning.
    /// The stream ends when the cancellation token is cancelled (Pause/Reset) or when a
    /// bounded end condition in the directive is reached.
    /// </summary>
    /// <param name="uncapped">
    /// When true, ignores KafkaSettings.MaxFetchMessages (that safety cap exists for
    /// interactive UI sessions; long-running background consumers need to keep tailing
    /// indefinitely instead of silently stopping after N messages).
    /// </param>
    IAsyncEnumerable<KafkaMessage> TailTopicAsync(
        string env, string topicName, KafkaSeekDirective directive, CancellationToken ct = default,
        bool uncapped = false);

    /// <summary>
    /// Returns watermark offsets and first/last message timestamps for each partition.
    /// Used by the Stats popover in the topic inspector.
    /// </summary>
    Task<IReadOnlyList<KafkaPartitionStats>> GetPartitionStatsAsync(
        string env, string topicName, CancellationToken ct = default);

    /// <summary>
    /// Re-fetches the raw <c>byte[]</c> payload for a single message by partition + offset.
    /// Used for the download path — bytes are never cached between requests.
    /// Returns null if the offset is no longer available (e.g. compacted away).
    /// </summary>
    Task<byte[]?> FetchRawBytesAsync(
        string env, string topicName, int partition, long offset, CancellationToken ct = default);
}

/// <summary>Destructive Kafka admin operations. Inject only where mutations are explicitly needed.</summary>
public interface IKafkaAdmin
{
    Task DeleteTopicAsync(string env, string topicName, CancellationToken ct = default);
    Task SetTopicRetentionAsync(string env, string topicName, long retentionMs, CancellationToken ct = default);
    Task DeleteConsumerGroupAsync(string env, string groupId, CancellationToken ct = default);

    /// <summary>Reads a topic's current effective retention.ms, or null if it couldn't be read.</summary>
    Task<long?> GetTopicRetentionMsAsync(string env, string topicName, CancellationToken ct = default);

    /// <summary>
    /// "Purges" a topic without deleting it and without needing Delete ACL on it: drops
    /// retention.ms to a near-zero value (making all current segments eligible for the
    /// broker's own log cleaner), then restores the topic's original retention.ms. This is
    /// NOT immediate — actual segment removal happens on the broker's own cleanup schedule
    /// (log.retention.check.interval.ms), which a client cannot trigger on demand.
    /// </summary>
    Task<KafkaPurgeResult> PurgeTopicAsync(string env, string topicName, CancellationToken ct = default);
}

/// <summary>Result of the (eventual, retention-based) purge of one topic.</summary>
public sealed record KafkaPurgeResult(string Topic, bool Success, long? RestoredRetentionMs, string? Error);

/// <summary>Combined interface implemented by full Kafka service implementations.</summary>
public interface IKafkaService : IKafkaMonitor, IKafkaAdmin { }
