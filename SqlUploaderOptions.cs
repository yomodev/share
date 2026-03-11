namespace BulkUploader.SqlServer;

/// <summary>
/// Configuration options for <see cref="SqlUploaderFactory"/>.
/// Bind from appsettings.json via:
///
/// <code>
/// services.Configure&lt;SqlUploaderOptions&gt;(
///     configuration.GetSection("BulkUploader:SqlServer"));
/// </code>
///
/// appsettings.json:
/// <code>
/// "BulkUploader": {
///   "SqlServer": {
///     "ConnectionString": "Server=.;Database=MyDb;Trusted_Connection=True;",
///     "DefaultParameters": {
///       "MaxRetries": 3,
///       "RetryBaseDelayMs": 500,
///       "IdleTimeoutMs": 10000,
///       "FlushAfterIdleMs": 2000
///     },
///     "DefaultTuner": {
///       "Initial": 2000,
///       "Min": 100,
///       "Max": 20000
///     }
///   }
/// }
/// </code>
/// </summary>
public sealed class SqlUploaderOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Default parameters applied to every uploader created by the factory.
    /// Individual <see cref="Core.UploaderParameters"/> passed to
    /// <see cref="SqlUploaderFactory.Create{T}"/> override these on a per-table basis.
    /// </summary>
    public SqlUploaderParameterOptions DefaultParameters { get; set; } = new();

    /// <summary>Default tuner seed values for every uploader created by the factory.</summary>
    public SqlUploaderTunerOptions DefaultTuner { get; set; } = new();
}

public sealed class SqlUploaderParameterOptions
{
    public int JobChannelCapacity    { get; set; } = 256;
    public int RecordChannelCapacity { get; set; } = 500_000;
    public int BatchChannelCapacity  { get; set; } = 8;
    public int MaxRetries            { get; set; } = 3;
    public int RetryBaseDelayMs      { get; set; } = 500;
    public int IdleTimeoutMs         { get; set; } = 10_000;
    public int FlushAfterIdleMs      { get; set; } = 2_000;
}

public sealed class SqlUploaderTunerOptions
{
    public int    Initial          { get; set; } = 2_000;
    public int    Min              { get; set; } = 100;
    public int    Max              { get; set; } = 20_000;
    public double StepFraction     { get; set; } = 0.15;
    public double DeadBandFraction { get; set; } = 0.05;
}
