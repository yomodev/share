using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Core.Configuration;

namespace NxtUI.Core.Services.Mongo;

/// <summary>
/// Builds and caches <see cref="IMongoClient"/> instances per environment. Connection
/// mechanics (connection string template, timeouts, TLS, Kerberos domain) are shared
/// across every environment via <see cref="MongoSettings"/> (appsettings.json); only the
/// server and database name come from each environment's own config/{env}.json (<see
/// cref="MongoEnvSettings"/>, via <see cref="EnvironmentConfigLoader"/>). Two environments
/// that resolve to an identical connection string share one client (keyed by fingerprint).
/// </summary>
public sealed class MongoConnectionFactory(
    EnvironmentConfigLoader loader,
    IOptions<MongoSettings> options,
    ILogger<MongoConnectionFactory> log)
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
        var global = options.Value;
        var envCfg = loader.GetMongo(envId);
        var connectionString = ResolveConnectionString(global, envCfg);
        // Fingerprint on the fully-resolved connection string (not the shared template) so
        // two environments pointed at different servers never share a client, while two
        // environments that DO resolve to the same server+options correctly do.
        var fingerprint = ComputeFingerprint(connectionString, global);
        return _cache.GetOrAdd(fingerprint, _ => new Lazy<Task<MongoClient>>(() => BuildAsync(envId, global, envCfg, connectionString)));
    }

    public IMongoDatabase GetDatabase(string envId)
    {
        var global = options.Value;
        var envCfg = loader.GetMongo(envId);
        return GetClient(envId).GetDatabase(ResolveDatabaseName(global, envCfg, envId));
    }

    /// <summary>
    /// False when the connection string template can't be resolved for this environment
    /// (template empty, or it needs a <c>{server}</c> substitution that this environment's
    /// config doesn't supply) — meaning no real MongoDB has been configured for it.
    /// </summary>
    public bool IsConfigured(string envId) =>
        ResolveConnectionString(options.Value, loader.GetMongo(envId)) is not null;

    private static string? ResolveConnectionString(MongoSettings global, MongoEnvSettings envCfg)
    {
        var template = global.ConnectionString;
        if (string.IsNullOrWhiteSpace(template)) return null;
        if (!template.Contains("{server}", StringComparison.OrdinalIgnoreCase)) return template;
        if (string.IsNullOrWhiteSpace(envCfg.ServerName)) return null;
        return template.Replace("{server}", envCfg.ServerName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDatabaseName(MongoSettings global, MongoEnvSettings envCfg, string envId) =>
        !string.IsNullOrWhiteSpace(envCfg.DatabaseName) ? envCfg.DatabaseName : $"{global.DatabasePrefix}_{envId}";

    private static string ComputeFingerprint(string? connectionString, MongoSettings global)
    {
        var key = $"{connectionString}|{global.TlsEnabled}|{global.TlsCertificatePath}|{global.KerberosDomain}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..12];
    }

    private async Task<MongoClient> BuildAsync(string envId, MongoSettings global, MongoEnvSettings envCfg, string? connectionString)
    {
        if (connectionString is null)
            throw new InvalidOperationException(
                $"mongo [{envId}]: connection string could not be resolved — check Mongo:ConnectionString " +
                $"in appsettings.json and Mongo:ServerName in config/{envId}.json.");

        var url = new MongoUrl(connectionString);

        MongoClientSettings BuildOpts(string? kerberosPrincipal)
        {
            var opts = MongoClientSettings.FromUrl(url);
            opts.ConnectTimeout        = TimeSpan.FromMilliseconds(Math.Max(1000, global.ConnectionTimeoutMs));
            opts.ServerSelectionTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, global.ServerSelectionTimeoutMs));

            if (!string.IsNullOrWhiteSpace(kerberosPrincipal))
                opts.Credential = MongoCredential.CreateGssapiCredential(kerberosPrincipal);

            if (global.TlsEnabled is true)
            {
                opts.UseTls = true;
                if (!string.IsNullOrWhiteSpace(global.TlsCertificatePath))
                {
                    var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        global.TlsCertificatePath, global.TlsCertificatePassword);
                    opts.SslSettings = new SslSettings { ClientCertificates = [cert] };
                }
            }
            return opts;
        }

        // No Kerberos domain configured — connect with no credential at all (e.g. local/dev
        // Mongo with auth disabled). This is a deliberate no-op path, not a fallback.
        if (string.IsNullOrWhiteSpace(global.KerberosDomain))
            return new MongoClient(BuildOpts(null));

        // Windows-integrated auth: the account name comes from the process's own Windows
        // identity ("DOMAIN\user" — DOMAIN discarded, since it's whatever AD domain the
        // process happens to be logged into, not necessarily the Kerberos realm Mongo
        // expects); the realm comes from the configured KerberosDomain instead.
#pragma warning disable CA1416 // Validate platform compatibility
        var windowsName = WindowsIdentity.GetCurrent().Name;
#pragma warning restore CA1416 // Validate platform compatibility
        var backslash = windowsName.IndexOf('\\');
        var accountName = backslash >= 0 ? windowsName[(backslash + 1)..] : windowsName;

        // Some deployments register the Mongo user's external (GSSAPI) principal as either
        // all-uppercase or all-lowercase depending on how it was provisioned, and it doesn't
        // always match the account name's own casing. Try uppercase first, then lowercase,
        // keeping whichever one actually authenticates — same pattern as the SCRAM
        // username-casing fallback this replaces. Build() only runs once per settings
        // fingerprint (the cache in GetLazyBuild), so the resolved casing is effectively
        // remembered for the rest of the process's lifetime.
        var candidates = new[]
            {
                $"{accountName.ToUpperInvariant()}@{global.KerberosDomain}",
                $"{accountName.ToLowerInvariant()}@{global.KerberosDomain}",
            }
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
                log.LogDebug(ex, "mongo [{Env}]: Kerberos principal '{Candidate}' failed, trying next casing", envId, candidates[i]);
            }
            catch (Exception ex)
            {
                // Last candidate also failed (could be a bad account/domain mapping on both
                // casings, or the server being genuinely unreachable) — this IS a genuine
                // connection failure. Still return the client rather than throwing here, so
                // callers get the driver's own connection error on first real use, same as
                // before this fallback existed.
                log.LogError(ex, "mongo [{Env}]: failed to connect/authenticate after trying {Count} Kerberos principal casing(s)", envId, candidates.Length);
                return client;
            }
        }

        throw new UnreachableException(); // candidates always has >=1 entry
    }
}
