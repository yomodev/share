namespace NxtUI.Core.Configuration;

/// <summary>Settings for the Services page — bound from appsettings.json "Services" section.</summary>
public class ServicesPageSettings
{
    public const string SectionName = "Services";

    /// <summary>
    /// Filter text pre-populated in the Services page's filter box on load. Uses the same
    /// syntax as the filter box itself. Default: "updated:&gt;-30m" (only services heartbeating
    /// within the last 30 minutes).
    /// </summary>
    public string DefaultFilterText { get; set; } = "updated:>-30m";
}
