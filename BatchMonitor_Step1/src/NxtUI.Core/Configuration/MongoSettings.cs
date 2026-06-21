namespace BatchMonitor.Configuration;

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

    /// <summary>Returns the fully qualified database name for a given environment.</summary>
    public string GetDatabaseName(string environmentId) =>
        $"{DatabasePrefix}_{environmentId}";
}
