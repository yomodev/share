using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BatchMonitor.Models;

public class HeartbeatDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

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
}
