using System.Security.Cryptography;
using System.Text;

namespace NxtUI.Core.Configuration;

/// <summary>
/// MongoDB connection settings — bound from appsettings.json "Mongo" section.
/// </summary>
public class MongoSettings
{
    public const string SectionName = "Mongo";

    /// <summary>MongoDB connection string, e.g. "mongodb://localhost:27017"</summary>
    public string? ConnectionString { get; set; }

    public string? ServerName { get; set; }

    public string? DatabaseName { get; set; }

    /// <summary>
    /// Database name prefix. The actual database name is built as
    /// "{DatabasePrefix}_{environmentId}" so each environment has its own database.
    /// </summary>
    public string DatabasePrefix { get; set; } = string.Empty;

    /// <summary>Name of the PerformanceTracker collection within each environment database.</summary>
    public string PerformanceTrackerCollection { get; set; } = "PerformanceTracker";

    /// <summary>Name of the Heartbeats collection within each environment database.</summary>
    public string? HeartbeatsCollection { get; set; }

    // ── Optional credential override ─────────────────────────────────────────
    // Leave blank to use whatever is already in ConnectionString.
    // Set at runtime (e.g. from a secrets manager) to inject credentials
    // without hard-coding them in appsettings.

    public string? Username { get; set; }
    public string? Password { get; set; }

    // ── TLS ──────────────────────────────────────────────────────────────────

    public bool? TlsEnabled { get; set; }
    public string? TlsCertificatePath { get; set; }
    public string? TlsCertificatePassword { get; set; }

    public string GetFingerprint()
    {
        var key = $"{ConnectionString}|{Username}|{TlsEnabled}|{TlsCertificatePath}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..12];
    }
}
