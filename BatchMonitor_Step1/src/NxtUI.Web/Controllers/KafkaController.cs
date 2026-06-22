using NxtUI.Core.Models;
using NxtUI.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace NxtUI.Controllers;

[ApiController]
[Route("api/{env}/kafka")]
public class KafkaController : ControllerBase
{
    private readonly IKafkaService _svc;

    public KafkaController(IKafkaService svc) => _svc = svc;

    [HttpGet("cluster")]
    public async Task<ActionResult<KafkaClusterInfo>> GetCluster(string env, CancellationToken ct)
        => Ok(await _svc.GetClusterInfoAsync(env, ct));

    [HttpGet("topics")]
    public async Task<ActionResult<IReadOnlyList<KafkaTopicSummary>>> GetTopics(string env, CancellationToken ct)
        => Ok(await _svc.GetTopicsAsync(env, ct));

    [HttpGet("topics/{topicName}/config")]
    public async Task<ActionResult<KafkaTopicConfig>> GetTopicConfig(string env, string topicName, CancellationToken ct)
        => Ok(await _svc.GetTopicConfigAsync(env, topicName, ct));

    [HttpGet("topics/{topicName}/consumergroups")]
    public async Task<ActionResult<IReadOnlyList<KafkaTopicConsumerGroup>>> GetTopicConsumerGroups(string env, string topicName, CancellationToken ct)
        => Ok(await _svc.GetTopicConsumerGroupsAsync(env, topicName, ct));
}
