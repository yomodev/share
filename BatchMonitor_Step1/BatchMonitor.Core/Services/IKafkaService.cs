using BatchMonitor.Core.Models;

namespace BatchMonitor.Core.Services;

public interface IKafkaService
{
    Task<KafkaClusterInfo>                    GetClusterInfoAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicSummary>>    GetTopicsAsync(string env, CancellationToken ct = default);
    Task<KafkaTopicConfig>                    GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(string env, string topicName, CancellationToken ct = default);
    IAsyncEnumerable<KafkaMessage> TailTopicAsync(string env, string topicName, int maxMessages, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(string env, CancellationToken ct = default);
    Task<IReadOnlyList<KafkaGroupTopicLag>>         GetGroupTopicLagsAsync(string env, string groupId, CancellationToken ct = default);
}
