namespace NxtUI.Configuration;

public class SqlSettings
{
    public const string SectionName = "Sql";

    public string ConnectionString { get; set; } = "";
    public string Schema           { get; set; } = "dbo";
    public string RunsTable        { get; set; } = "BatchRun";

    /// <summary>
    /// Maps each RunStatus name to the values stored in the Status column.
    /// E.g. single-char codes: { "Running": ["R"], "Completed": ["C", "DONE"], "Failed": ["F"] }
    /// Defaults assume the column stores the full enum name (Running / Completed / Failed).
    /// </summary>
    public Dictionary<string, string[]> StatusValues { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Running"]   = ["Running"],
            ["Completed"] = ["Completed"],
            ["Failed"]    = ["Failed"],
        };
}
