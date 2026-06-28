using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NxtUI.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Mongo;

public class MongoHeartbeatService : IHeartbeatService
{
    private readonly HeartbeatSettings             _heartbeat;
    private readonly MongoConnection               _connection;
    private readonly ILogger<MongoHeartbeatService> _log;

    public MongoHeartbeatService(MongoConnection connection, IOptions<HeartbeatSettings> heartbeat, ILogger<MongoHeartbeatService> log)
    {
        _connection = connection;
        _heartbeat  = heartbeat.Value;
        _log        = log;
    }

    public async Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, CancellationToken ct = default)
    {
        _log.LogDebug("heartbeat [{Env}]: querying collection '{Col}'", env, _heartbeat.CollectionName);

        var db         = _connection.GetHeartbeatsDatabase(env);
        var collection = db.GetCollection<HeartbeatDocument>(_heartbeat.CollectionName);

        var docs = await collection
            .Find(Builders<HeartbeatDocument>.Filter.Empty)
            .SortByDescending(d => d.UpdatedDateTime)
            .Limit(5000)
            .ToListAsync(ct);

        var threshold = TimeSpan.FromSeconds(_heartbeat.IntervalSeconds * 2);
        var now       = DateTime.UtcNow;

        _log.LogDebug("heartbeat [{Env}]: {Count} documents returned", env, docs.Count);

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
