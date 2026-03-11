namespace BulkUploader.MongoDb;

/// <summary>
/// Configuration options for <see cref="MongoUploaderFactory"/>.
/// Bind from appsettings.json via:
///
/// <code>
/// services.Configure&lt;MongoUploaderOptions&gt;(
///     configuration.GetSection("BulkUploader:MongoDb"));
/// </code>
///
/// appsettings.json:
/// <code>
/// "BulkUploader": {
///   "MongoDb": {
///     "ConnectionString": "mongodb://localhost:27017",
///     "DatabaseName": "mydb",
///     "MaxConnectionPoolSize": 10,
///     "DefaultParameters": { ... },
///     "DefaultTuner": { ... }
///   }
/// }
/// </code>
/// </summary>
public sealed class MongoUploaderOptions
{
    public string ConnectionString     { get; set; } = string.Empty;

    /// <summary>
    /// Default database name used when <see cref="IMongoUploaderFactory.Create{T}"/>
    /// is called without an explicit <c>databaseName</c> override.
    /// </summary>
    public string DatabaseName         { get; set; } = string.Empty;

    public int    MaxConnectionPoolSize { get; set; } = 10;

    public MongoUploaderParameterOptions DefaultParameters { get; set; } = new();
    public MongoUploaderTunerOptions     DefaultTuner      { get; set; } = new();
}

public sealed class MongoUploaderParameterOptions
{
    public int JobChannelCapacity    { get; set; } = 256;
    public int RecordChannelCapacity { get; set; } = 500_000;
    public int BatchChannelCapacity  { get; set; } = 8;
    public int MaxRetries            { get; set; } = 3;
    public int RetryBaseDelayMs      { get; set; } = 500;
    public int IdleTimeoutMs         { get; set; } = 10_000;
    public int FlushAfterIdleMs      { get; set; } = 2_000;
}

public sealed class MongoUploaderTunerOptions
{
    public int    Initial          { get; set; } = 500;
    public int    Min              { get; set; } = 50;
    public int    Max              { get; set; } = 10_000;
    public double StepFraction     { get; set; } = 0.15;
    public double DeadBandFraction { get; set; } = 0.05;
}
