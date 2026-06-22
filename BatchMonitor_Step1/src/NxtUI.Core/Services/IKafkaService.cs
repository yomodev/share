using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

public interface IKafkaService
{
    Task<KafkaClusterInfo>                    GetClusterInfoAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicSummary>>    GetTopicsAsync(string env, CancellationToken ct = default);
    Task<KafkaTopicConfig>                    GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(string env, string topicName, CancellationToken ct = default);
    /// <param name="maxMessages">Cap on buffered messages. 0 = live/continuous — stream never ends until ct is cancelled.</param>
    IAsyncEnumerable<KafkaMessage> TailTopicAsync(string env, string topicName, int maxMessages, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaGroupTopicLag>>         GetGroupTopicLagsAsync(string env, string groupId, CancellationToken ct = default);
    Task DeleteTopicAsync(string env, string topicName, CancellationToken ct = default);
    Task SetTopicRetentionAsync(string env, string topicName, long retentionMs, CancellationToken ct = default);
    Task DeleteConsumerGroupAsync(string env, string groupId, CancellationToken ct = default);
}
