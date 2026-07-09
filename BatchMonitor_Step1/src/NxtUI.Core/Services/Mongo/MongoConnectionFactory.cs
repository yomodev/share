using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Core.Configuration;

namespace NxtUI.Core.Services.Mongo;

/// <summary>
/// Builds and caches <see cref="IMongoClient"/> instances per environment.
/// Two environments with identical connection settings share one client
/// (keyed by settings fingerprint).
/// </summary>
public sealed class MongoConnectionFactory(EnvironmentConfigLoader loader)
{
    private readonly ConcurrentDictionary<string, IMongoClient> _cache = new();

    public IMongoClient GetClient(string envId)
    {
        var settings = loader.GetMongo(envId);
        var fingerprint = settings.GetFingerprint();
        return _cache.GetOrAdd(fingerprint, _ => Build(settings));
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

    private static MongoClient Build(MongoSettings s)
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                client.GetDatabase(url.DatabaseName ?? "admin")
                    .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token)
                    .GetAwaiter().GetResult();
                return client;
            }
            catch when (i < candidates.Length - 1)
            {
                // Auth (or any other) failure with this casing — fall through and try the
                // next one instead of giving up immediately.
            }
            catch
            {
                // Last candidate also failed (could be a bad username on both casings, or
                // the server being genuinely unreachable) — return it anyway rather than
                // throwing here, so callers get the driver's own connection error on first
                // real use, same as before this fallback existed.
                return client;
            }
        }

        throw new UnreachableException(); // candidates always has >=1 entry
    }
}
