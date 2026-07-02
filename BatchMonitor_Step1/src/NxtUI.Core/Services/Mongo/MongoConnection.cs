using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NxtUI.Configuration;
using System.Security.Cryptography.X509Certificates;

namespace NxtUI.Core.Services.Mongo;

/// <summary>
/// Centralised MongoDB connection factory.
/// Register as singleton; inject wherever an IMongoClient or per-env database is needed.
/// </summary>
/// <remarks>
/// The connection string in appsettings can be a bare host string such as
/// "mongodb://host:27017". To supply credentials at runtime (e.g. the current
/// OS user or a vault-fetched secret) set <see cref="MongoSettings.Username"/>
/// and <see cref="MongoSettings.Password"/> — they are injected into the URL
/// before the client is built so they never have to be hard-coded in config.
///
/// For TLS, point <see cref="MongoSettings.TlsCertificatePath"/> at a PEM/PFX
/// file and set <see cref="MongoSettings.TlsEnabled"/> to true; this class
/// attaches the certificate to the client settings automatically.
/// </remarks>
public sealed class MongoConnection
{
    private readonly MongoSettings _settings;
    private readonly IMongoClient _client;

    public MongoConnection(IOptions<MongoSettings> options)
    {
        _settings = options.Value;
        _client = BuildClient(_settings);
    }

    public IMongoClient Client => _client;

    /// <summary>
    /// False when the connection string is the default localhost placeholder,
    /// meaning no real MongoDB has been configured. Polling is skipped in that case.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.ConnectionString) &&
        !_settings.ConnectionString.Equals("mongodb://localhost:27017", StringComparison.OrdinalIgnoreCase);

    public IMongoDatabase GetDatabase(string environmentId) =>
        _client.GetDatabase(_settings.GetDatabaseName(environmentId));

    public IMongoDatabase GetHeartbeatsDatabase(string environmentId) =>
        _client.GetDatabase(_settings.GetHeartbeatsDatabaseName(environmentId));

    // ── internals ────────────────────────────────────────────────────────────

    private static IMongoClient BuildClient(MongoSettings s)
    {
        var url = new MongoUrl(InjectCredentials(s.ConnectionString, s.Username, s.Password));
        var settings = MongoClientSettings.FromUrl(url);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);

        if (s.TlsEnabled)
        {
            settings.UseTls = true;

            if (!string.IsNullOrWhiteSpace(s.TlsCertificatePath))
            {
                var cert = new X509Certificate2(s.TlsCertificatePath, s.TlsCertificatePassword);

                settings.SslSettings = new SslSettings
                {
                    ClientCertificates = [cert],
                };
            }
        }

        return new MongoClient(settings);
    }

    /// <summary>
    /// Injects username/password into a connection string that may or may not
    /// already carry credentials. A bare "mongodb://host:27017" becomes
    /// "mongodb://user:pass@host:27017"; an existing credential pair is replaced.
    /// </summary>
    private static string InjectCredentials(string connectionString, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
            return connectionString;

        var url = new MongoUrlBuilder(connectionString)
        {
            Username = username,
            Password = password ?? string.Empty
        };

        return url.ToMongoUrl().ToString();
    }
}
