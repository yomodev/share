using System.Collections.Concurrent;
using MongoDB.Driver;
using NxtUI.Configuration;

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
        var settings    = loader.GetMongo(envId);
        var fingerprint = settings.GetFingerprint();
        return _cache.GetOrAdd(fingerprint, _ => Build(settings));
    }

    public IMongoDatabase GetDatabase(string envId)
    {
        var settings = loader.GetMongo(envId);
        return GetClient(envId).GetDatabase(settings.GetDatabaseName(envId));
    }

    private static IMongoClient Build(MongoSettings s)
    {
        var url  = new MongoUrl(s.ConnectionString);
        var opts = MongoClientSettings.FromUrl(url);

        if (!string.IsNullOrWhiteSpace(s.Username) && !string.IsNullOrWhiteSpace(s.Password))
            opts.Credential = MongoCredential.CreateCredential(
                url.DatabaseName ?? "admin", s.Username, s.Password);

        if (s.TlsEnabled)
        {
            opts.UseTls = true;
            if (!string.IsNullOrWhiteSpace(s.TlsCertificatePath))
            {
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    s.TlsCertificatePath, s.TlsCertificatePassword);
                opts.SslSettings = new SslSettings { ClientCertificates = [cert] };
            }
        }

        return new MongoClient(opts);
    }
}
