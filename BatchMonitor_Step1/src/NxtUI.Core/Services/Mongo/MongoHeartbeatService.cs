using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NxtUI.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Mongo;

public class MongoHeartbeatService(
    MongoConnectionFactory factory,
    IOptions<HeartbeatSettings> heartbeat,
    ILogger<MongoHeartbeatService> log) : IHeartbeatService
{
    private readonly HeartbeatSettings _heartbeat = heartbeat.Value;

    public async Task<List<ServiceStatus>> GetServiceStatusesAsync(string env, DateTime? since = null, CancellationToken ct = default)
    {
        since ??= DateTime.UtcNow.AddMinutes(-_heartbeat.RecentWindowMinutes);
        var filter = Builders<HeartbeatDocument>.Filter.Gte(d => d.UpdatedDateTime, since.Value);

        log.LogDebug("heartbeat [{Env}]: querying '{Col}' updated since {Since:HH:mm:ss}",
            env, _heartbeat.CollectionName, since.Value);

        var db = factory.GetDatabase(env);
        var collection = db.GetCollection<HeartbeatDocument>(_heartbeat.CollectionName);

        var sw = Stopwatch.StartNew();
        var docs = await collection
            .Find(filter)
            .SortByDescending(d => d.UpdatedDateTime)
            .ToListAsync(ct);
        sw.Stop();

        var threshold = TimeSpan.FromSeconds(_heartbeat.IntervalSeconds * 2);
        var now = DateTime.UtcNow;

        log.LogDebug("heartbeat [{Env}]: {Count} services returned in {Ms}ms", env, docs.Count, sw.ElapsedMilliseconds);

        return docs.Select(d => new ServiceStatus
        {
            ServiceName = d.ServiceName,
            HostName = d.HostName,
            ProcessId = d.ProcessId,
            ServiceInstanceId = d.ServiceInstanceId,
            UpdatedDateTime = d.UpdatedDateTime,
            CreatedDateTime = d.CreatedDateTime,
            IsOnline = (now - d.UpdatedDateTime) <= threshold,
        }).ToList();
    }
}
