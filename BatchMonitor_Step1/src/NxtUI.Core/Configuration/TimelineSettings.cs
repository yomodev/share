namespace NxtUI.Core.Configuration;

/// <summary>Settings for the Timeline page — bound from appsettings.json "Timeline" section.</summary>
public class TimelineSettings
{
    public const string SectionName = "Timeline";

    /// <summary>
    /// Delay (ms) before the Timeline highlights same-id blocks on hover. 0 (default) =
    /// immediate. If the pointer leaves a block before this elapses, nothing highlights.
    /// Independent of <see cref="PopupDelayMs"/>.
    /// </summary>
    public int HighlightDelayMs { get; set; } = 0;

    /// <summary>
    /// Delay (ms) before the Timeline shows the hover popup/tooltip. 0 (default) =
    /// immediate. If the pointer leaves a block before this elapses, nothing shows.
    /// Independent of <see cref="HighlightDelayMs"/>.
    /// </summary>
    public int PopupDelayMs { get; set; } = 0;
}
