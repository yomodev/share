namespace NxtUI.Core.Configuration;

/// <summary>Settings for the Home page — bound from appsettings.json "Home" section.</summary>
public class HomeSettings
{
    public const string SectionName = "Home";

    /// <summary>
    /// Whether the Home page shows the Memory treemap section. Disable if memory metrics
    /// aren't wired up for an environment/deployment and the section would just be dead
    /// weight. Default: true.
    /// </summary>
    public bool ShowMemoryDashboard { get; set; } = true;

    /// <summary>
    /// Number of most-recent runs shown in the Home page's "Recent Runs" panel.
    /// Default: 20.
    /// </summary>
    public int RecentRunsCount { get; set; } = 20;
}
