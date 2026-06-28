using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>Read-only Kafka monitoring operations. Safe to inject in any component.</summary>
public interface IKafkaMonitor
{
    Task<KafkaClusterInfo>                       GetClusterInfoAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicSummary>>       GetTopicsAsync(string env, CancellationToken ct = default);
    Task<KafkaTopicConfig>                       GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(string env, string topicName, CancellationToken ct = default);
    /// <param name="maxMessages">Cap on buffered messages. 0 = live/continuous — stream never ends until ct is cancelled.</param>
    IAsyncEnumerable<KafkaMessage>               TailTopicAsync(string env, string topicName, int maxMessages, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaGroupTopicLag>>         GetGroupTopicLagsAsync(string env, string groupId, CancellationToken ct = default);
}

/// <summary>Destructive Kafka admin operations. Inject only where mutations are explicitly needed.</summary>
public interface IKafkaAdmin
{
    Task DeleteTopicAsync(string env, string topicName, CancellationToken ct = default);
    Task SetTopicRetentionAsync(string env, string topicName, long retentionMs, CancellationToken ct = default);
    Task DeleteConsumerGroupAsync(string env, string groupId, CancellationToken ct = default);
}

/// <summary>Combined interface implemented by full Kafka service implementations.</summary>
public interface IKafkaService : IKafkaMonitor, IKafkaAdmin { }
