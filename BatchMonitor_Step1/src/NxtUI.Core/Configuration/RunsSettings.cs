namespace NxtUI.Configuration;

public class RunsSettings
{
    public const string SectionName = "Runs";

    /// <summary>How often the home page polls for new runs, in seconds. Default: 30.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>Default look-back window. Default: 4 months.</summary>
    public int WindowMonths { get; set; } = 4;

    /// <summary>Maximum rows returned per query. Default: 100.</summary>
    public int PageSize { get; set; } = 100;
}
