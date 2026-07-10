namespace NxtUI.Core.Configuration;

/// <summary>
/// MongoDB connection settings shared across every environment — bound from
/// appsettings.json's "Mongo" section. Environment-specific bits (which server, which
/// database) live in <see cref="MongoEnvSettings"/> instead, loaded per-environment from
/// config/{env}.json — see <see cref="MongoConnectionFactory"/> for how the two combine.
/// </summary>
public class MongoSettings
{
    public const string SectionName = "Mongo";

    /// <summary>
    /// Connection string TEMPLATE, e.g. "mongodb://{server}:27017/?connectTimeoutMS=5000".
    /// The literal token <c>{server}</c> (if present) is substituted with the environment's
    /// <see cref="MongoEnvSettings.ServerName"/>. If the template has no <c>{server}</c>
    /// token at all, it's used as-is (e.g. a fixed local/dev connection string that doesn't
    /// vary per environment).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Database name prefix. Used to build "{DatabasePrefix}_{environmentId}" for any
    /// environment that doesn't set its own <see cref="MongoEnvSettings.DatabaseName"/>.
    /// </summary>
    public string DatabasePrefix { get; set; } = string.Empty;

    /// <summary>Name of the PerformanceTracker collection within each environment database.</summary>
    public string PerformanceTrackerCollection { get; set; } = "PerformanceTracker";

    // ── Windows/Kerberos (GSSAPI) authentication ──────────────────────────────
    // The running process's own Windows identity (WindowsIdentity.GetCurrent(), format
    // "DOMAIN\user") supplies the account name; DOMAIN itself is discarded and replaced
    // with this configured Kerberos domain/realm, since the AD domain the process happens
    // to be logged into doesn't always match the realm Mongo's GSSAPI mapping expects.

    /// <summary>
    /// Domain/realm appended to the current Windows account name to build the Kerberos
    /// (GSSAPI) principal, e.g. "user@CORP.EXAMPLE.COM". Null/empty disables Kerberos
    /// entirely — the client then connects with no credential at all (e.g. local/dev
    /// Mongo with no auth configured).
    /// </summary>
    public string? KerberosDomain { get; set; }

    // ── Timeouts ───────────────────────────────────────────────────────────────

    /// <summary>Driver's ConnectTimeout, in milliseconds. Default: 10000 (10s).</summary>
    public int ConnectionTimeoutMs { get; set; } = 10_000;

    /// <summary>Driver's ServerSelectionTimeout, in milliseconds. Default: 10000 (10s).</summary>
    public int ServerSelectionTimeoutMs { get; set; } = 10_000;

    // ── TLS ──────────────────────────────────────────────────────────────────

    public bool? TlsEnabled { get; set; }
    public string? TlsCertificatePath { get; set; }
    public string? TlsCertificatePassword { get; set; }
}

/// <summary>
/// Per-environment Mongo connection specifics — bound from config/{env}.json's "Mongo"
/// section. Everything else (connection string template, timeouts, TLS, Kerberos domain,
/// collection names) is shared across environments and comes from <see cref="MongoSettings"/>
/// (appsettings.json) instead.
/// </summary>
public class MongoEnvSettings
{
    /// <summary>Substituted for the <c>{server}</c> token in <see cref="MongoSettings.ConnectionString"/>.</summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Database name for this environment. Falls back to
    /// "{<see cref="MongoSettings.DatabasePrefix"/>}_{environmentId}" when not set.
    /// </summary>
    public string? DatabaseName { get; set; }
}
