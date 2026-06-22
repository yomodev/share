namespace NxtUI.Configuration;

/// <summary>
/// Test-only: drives a background service that fabricates metrics log files in
/// folders matching the LogPaths templates, so log discovery + monitoring can be
/// exercised without real services. Keep Enabled=false in production.
/// </summary>
public class TestLogGeneratorSettings
{
    public const string SectionName = "TestLogGenerator";

    public bool Enabled { get; set; } = false;

    /// <summary>Seconds between appended metrics lines.</summary>
    public int WriteIntervalSeconds { get; set; } = 15;

    /// <summary>How many lines to backfill immediately when seeding a file.</summary>
    public int InitialLineCount { get; set; } = 5;

    /// <summary>Which LogPaths template to materialise folders from.</summary>
    public int TemplateIndex { get; set; } = 0;
}
