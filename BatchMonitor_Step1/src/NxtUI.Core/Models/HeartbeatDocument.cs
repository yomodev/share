using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NxtUI.Core.Models;

[BsonIgnoreExtraElements]
public class HeartbeatDocument
{
    [BsonId]
    public required string Id { get; set; }

    [BsonElement("ServiceName")]
    public string ServiceName { get; set; } = string.Empty;

    [BsonElement("UpdatedDateTime")]
    public DateTime UpdatedDateTime { get; set; }

    [BsonElement("CreatedDateTime")]
    public DateTime CreatedDateTime { get; set; }

    [BsonElement("ProcessId")]
    public int ProcessId { get; set; }

    [BsonElement("HostName")]
    public string HostName { get; set; } = string.Empty;

    /// <summary>GUID (dashes removed) identifying this specific process instance —
    /// used to match this heartbeat to its live Kafka metrics stream.</summary>
    [BsonElement("ServiceInstanceId")]
    public string ServiceInstanceId { get; set; } = string.Empty;
}
