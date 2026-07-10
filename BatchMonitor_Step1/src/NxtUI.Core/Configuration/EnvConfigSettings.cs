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

    /// <summary>
    /// Whether the Config page allows editing/saving at all. Default: false (read-only) —
    /// this writes files on potentially every server in an environment, so it's opt-in.
    /// </summary>
    public bool EditingEnabled { get; set; } = false;

    /// <summary>
    /// Where a save is written. See <see cref="EnvConfigSaveMode"/>. Default: SingleLocation.
    /// </summary>
    public EnvConfigSaveMode SaveMode { get; set; } = EnvConfigSaveMode.SingleLocation;
}

/// <summary>Controls where a Config page save is written.</summary>
public enum EnvConfigSaveMode
{
    /// <summary>Only the source-of-truth file (<see cref="EnvConfigSettings.SourcePathTemplate"/>) is written, with a backup.</summary>
    SingleLocation,

    /// <summary>
    /// The source file is written (with a backup, same as SingleLocation), then the same
    /// content is copied to every server's <see cref="EnvConfigSettings.PathTemplate"/>
    /// path — no backup is taken for those mirror copies.
    /// </summary>
    AllServers,
}
