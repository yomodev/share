namespace NxtUI.Core.Configuration;

public class UiSettings
{
    public const string SectionName = "Ui";

    /// <summary>
    /// Default debounce interval (ms) for FilterBox inputs — how long to wait after the
    /// user stops typing before firing OnFilterChanged (and the resulting Mongo/SQL/Kafka
    /// query). Individual FilterBox call sites can still override this explicitly.
    /// </summary>
    public int FilterDebounceMs { get; set; } = 500;

    /// <summary>
    /// Whether the Home page shows the Memory treemap section. Disable if memory metrics
    /// aren't wired up for an environment/deployment and the section would just be dead
    /// weight. Default: true.
    /// </summary>
    public bool ShowMemoryDashboard { get; set; } = true;

    /// <summary>
    /// Words removed from service-node and pipeline-row labels in the run-detail flow
    /// graph to save horizontal space (display only — the underlying names used for
    /// edge/topology matching are untouched). Matching is case-insensitive. The special
    /// token <c>{EnvID}</c> is replaced with the current environment id before removal.
    /// After removal, leftover doubled separators are collapsed and leading/trailing
    /// <c>-</c>/<c>_</c>/spaces are trimmed.
    /// </summary>
    public string[] GraphLabelStripWords { get; set; } = ["Pipeline", "ABC", "{EnvID}"];
}
