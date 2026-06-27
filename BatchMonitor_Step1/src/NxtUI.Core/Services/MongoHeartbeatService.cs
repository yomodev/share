using NxtUI.Configuration;
using NxtUI.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace NxtUI.Services;

public class MongoHeartbeatService : IHeartbeatService
{
    private readonly MongoSettings _mongo;
    private readonly HeartbeatSettings _heartbeat;
    private readonly MongoClient _client;

    public MongoHeartbeatService(IOptions<MongoSettings> mongo, IOptions<HeartbeatSettings> heartbeat)
    {
        _mongo     = mongo.Value;
        _heartbeat = heartbeat.Value;
        _client    = new MongoClient(_mongo.ConnectionString);
    }

    public async Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default)
    {
        var db         = _client.GetDatabase(_mongo.GetDatabaseName(env));
        var collection = db.GetCollection<HeartbeatDocument>(_heartbeat.CollectionName);

        var docs = await collection
            .Find(Builders<HeartbeatDocument>.Filter.Empty)
            .SortByDescending(d => d.UpdatedDateTime)
            .Limit(5000)
            .ToListAsync(ct);

        var threshold = TimeSpan.FromSeconds(_heartbeat.IntervalSeconds * 2);
        var now       = DateTime.UtcNow;

        return docs.Select(d => new ServiceStatus
        {
            ServiceName     = d.ServiceName,
            HostName        = d.HostName,
            ProcessId       = d.ProcessId,
            UpdatedDateTime = d.UpdatedDateTime,
            CreatedDateTime = d.CreatedDateTime,
            IsOnline        = (now - d.UpdatedDateTime) <= threshold,
        }).ToList();
    }
}
