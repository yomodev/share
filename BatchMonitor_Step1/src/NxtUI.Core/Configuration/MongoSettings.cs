namespace NxtUI.Configuration;

/// <summary>
/// MongoDB connection settings — bound from appsettings.json "Mongo" section.
/// </summary>
public class MongoSettings
{
    public const string SectionName = "Mongo";

    /// <summary>MongoDB connection string, e.g. "mongodb://localhost:27017"</summary>
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    /// <summary>
    /// Database name prefix. The actual database name is built as
    /// "{DatabasePrefix}_{environmentId}" so each environment has its own database.
    /// </summary>
    public string DatabasePrefix { get; set; } = "BatchMonitor";

    /// <summary>Name of the PerformanceTracker collection within each environment database.</summary>
    public string PerformanceTrackerCollection { get; set; } = "PerformanceTracker";

    /// <summary>Name of the Heartbeats collection within each environment database.</summary>
    public string HeartbeatsCollection { get; set; } = "Heartbeats";

    // ── Optional credential override ─────────────────────────────────────────
    // Leave blank to use whatever is already in ConnectionString.
    // Set at runtime (e.g. from a secrets manager) to inject credentials
    // without hard-coding them in appsettings.

    public string? Username { get; set; }
    public string? Password { get; set; }

    // ── TLS ──────────────────────────────────────────────────────────────────

    public bool    TlsEnabled            { get; set; }
    public string? TlsCertificatePath    { get; set; }
    public string? TlsCertificatePassword{ get; set; }

    /// <summary>Returns the fully qualified database name for a given environment.</summary>
    public string GetDatabaseName(string environmentId) =>
        $"{DatabasePrefix}_{environmentId}";
}
