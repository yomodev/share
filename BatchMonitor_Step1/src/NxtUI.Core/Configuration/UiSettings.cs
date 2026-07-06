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
    /// Words removed from displayed service/pipeline labels to save horizontal space —
    /// used by the run-detail flow graph (node + pipeline-row labels) and the Services
    /// page card view (service name). Display only: the underlying names used for
    /// edge/topology matching and log-path discovery are untouched. Matching is
    /// case-insensitive. The special token <c>{EnvID}</c> is replaced with the current
    /// environment id before removal. After removal, leftover doubled separators are
    /// collapsed and leading/trailing <c>-</c>/<c>_</c>/spaces are trimmed.
    /// </summary>
    public string[] LabelStripWords { get; set; } = ["Pipeline", "ABC", "{EnvID}"];
}
