using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Core.Configuration;

namespace NxtUI.Core.Services.Mongo;

/// <summary>
/// Builds and caches <see cref="IMongoClient"/> instances per environment.
/// Two environments with identical connection settings share one client
/// (keyed by settings fingerprint).
/// </summary>
public sealed class MongoConnectionFactory(EnvironmentConfigLoader loader, ILogger<MongoConnectionFactory> log)
{
    // Lazy<Task<...>> (not a plain IMongoClient) so concurrent first-time callers for the
    // same fingerprint share one in-flight build instead of racing duplicate connections,
    // AND so GetClientAsync can actually await it without blocking a thread — see below.
    private readonly ConcurrentDictionary<string, Lazy<Task<MongoClient>>> _cache = new();

    public IMongoClient GetClient(string envId) =>
        GetLazyBuild(envId).Value.GetAwaiter().GetResult();

    /// <summary>
    /// Same client as <see cref="GetClient"/> (shares the same cache — calling this first
    /// for a not-yet-built fingerprint doesn't cause a second, separate connection), but
    /// waits for it asynchronously. <paramref name="ct"/> only bounds THIS caller's wait —
    /// the build itself keeps running in the background either way (so it's still hot in
    /// the cache for the next call), since there's no way to actually abort a driver-level
    /// server-selection probe partway through.
    /// </summary>
    public async Task<IMongoClient> GetClientAsync(string envId, CancellationToken ct = default)
    {
        var buildTask = GetLazyBuild(envId).Value;
        if (ct.CanBeCanceled && !buildTask.IsCompleted)
        {
            var delay = Task.Delay(Timeout.Infinite, ct);
            var completed = await Task.WhenAny(buildTask, delay);
            if (completed == delay) ct.ThrowIfCancellationRequested();
        }
        return await buildTask;
    }

    private Lazy<Task<MongoClient>> GetLazyBuild(string envId)
    {
        var settings = loader.GetMongo(envId);
        var fingerprint = settings.GetFingerprint();
        return _cache.GetOrAdd(fingerprint, _ => new Lazy<Task<MongoClient>>(() => BuildAsync(envId, settings)));
    }

    public IMongoDatabase GetDatabase(string envId)
    {
        var settings = loader.GetMongo(envId);
        return GetClient(envId).GetDatabase(settings.DatabaseName);
    }

    /// <summary>
    /// False when the connection string is the default localhost placeholder,
    /// meaning no real MongoDB has been configured for this environment.
    /// </summary>
    public bool IsConfigured(string envId)
    {
        var cs = loader.GetMongo(envId).ConnectionString;
        return !string.IsNullOrWhiteSpace(cs);
    }

    private async Task<MongoClient> BuildAsync(string envId, MongoSettings s)
    {
        var url = new MongoUrl(s.ConnectionString);

        MongoClientSettings BuildOpts(string? username)
        {
            var opts = MongoClientSettings.FromUrl(url);
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(s.Password))
                opts.Credential = MongoCredential.CreateCredential(
                    url.DatabaseName ?? "admin", username, s.Password);

            if (s.TlsEnabled is true)
            {
                opts.UseTls = true;
                if (!string.IsNullOrWhiteSpace(s.TlsCertificatePath))
                {
                    var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        s.TlsCertificatePath, s.TlsCertificatePassword);
                    opts.SslSettings = new SslSettings { ClientCertificates = [cert] };
                }
            }
            return opts;
        }

        if (string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrWhiteSpace(s.Password))
            return new MongoClient(BuildOpts(s.Username));

        // Some deployments register the Mongo user as either all-uppercase or
        // all-lowercase depending on how it was provisioned, and the configured
        // username doesn't always match. Try uppercase first, then lowercase,
        // keeping whichever one actually authenticates. Build() only runs once per
        // settings fingerprint (GetClient's cache), so the resolved casing is
        // effectively remembered for the rest of the process's lifetime.
        var candidates = new[] { s.Username.ToUpperInvariant(), s.Username.ToLowerInvariant() }
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < candidates.Length; i++)
        {
            var client = new MongoClient(BuildOpts(candidates[i]));
            try
            {
                // Bound just this probe, not the client's own settings, so a genuinely
                // unreachable server doesn't hang here for the driver's full default timeout.
                // Genuinely async (not .GetAwaiter().GetResult()) — this runs inside the
                // Lazy<Task<...>> cache, so a blocking call here would tie up a thread-pool
                // thread for the whole probe on every first-time caller, and — more
                // importantly — would defeat GetClientAsync's ability to stop WAITING on a
                // slow build without blocking (see GetClientAsync above).
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.GetDatabase(url.DatabaseName ?? "admin")
                    .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
                return client;
            }
            catch (Exception ex) when (i < candidates.Length - 1)
            {
                // Auth (or any other) failure with this casing — fall through and try the
                // next one instead of giving up immediately. Routine/expected as part of
                // the fallback mechanism, not a genuine connection failure yet.
                log.LogDebug(ex, "mongo [{Env}]: candidate username '{Candidate}' failed, trying next casing", envId, candidates[i]);
            }
            catch (Exception ex)
            {
                // Last candidate also failed (could be a bad username on both casings, or
                // the server being genuinely unreachable) — this IS a genuine connection
                // failure. Still return the client rather than throwing here, so callers
                // get the driver's own connection error on first real use, same as before
                // this fallback existed.
                log.LogError(ex, "mongo [{Env}]: failed to connect/authenticate after trying {Count} username casing(s)", envId, candidates.Length);
                return client;
            }
        }

        throw new UnreachableException(); // candidates always has >=1 entry
    }
}
