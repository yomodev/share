using NxtUI.Core.Models;

namespace NxtUI.Core.Configuration;

public class SqlSettings
{
    public const string SectionName = "Sql";

    public string ConnectionString { get; set; } = "";
    public string Schema { get; set; } = "dbo";
    public string RunsTable { get; set; } = "BatchRun";

    /// <summary>
    /// Maps each RunStatus name to the values stored in the Status column. Defaults to the
    /// real values this app's SQL Server schema uses (CREATED/SUCCESS/FAILED/TERMINATED/
    /// PURGED) so status conversion works out of the box; override per-environment in
    /// config/{env}.json only if a given environment's schema uses different strings.
    /// This is the single source of truth for status conversion in both directions:
    /// filtering (RunStatus -> DB values, see StatusValuesFor) and parsing query results
    /// back into a RunStatus (DB value -> RunStatus, see ParseStatus).
    /// </summary>
    public Dictionary<string, string[]> StatusValues { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Running"] = ["CREATED"],
            ["Completed"] = ["SUCCESS"],
            ["Failed"] = ["FAILED"],
            ["Terminated"] = ["TERMINATED"],
            ["Purged"] = ["PURGED"],
        };

    // Built once from StatusValues and cached — SqlSettings instances are cached per
    // environment for the process lifetime (see EnvironmentConfigLoader), so this never
    // goes stale within a running app.
    private Dictionary<string, RunStatus>? _reverseLookup;

    /// <summary>Raw DB values configured for a given status. Empty if none are configured.</summary>
    public IReadOnlyList<string> StatusValuesFor(RunStatus status) =>
        StatusValues.TryGetValue(status.ToString(), out var values) ? values : [];

    /// <summary>
    /// Maps a raw DB status string back to a RunStatus via StatusValues (reverse lookup).
    /// Returns RunStatus.Unknown if the value isn't listed under any status.
    /// </summary>
    public RunStatus ParseStatus(string? dbValue)
    {
        if (string.IsNullOrWhiteSpace(dbValue)) return RunStatus.Unknown;

        _reverseLookup ??= BuildReverseLookup();
        return _reverseLookup.TryGetValue(dbValue.Trim(), out var status) ? status : RunStatus.Unknown;
    }

    private Dictionary<string, RunStatus> BuildReverseLookup()
    {
        var map = new Dictionary<string, RunStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var (statusName, dbValues) in StatusValues)
        {
            if (!Enum.TryParse<RunStatus>(statusName, ignoreCase: true, out var status)) continue;
            foreach (var v in dbValues)
                map[v] = status;
        }
        return map;
    }
}
