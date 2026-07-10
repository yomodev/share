namespace NxtUI.Core.Services;

/// <summary>
/// Reads/saves the per-environment config file shown on the Config page. See
/// <see cref="NxtUI.Core.Configuration.EnvConfigSettings"/> for path templates and save mode.
/// </summary>
public interface IEnvConfigService
{
    /// <summary>True if editing/saving is enabled and the environment has a resolvable path.</summary>
    bool IsEditingEnabled(string env);

    /// <summary>The source-of-truth file's resolved absolute path for this environment.</summary>
    string? GetSourcePath(string env);

    /// <summary>
    /// Every path a save would write to, in write order (source first). Used to populate
    /// the pre-save confirmation dialog — does not perform any I/O.
    /// </summary>
    IReadOnlyList<string> GetSaveTargets(string env);

    /// <summary>
    /// Backs up the source file (timestamped, same folder, source server only), writes
    /// the new content there, then — if the configured save mode is AllServers — copies
    /// the same content to every other resolved target. Every target is attempted even
    /// if an earlier one fails; each gets its own result.
    /// </summary>
    Task<IReadOnlyList<EnvConfigSaveResult>> SaveAsync(string env, string content, CancellationToken ct = default);

    /// <summary>
    /// Backups previously taken by <see cref="SaveAsync"/> for this environment's source
    /// file (same folder, "{filename}.{yyyyMMdd_HHmmss}.bak"), newest first.
    /// </summary>
    IReadOnlyList<EnvConfigBackup> ListBackups(string env);
}

public sealed record EnvConfigSaveResult(string Path, bool Success, string? Error);

public sealed record EnvConfigBackup(string Path, DateTime TakenAtUtc);
