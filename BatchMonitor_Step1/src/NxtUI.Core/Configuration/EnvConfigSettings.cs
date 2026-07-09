namespace NxtUI.Core.Configuration;

/// <summary>
/// Locates the per-environment config JSON file shown/edited on the Config page.
/// </summary>
public class EnvConfigSettings
{
    public const string SectionName = "EnvConfig";

    /// <summary>
    /// UNC path template for the source-of-truth copy of the file — this is what the
    /// Config page reads and edits, and where a timestamped backup is written just
    /// before any save. Expanded against the environment's source server (the first
    /// entry in that environment's <c>Servers</c> list) and the environment id.
    /// Placeholders: {server}, {env}.
    /// </summary>
    public string SourcePathTemplate { get; set; } = "";

    /// <summary>
    /// UNC path template for this same file on every server in the environment
    /// (including the source server) — expanded once per server. When editing is
    /// enabled and the save mode is AllServers, a save overwrites this resolved path
    /// on every server after the source copy's backup+write succeeds.
    /// Placeholders: {server}, {env}.
    /// </summary>
    public string PathTemplate { get; set; } = "";
}
