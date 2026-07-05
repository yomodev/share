namespace NxtUI.Configuration;

public class UiSettings
{
    public const string SectionName = "Ui";

    /// <summary>
    /// Default debounce interval (ms) for FilterBox inputs — how long to wait after the
    /// user stops typing before firing OnFilterChanged (and the resulting Mongo/SQL/Kafka
    /// query). Individual FilterBox call sites can still override this explicitly.
    /// </summary>
    public int FilterDebounceMs { get; set; } = 500;
}
